create schema [web];
go

create user [todo-backend] with password = '$BackEndUserPassword$' -- substituted with reall password by DbUp
go

grant execute on schema::[web] to [todo-backend]
go

create sequence dbo.[global_id]
as int
start with 1
increment by 1;
go

create table dbo.todos
(
	id int not null primary key default (next value for [global_id]),
	todo nvarchar(100) not null,
	completed tinyint not null default (0)
)
go
