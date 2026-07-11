// DefaultViews: the platform's fallback cascade, PoC-sized. When no explicit
// view matches a route, generate the default list / detail / create layout
// from the entity-type config — exactly how p2 serves entity types that ship
// no views/*.json (its SystemViewRouter falls back to generated layouts).
using System.Text.Json;

namespace ConfigViews;

public static class DefaultViews
{
    public static (ViewDefinition View, Dictionary<string, string> Params)? Resolve(
        SystemConfigSet config, string path)
    {
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length is 0 or > 2) return null;

        // "/articles" → entity type "article" (plural convention, like the platform's nav).
        var et = config.EntityTypes.Values.FirstOrDefault(t => ConfigLiveView.Pluralize(t.Slug) == segs[0]);
        if (et == null) return null;

        if (segs.Length == 1)
            return (GeneratedList(et), new Dictionary<string, string>());
        if (segs[1] == "new")
            return (GeneratedCreate(et), new Dictionary<string, string>());
        return (GeneratedDetail(et), new Dictionary<string, string> { ["id"] = segs[1] });
    }

    /// <summary>First few meaningful fields make the default table columns.</summary>
    public static List<string> DefaultColumns(EntityTypeConfig et)
    {
        var cols = new List<string> { "referenceNumber" };
        cols.AddRange(et.Fields
            .Where(f => f.Type is not ("richtext" or "image") && f.Name != "slug")
            .Take(4).Select(f => f.Name));
        return cols;
    }

    private static ViewDefinition GeneratedList(EntityTypeConfig et) => new()
    {
        Name = et.Name,
        Route = $"/{ConfigLiveView.Pluralize(et.Slug)}",
        Layout = Node("Page", new { title = et.Name + "s" },
            Node("Text", new { variant = "p", content = $"All {et.Name.ToLowerInvariant()}s (generated default layout — no explicit view configured)." }),
            Node("Button", new
            {
                label = $"New {et.Name.ToLowerInvariant()}",
                variant = "primary",
                onClick = new { action = "navigate", to = $"/{ConfigLiveView.Pluralize(et.Slug)}/new" },
            }, visibleWhen: "user != null && user.id != null"),
            Node("EntityList", new
            {
                entityType = et.Slug,
                columns = DefaultColumns(et),
                sortable = true,
                searchable = true,
            })),
    };

    private static ViewDefinition GeneratedDetail(EntityTypeConfig et) => new()
    {
        Name = et.Name,
        Route = $"/{ConfigLiveView.Pluralize(et.Slug)}/:id",
        Layout = Node("Page", new { title = et.Name },
            Node("EntityDetail", new
            {
                entityType = et.Slug,
                titleField = et.TitleField is { Length: > 0 } ? et.TitleField : "title",
                bodyField = "body",
                displayFields = et.Fields
                    .Where(f => f.Type is not ("richtext") && f.Name != "slug" && f.Name != et.TitleField)
                    .Select(f => f.Name).ToArray(),
            })),
    };

    private static ViewDefinition GeneratedCreate(EntityTypeConfig et) => new()
    {
        Name = $"New {et.Name}",
        Route = $"/{ConfigLiveView.Pluralize(et.Slug)}/new",
        Layout = Node("Page", new { title = $"New {et.Name.ToLowerInvariant()}" },
            Node("MemberGate", new { action = $"create a {et.Name.ToLowerInvariant()}" },
                Node("EntityForm", new { entityType = et.Slug, mode = "create", afterCreate = "detail" }))),
    };

    private static ViewNode Node(string component, object props, params ViewNode[] children)
        => Node(component, props, null, children);

    private static ViewNode Node(string component, object props, string? visibleWhen, params ViewNode[] children)
    {
        var node = new ViewNode
        {
            Component = component,
            VisibleWhen = visibleWhen,
            Props = JsonSerializer.SerializeToElement(props),
        };
        node.Children.AddRange(children);
        return node;
    }
}
