// P2EntityStore: the Phase-1 adapter onto the real p2 platform.
//
//   READS  — direct PostgreSQL (Npgsql) against the live Entities/Comments/
//            Users tables: the render path wants bulk, denormalized,
//            millisecond loads (this is where an LMDB projection slots in
//            at Phase 2 — same shape, fed by the mutation bridge).
//   WRITES — through p2's REST API with a service-account JWT, so every
//            create runs the platform's real pipeline: config validation,
//            reference-number generation, computedValue cascades, triggers,
//            audit, and the mutation broadcast (which loops back to us via
//            the MutationBridge and patches every LiveView session).
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace ConfigViews;

public sealed class P2Options
{
    public string ApiBase { get; set; } = "http://127.0.0.1:5211";
    public string ConnectionString { get; set; } =
        "Host=127.0.0.1;Port=5432;Database=workflow_system;Username=postgres;Password=postgres_dev_password;Maximum Pool Size=4;Connection Idle Lifetime=15";
    public string SystemSlug { get; set; } = "coaching-hub";
    public string[] EntityTypes { get; set; } = { "forum-thread", "forum-category", "article" };
    // Dev service account (matches p2's seeded dev admin). A production
    // integration passes each user's own JWT via the configure hook instead.
    public string Email { get; set; } = "admin@test.com";
    public string Password { get; set; } = "DevPass123!";
}

public sealed class P2EntityStore : IEntityStore
{
    private readonly P2Options _opt;
    private readonly HttpClient _http = new();
    private string? _token;
    private readonly object _tokenGate = new();

    public P2EntityStore(P2Options opt) => _opt = opt;

    // ── reads: direct SQL ──

    public List<EntityRecord> LoadAll()
    {
        using var conn = new NpgsqlConnection(_opt.ConnectionString);
        conn.Open();
        var records = new List<EntityRecord>();
        var users = LoadUserNames(conn);

        using (var cmd = new NpgsqlCommand("""
            SELECT "Id", "EntityTypeSlug", "ReferenceNumber", "FormData", "CreatedBy", "CreatedAt"
            FROM "Entities"
            WHERE "SystemSlug" = @sys AND "EntityTypeSlug" = ANY(@types) AND NOT "IsDeleted"
            ORDER BY "CreatedAt"
            """, conn))
        {
            cmd.Parameters.AddWithValue("sys", _opt.SystemSlug);
            cmd.Parameters.AddWithValue("types", _opt.EntityTypes);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                records.Add(MapEntity(reader, users));
        }

        // Replies: p2's real comments subsystem, attached to our entities.
        using (var cmd = new NpgsqlCommand("""
            SELECT c."Id", c."EntityId", c."Content", c."CreatedBy", c."CreatedAt"
            FROM "Comments" c
            JOIN "Entities" e ON e."Id" = c."EntityId"
            WHERE e."SystemSlug" = @sys AND e."EntityTypeSlug" = ANY(@types)
              AND NOT c."IsDeleted" AND NOT e."IsDeleted"
            ORDER BY c."CreatedAt"
            """, conn))
        {
            cmd.Parameters.AddWithValue("sys", _opt.SystemSlug);
            cmd.Parameters.AddWithValue("types", _opt.EntityTypes);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                records.Add(new EntityRecord
                {
                    Key = reader.GetGuid(0).ToString(),
                    ParentKey = reader.GetGuid(1).ToString(),
                    EntityType = "comment",
                    Fields = new() { ["body"] = reader.GetString(2) },
                    AuthorName = users.GetValueOrDefault(reader.GetGuid(3), "member"),
                    CreatedAt = reader.GetDateTime(4),
                });
        }

        return records;
    }

    public List<EntityRecord> FetchByKeys(IReadOnlyCollection<string> keys)
    {
        var ids = keys.Select(k => Guid.TryParse(k, out var g) ? g : Guid.Empty)
                      .Where(g => g != Guid.Empty).ToArray();
        if (ids.Length == 0) return new();

        using var conn = new NpgsqlConnection(_opt.ConnectionString);
        conn.Open();
        var users = LoadUserNames(conn);
        using var cmd = new NpgsqlCommand("""
            SELECT "Id", "EntityTypeSlug", "ReferenceNumber", "FormData", "CreatedBy", "CreatedAt"
            FROM "Entities"
            WHERE "Id" = ANY(@ids) AND NOT "IsDeleted"
            """, conn);
        cmd.Parameters.AddWithValue("ids", ids);
        using var reader = cmd.ExecuteReader();
        var result = new List<EntityRecord>();
        while (reader.Read())
            result.Add(MapEntity(reader, users));
        return result;
    }

    private static EntityRecord MapEntity(NpgsqlDataReader reader, Dictionary<Guid, string> users)
    {
        var fields = new Dictionary<string, string>();
        using (var doc = JsonDocument.Parse(reader.GetString(3)))
            foreach (var prop in doc.RootElement.EnumerateObject())
                fields[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? "",
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null => "",
                    _ => prop.Value.GetRawText(),
                };

        return new EntityRecord
        {
            Key = reader.GetGuid(0).ToString(),
            EntityType = reader.GetString(1),
            Ref = reader.GetString(2),
            Fields = fields,
            AuthorName = users.GetValueOrDefault(reader.GetGuid(4), "member"),
            CreatedAt = reader.GetDateTime(5),
        };
    }

    private static Dictionary<Guid, string> LoadUserNames(NpgsqlConnection conn)
    {
        var users = new Dictionary<Guid, string>();
        using var cmd = new NpgsqlCommand("""
            SELECT "Id", COALESCE(NULLIF(TRIM(CONCAT("FirstName", ' ', "LastName")), ''), "Email")
            FROM "Users"
            """, conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            users[reader.GetGuid(0)] = reader.GetString(1);
        return users;
    }

    // ── writes: through the p2 REST API (the real pipeline) ──

    /// <summary>Authenticate a real platform user; returns (token, displayName,
    /// isAdmin) for session-scoped writes and expression context.</summary>
    public (string Token, string Name, bool IsAdmin)? Login(string email, string password)
    {
        var res = _http.PostAsJsonAsync(_opt.ApiBase + "/api/auth/login",
            new { email, password }).GetAwaiter().GetResult();
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(res.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        var data = doc.RootElement.GetProperty("data");
        var user = data.GetProperty("user");
        var name = $"{user.GetProperty("firstName").GetString()} {user.GetProperty("lastName").GetString()}".Trim();
        return (data.GetProperty("token").GetString()!,
                name == "" ? user.GetProperty("email").GetString()! : name,
                user.TryGetProperty("isSystemAdmin", out var a) && a.GetBoolean());
    }

    public EntityRecord CreateEntity(string entityType, string author, Dictionary<string, string> fields, string? bearer = null)
    {
        var response = Post("/api/entities", new
        {
            entityTypeSlug = entityType,
            systemSlug = _opt.SystemSlug,
            formData = fields.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
        }, bearer);

        var id = response.GetProperty("id").GetString()!;
        return FetchByKeys(new[] { id }).FirstOrDefault()
               ?? throw new InvalidOperationException($"created entity {id} not readable back");
    }

    public EntityRecord CreateReply(string parentKey, string body, string author, string? bearer = null)
    {
        var response = Post("/api/comments", new
        {
            entityId = parentKey,
            content = body,
        }, bearer);

        return new EntityRecord
        {
            Key = response.GetProperty("id").GetString() ?? Guid.NewGuid().ToString(),
            ParentKey = parentKey,
            EntityType = "comment",
            Fields = new() { ["body"] = body },
            AuthorName = response.TryGetProperty("createdByName", out var n)
                ? n.GetString() ?? author : author,
            CreatedAt = DateTime.UtcNow,
        };
    }

    private JsonElement Post(string path, object body, string? bearer = null)
    {
        for (int attempt = 0; ; attempt++)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, _opt.ApiBase + path)
            { Content = JsonContent.Create(body) };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer ?? Token());
            req.Headers.Add("X-System-Slug", _opt.SystemSlug);

            var res = _http.Send(req);
            if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized && attempt == 0)
            {
                lock (_tokenGate) _token = null; // expired — login again once
                continue;
            }
            var text = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"p2 API {path} → {(int)res.StatusCode}: {text[..Math.Min(300, text.Length)]}");
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.GetProperty("data").Clone();
        }
    }

    internal string Token()
    {
        lock (_tokenGate)
        {
            if (_token != null) return _token;
            var res = _http.PostAsJsonAsync(_opt.ApiBase + "/api/auth/login",
                new { email = _opt.Email, password = _opt.Password }).GetAwaiter().GetResult();
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(res.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            var data = doc.RootElement.GetProperty("data");
            _token = (data.TryGetProperty("token", out var t) ? t.GetString()
                    : data.GetProperty("accessToken").GetString())!;
            return _token;
        }
    }
}
