// ASP.NET Core DI integration for the LMDB object database.
//
// Usage in Program.cs:
//   builder.Services.AddLmdbObjectDatabase("./mydata");
//   builder.Services.AddCollection<User>("users");
//
// Then inject Collection<User> into controllers:
//   public class UserController(Collection<User> users) { ... }
using Lmdb;
using Lmdb.Objects;
using Microsoft.Extensions.DependencyInjection;

namespace Lmdb.AspNetCore;

public static class ServiceCollectionExtensions
{
    /// <summary>Register the LMDB object database as a singleton service.</summary>
    public static IServiceCollection AddLmdbObjectDatabase(this IServiceCollection services,
        string path, Action<ObjectDatabaseOptions>? configure = null)
    {
        var options = new ObjectDatabaseOptions();
        configure?.Invoke(options);

        services.AddSingleton<ObjectDatabase>(_ => ObjectDatabase.Open(path, options));
        services.AddSingleton<AsyncObjectDatabase>(sp =>
            new AsyncObjectDatabase(sp.GetRequiredService<ObjectDatabase>()));
        return services;
    }

    /// <summary>Register a typed collection so it can be injected directly.</summary>
    public static IServiceCollection AddCollection<T>(this IServiceCollection services,
        string name, Action<CollectionOptions<T>>? configure = null) where T : class
    {
        services.AddSingleton<Collection<T>>(sp =>
        {
            var db = sp.GetRequiredService<ObjectDatabase>();
            var opts = new CollectionOptions<T>();
            configure?.Invoke(opts);
            return db.GetCollection(name, opts);
        });
        return services;
    }
}
