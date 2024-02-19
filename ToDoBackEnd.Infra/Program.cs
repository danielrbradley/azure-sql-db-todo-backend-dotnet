using Pulumi;
using Pulumi.AzureNative.Resources;
using Pulumi.AzureNative.Storage;
using StorageInputs = Pulumi.AzureNative.Storage.Inputs;
using Pulumi.AzureNative.Sql;
using Pulumi.AzureNative.Web;
using WebInputs = Pulumi.AzureNative.Web.Inputs;
using Pulumi.Command.Local;
using System.Collections.Generic;

return await Pulumi.Deployment.RunAsync(async () =>
{
    // Create an Azure Resource Group
    var resourceGroup = new ResourceGroup("resourceGroup",
        new ResourceGroupArgs
        {
            Location = "WestEurope"
        });

    var storageAccount = new StorageAccount("sa", new StorageAccountArgs
    {
        ResourceGroupName = resourceGroup.Name,
        Kind = "StorageV2",
        Sku = new StorageInputs.SkuArgs
        {
            Name = SkuName.Standard_LRS,
        },
    });

    var container = new BlobContainer("zips", new BlobContainerArgs
    {
        AccountName = storageAccount.Name,
        PublicAccess = PublicAccess.None,
        ResourceGroupName = resourceGroup.Name,
    });

    var buildApi = await Run.InvokeAsync(new RunArgs
    {
        Command = "dotnet publish -c Debug",
        Dir = "../ToDoBackEnd.API",
    });

    var blob = new Blob("appservice-blob", new BlobArgs
    {
        ResourceGroupName = resourceGroup.Name,
        AccountName = storageAccount.Name,
        ContainerName = container.Name,
        Type = BlobType.Block,
        BlobName = "ToDoBackEnd.API.zip",
        Source = new FileArchive("../ToDoBackEnd.API/bin/Debug/netcoreapp3.1/publish"),
    });

    var codeBlobUrl = SignedBlobReadUrl(blob, container, storageAccount, resourceGroup);

    var password = new Pulumi.Random.RandomPassword("password", new Pulumi.Random.RandomPasswordArgs
    {
        Length = 16,
        Special = true
    });

    var sqlServer = new Server("sqlServer", new ServerArgs
    {
        ResourceGroupName = resourceGroup.Name,
        Location = resourceGroup.Location,
        AdministratorLogin = "pulumi",
        AdministratorLoginPassword = password.Result,
        Version = "12.0",
        PublicNetworkAccess = "Enabled",
    });

    var sqlDb = new Database("sqlDb", new DatabaseArgs
    {
        ResourceGroupName = resourceGroup.Name,
        ServerName = sqlServer.Name,
    });

    var connectionString = Output.Format($"Server=tcp:{sqlServer.FullyQualifiedDomainName},1433;Initial Catalog={sqlDb.Name};Persist Security Info=False;User ID=pulumi;Password={password.Result};MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;");

    var insightsWorkspace = new Pulumi.AzureNative.OperationalInsights.Workspace("insightsWorkspace", new Pulumi.AzureNative.OperationalInsights.WorkspaceArgs
    {
        ResourceGroupName = resourceGroup.Name,
        Sku = new Pulumi.AzureNative.OperationalInsights.Inputs.WorkspaceSkuArgs
        {
            Name = "PerGB2018",
        },
    });

    var appInsights = new Pulumi.AzureNative.Insights.Component("appInsights", new Pulumi.AzureNative.Insights.ComponentArgs
    {
        ApplicationType = "web",
        Kind = "web",
        ResourceGroupName = resourceGroup.Name,
        WorkspaceResourceId = insightsWorkspace.Id,
    });

    // Create an Azure App Service Plan with B1 SKU
    var appServicePlan = new AppServicePlan("appServicePlan",
        new AppServicePlanArgs
        {
            ResourceGroupName = resourceGroup.Name,
            Kind = "App",
            Sku = new WebInputs.SkuDescriptionArgs
            {
                Name = "B1",
                Tier = "Basic",
            },
        }
    );

    // Create an Azure Web App
    var app = new WebApp("webApp",
        new WebAppArgs
        {
            ResourceGroupName = resourceGroup.Name,
            ServerFarmId = appServicePlan.Id,
            SiteConfig = new WebInputs.SiteConfigArgs() // Additional configurations can be set here
            {
                AppSettings = new[]
                {
                    new WebInputs.NameValuePairArgs
                    {
                        Name = "WEBSITE_RUN_FROM_PACKAGE",
                        Value = codeBlobUrl,
                    },
                    new WebInputs.NameValuePairArgs
                    {
                        Name = "ASPNETCORE_ENVIRONMENT",
                        Value = "Development",
                    },
                    new WebInputs.NameValuePairArgs{
                        Name = "APPINSIGHTS_INSTRUMENTATIONKEY",
                        Value = appInsights.InstrumentationKey
                    },
                    new WebInputs.NameValuePairArgs{
                        Name = "APPLICATIONINSIGHTS_CONNECTION_STRING",
                        Value = appInsights.InstrumentationKey.Apply(key => $"InstrumentationKey={key}"),
                    },
                    new WebInputs.NameValuePairArgs{
                        Name = "ApplicationInsightsAgent_EXTENSION_VERSION",
                        Value = "~2",
                    },
                },
                ConnectionStrings = new[]
                {
                    new WebInputs.ConnStringInfoArgs
                    {
                        Name = "ReadWriteConnection",
                        Type = ConnectionStringType.SQLAzure,
                        ConnectionString = connectionString,

                    },
                    new WebInputs.ConnStringInfoArgs
                    {
                        Name = "ReadOnlyConnection",
                        Type = ConnectionStringType.SQLAzure,
                        ConnectionString = connectionString,
                    },
                }
            }
        }
    );

    app.OutboundIpAddresses.Apply(ips =>
    {
        foreach (var ip in ips.Split(","))
        {
            var firewallRule = new FirewallRule(ip, new FirewallRuleArgs
            {
                ResourceGroupName = resourceGroup.Name,
                ServerName = sqlServer.Name,
                StartIpAddress = ip,
                EndIpAddress = ip,
            });
        }
        return ips;
    });

    var myIp = await Run.InvokeAsync(new RunArgs
    {
        Command = "curl ifconfig.me",
    });

    var firewallRule = new FirewallRule("localIp", new FirewallRuleArgs
    {
        ResourceGroupName = resourceGroup.Name,
        ServerName = sqlServer.Name,
        StartIpAddress = myIp.Stdout,
        EndIpAddress = myIp.Stdout,
    });

    var dbDeploy = new Command("dbDeploy", new CommandArgs
    {
        Create = "dotnet run",
        Dir = "../ToDoBackEnd.Deploy",
        Environment = new InputMap<string>
        {
            { "ConnectionString", connectionString },
            { "BackEndUserPassword", password.Result },
            { "GITHUB_REF", "main" },
        },
    });

    // Export the Web App endpoint
    return new Dictionary<string, object?>
    {
        ["Endpoint"] = app.DefaultHostName.Apply(hostname => $"https://{hostname}/")
    };
});

static Output<string> SignedBlobReadUrl(Blob blob, BlobContainer container, StorageAccount account, ResourceGroup resourceGroup)
{
    var serviceSasToken = ListStorageAccountServiceSAS.Invoke(new ListStorageAccountServiceSASInvokeArgs
    {
        AccountName = account.Name,
        Protocols = HttpProtocol.Https,
        SharedAccessStartTime = "2021-01-01",
        SharedAccessExpiryTime = "2030-01-01",
        Resource = SignedResource.C,
        ResourceGroupName = resourceGroup.Name,
        Permissions = Permissions.R,
        CanonicalizedResource = Output.Format($"/blob/{account.Name}/{container.Name}"),
        ContentType = "application/json",
        CacheControl = "max-age=5",
        ContentDisposition = "inline",
        ContentEncoding = "deflate",
    }).Apply(blobSAS => blobSAS.ServiceSasToken);

    return Output.Format($"https://{account.Name}.blob.core.windows.net/{container.Name}/{blob.Name}?{serviceSasToken}");
}
