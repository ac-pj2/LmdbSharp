// Data + config models for the ConfigViews PoC.
//
// EntityRecord is the render-side shape of an entity: a flat field dictionary
// plus common metadata. It mirrors p2's storage model (JSONB FormData keyed by
// entity-type slug). `Key` is the public identity used everywhere in views —
// a GUID when backed by p2's PostgreSQL, a number when backed by LMDB.
using MemoryPack;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConfigViews;

[MemoryPackable]
public partial class EntityRecord
{
    /// <summary>LMDB auto-id (unused in p2 mode — Key is the identity).</summary>
    public long Id { get; set; }
    public string Key { get; set; } = "";
    public string ParentKey { get; set; } = "";      // comments hang off a parent
    public string EntityType { get; set; } = "";     // slug, e.g. "forum-thread"
    public string Ref { get; set; } = "";            // e.g. THRD-0007
    public string AuthorName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public Dictionary<string, string> Fields { get; set; } = new();

    public string F(string name) => Fields.GetValueOrDefault(name, "");
    public bool Flag(string name) => Fields.GetValueOrDefault(name) == "true";
}

/// <summary>The data seam. LmdbEntityStore is self-contained (demo mode);
/// P2EntityStore reads the platform's PostgreSQL and writes through its REST
/// API so every write flows through the real pipeline (validation, triggers,
/// mutation broadcast).</summary>
public interface IEntityStore
{
    List<EntityRecord> LoadAll();
    EntityRecord CreateEntity(string entityType, string author, Dictionary<string, string> fields);
    EntityRecord CreateReply(string parentKey, string body, string author);
    /// <summary>Fresh copies of specific entities (mutation-bridge refresh).</summary>
    List<EntityRecord> FetchByKeys(IReadOnlyCollection<string> keys);
}

/// <summary>The projection every session mounts from — the read model kept
/// fresh by local writes and (in p2 mode) the mutation-stream bridge.</summary>
public interface IRecordProjection
{
    void Upsert(EntityRecord rec);
    void Remove(string key);
    List<EntityRecord> Snapshot();
    void Fill(IEnumerable<EntityRecord> records);
    string Describe();   // for the DevPanel
}

/// <summary>In-memory projection (self-contained LMDB store mode).</summary>
public sealed class RecordCache : IRecordProjection
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, EntityRecord> _records = new();

    public void Upsert(EntityRecord rec) => _records[rec.Key] = rec;
    public void Remove(string key) => _records.TryRemove(key, out _);
    public List<EntityRecord> Snapshot() => _records.Values.ToList();
    public void Fill(IEnumerable<EntityRecord> records)
    {
        foreach (var r in records) _records[r.Key] = r;
    }
    public string Describe() => $"in-memory · {_records.Count} records";
}

// ── Config models: the exact shapes of p2's views/*.json + entity-types/*.json ──

public sealed class ViewDefinition
{
    public string Name { get; set; } = "";
    public string Route { get; set; } = "";
    public Dictionary<string, DataBinding>? DataBindings { get; set; }
    public ViewNode Layout { get; set; } = new();
}

public sealed class DataBinding
{
    public string Source { get; set; } = "api";
    public string Endpoint { get; set; } = "";
}

[JsonConverter(typeof(ViewNodeConverter))]
public sealed class ViewNode
{
    public string Component { get; set; } = "";
    public string? VisibleWhen { get; set; }
    public JsonElement? Props { get; set; }
    public List<ViewNode> Children { get; set; } = new();

    public string PropStr(string name, string fallback = "")
        => Props?.TryGetProperty(name, out var v) == true && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback : fallback;

    public JsonElement? Prop(string name)
        => Props?.TryGetProperty(name, out var v) == true ? v : null;
}

public sealed class EntityTypeConfig
{
    public string Slug { get; set; } = "";           // filename
    public string Name { get; set; } = "";           // display name (list views reference this too)
    public string ReferencePrefix { get; set; } = "";
    public string TitleField { get; set; } = "";
    public List<FieldConfig> Fields { get; set; } = new();

    public FieldConfig? Field(string name) => Fields.FirstOrDefault(f => f.Name == name);
}

public sealed class FieldConfig
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "text";       // text|richtext|boolean|reference|...
    public string Label { get; set; } = "";
    public bool Required { get; set; }
    public string? ReferenceType { get; set; }
}

/// <summary>Handles p2's two children shapes: a node object, or an ARRAY of
/// nodes (Columns uses one array per column). Array children become synthetic
/// "__group" nodes so the tree stays uniform.</summary>
public sealed class ViewNodeConverter : System.Text.Json.Serialization.JsonConverter<ViewNode>
{
    public override ViewNode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        return FromElement(doc.RootElement);
    }

    private static ViewNode FromElement(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Array)
        {
            var group = new ViewNode { Component = "__group" };
            foreach (var child in el.EnumerateArray())
                group.Children.Add(FromElement(child));
            return group;
        }

        var node = new ViewNode();
        foreach (var prop in el.EnumerateObject())
        {
            switch (prop.Name.ToLowerInvariant())
            {
                case "component": node.Component = prop.Value.GetString() ?? ""; break;
                case "visiblewhen": node.VisibleWhen = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null; break;
                case "props": node.Props = prop.Value.Clone(); break;
                case "children":
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                        foreach (var child in prop.Value.EnumerateArray())
                            node.Children.Add(FromElement(child));
                    break;
            }
        }
        return node;
    }

    public override void Write(Utf8JsonWriter writer, ViewNode value, JsonSerializerOptions options)
        => throw new NotSupportedException();
}

public sealed class NavItem
{
    public string Label { get; set; } = "";
    public string Route { get; set; } = "";
    public string? Icon { get; set; }
    public string? VisibleWhen { get; set; }
}

/// <summary>All config for one system, loaded straight from the p2 config
/// directory — the same files its React renderer consumes.</summary>
public sealed class SystemConfigSet
{
    public List<ViewDefinition> Views { get; } = new();
    public List<NavItem> Navigation { get; } = new();
    public Dictionary<string, EntityTypeConfig> EntityTypes { get; } = new(); // by slug

    /// <summary>Views reference entity types by slug OR display name.</summary>
    public EntityTypeConfig? ResolveEntityType(string slugOrName)
        => EntityTypes.TryGetValue(slugOrName, out var byS) ? byS
         : EntityTypes.Values.FirstOrDefault(t => t.Name.Equals(slugOrName, StringComparison.OrdinalIgnoreCase));
}
