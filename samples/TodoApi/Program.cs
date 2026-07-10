using Lmdb.Objects;
using TodoApi;

var builder = WebApplication.CreateBuilder(args);

// Register the TodoService as a singleton (one DB per process — LMDB single-writer)
builder.Services.AddSingleton<TodoService>(_ =>
    new TodoService(builder.Configuration["TodoDbPath"] ?? "./tododata"));

var app = builder.Build();

// ── REST API ──

app.MapGet("/todos", (TodoService svc, int? limit) =>
    Results.Ok(svc.GetAll(limit ?? 100)));

app.MapGet("/todos/{id:long}", (TodoService svc, long id) =>
{
    var todo = svc.GetTodo(id);
    return todo != null ? Results.Ok(todo) : Results.NotFound();
});

app.MapPost("/todos", (TodoService svc, Todo todo) =>
{
    var id = svc.CreateTodo(todo.Title, todo.Priority, todo.DueDate, todo.Category);
    return Results.Created($"/todos/{id}", new { id });
});

app.MapPut("/todos/{id:long}", (TodoService svc, long id, Todo todo) =>
{
    todo.Id = id;
    return svc.UpdateTodo(todo) ? Results.Ok(todo) : Results.NotFound();
});

app.MapDelete("/todos/{id:long}", (TodoService svc, long id) =>
    svc.DeleteTodo(id) ? Results.NoContent() : Results.NotFound());

app.MapPost("/todos/{id:long}/complete", (TodoService svc, long id) =>
    svc.MarkComplete(id) ? Results.Ok() : Results.NotFound());

// ── Query endpoints ──

app.MapGet("/todos/pending", (TodoService svc) =>
    Results.Ok(svc.GetPending()));

app.MapGet("/todos/category/{cat}", (TodoService svc, string cat) =>
    Results.Ok(svc.GetByCategory(cat)));

app.MapGet("/todos/high-priority", (TodoService svc) =>
    Results.Ok(svc.GetHighPriority()));

app.MapGet("/todos/due/{date:datetime}", (TodoService svc, DateTime date) =>
    Results.Ok(svc.GetDueSoon(date)));

app.MapGet("/todos/search", (TodoService svc, string q) =>
    Results.Ok(svc.Search(q)));

app.MapGet("/todos/count", (TodoService svc) =>
    Results.Ok(new { count = svc.Count() }));

app.Run();
