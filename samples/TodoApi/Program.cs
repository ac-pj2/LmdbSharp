using Lmdb.AspNetCore;
using Lmdb.Objects;
using TodoApi;

var builder = WebApplication.CreateBuilder(args);

// Register the object database + collections via DI
builder.Services.AddLmdbObjectDatabase(builder.Configuration["TodoDbPath"] ?? "./tododata");
builder.Services.AddCollection<Todo>("articles");

var app = builder.Build();

// ── REST API ──
// Inject Collection<Todo> directly — registered as a singleton by AddCollection.

app.MapGet("/todos", (Collection<Todo> todos, int? limit) =>
{
    using var txn = todos.Database.BeginRead();
    return Results.Ok(todos.Query(txn).Take(limit ?? 100).ToList());
});

app.MapGet("/todos/{id:long}", (Collection<Todo> todos, long id) =>
{
    using var txn = todos.Database.BeginRead();
    var todo = todos.Get(txn, id);
    return todo != null ? Results.Ok(todo) : Results.NotFound();
});

app.MapPost("/todos", (Collection<Todo> todos, Todo todo) =>
{
    using var txn = todos.Database.BeginWrite();
    todos.Insert(txn, todo);
    txn.Commit();
    return Results.Created($"/todos/{todo.Id}", new { id = todo.Id });
});

app.MapPut("/todos/{id:long}", (Collection<Todo> todos, long id, Todo todo) =>
{
    todo.Id = id;
    using var txn = todos.Database.BeginWrite();
    todos.Update(txn, todo);
    txn.Commit();
    return Results.Ok(todo);
});

app.MapDelete("/todos/{id:long}", (Collection<Todo> todos, long id) =>
{
    using var txn = todos.Database.BeginWrite();
    bool ok = todos.Delete(txn, id);
    txn.Commit();
    return ok ? Results.NoContent() : Results.NotFound();
});

app.Run();
