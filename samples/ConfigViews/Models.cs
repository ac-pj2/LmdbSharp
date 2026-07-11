// Data + config models for the ConfigViews PoC.
//
// EntityRecord is the PoC's generic entity: the platform this demonstrates
// against (p2) stores entities as JSONB FormData keyed by entity-type slug;
// we mirror that shape in LMDB — a flat field dictionary plus common metadata.
using MemoryPack;
using System.Text.Json;

namespace ConfigViews;

[MemoryPackable]
public partial class EntityRecord
{
    public long Id { get; set; }
    public string EntityType { get; set; } = "";     // slug, e.g. "forum-thread"
    public string Ref { get; set; } = "";            // e.g. THRD-0007
    public long ParentId { get; set; }               // comments hang off a parent
    public string AuthorName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public Dictionary<string, string> Fields { get; set; } = new();

    public string F(string name) => Fields.GetValueOrDefault(name, "");
    public bool Flag(string name) => Fields.GetValueOrDefault(name) == "true";
}

// ── Config models: the exact shapes of p2's views/*.json + entity-types/*.json ──

public sealed class ViewDefinition
{
    public string Name { get; set; } = "";
    public string Route { get; set; } = "";
    public ViewNode Layout { get; set; } = new();
}

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

/// <summary>All config for one system, loaded straight from the p2 config
/// directory — the same files its React renderer consumes.</summary>
public sealed class SystemConfigSet
{
    public List<ViewDefinition> Views { get; } = new();
    public Dictionary<string, EntityTypeConfig> EntityTypes { get; } = new(); // by slug

    /// <summary>Views reference entity types by slug OR display name.</summary>
    public EntityTypeConfig? ResolveEntityType(string slugOrName)
        => EntityTypes.TryGetValue(slugOrName, out var byS) ? byS
         : EntityTypes.Values.FirstOrDefault(t => t.Name.Equals(slugOrName, StringComparison.OrdinalIgnoreCase));
}
