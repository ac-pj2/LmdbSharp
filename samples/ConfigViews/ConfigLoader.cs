// Loads a p2 system-config directory (views/*.json + entity-types/*.json) —
// the same files the platform's React ViewRenderer consumes, unmodified.
using System.Text.Json;

namespace ConfigViews;

public static class ConfigLoader
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static SystemConfigSet Load(string configDir)
    {
        var set = new SystemConfigSet();

        foreach (var file in Directory.GetFiles(Path.Combine(configDir, "views"), "*.json"))
        {
            try
            {
                var view = JsonSerializer.Deserialize<ViewDefinition>(File.ReadAllText(file), Opts);
                if (view != null && view.Route != "") set.Views.Add(view);
            }
            catch (JsonException e)
            {
                // PoC loader models the common tree shape; some views (e.g.
                // member-home's array-per-column children) use variants it
                // doesn't parse yet. Skip them — the forum views all load.
                Console.WriteLine($"[config] skipped {Path.GetFileName(file)}: {e.Message.Split('.')[0]}");
            }
        }

        foreach (var file in Directory.GetFiles(Path.Combine(configDir, "entity-types"), "*.json"))
        {
            var et = JsonSerializer.Deserialize<EntityTypeConfig>(File.ReadAllText(file), Opts);
            if (et == null) continue;
            et.Slug = Path.GetFileNameWithoutExtension(file);
            set.EntityTypes[et.Slug] = et;
        }

        // Longest (most specific) routes first: /forum-threads/new beats /forum-threads/:id.
        set.Views.Sort((a, b) => RouteSpecificity(b.Route).CompareTo(RouteSpecificity(a.Route)));
        return set;
    }

    private static int RouteSpecificity(string route)
        => route.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Sum(seg => seg.StartsWith(':') ? 1 : 100);

    /// <summary>Match a path against a view route pattern (/a/:id style).
    /// Returns captured params, or null if no match.</summary>
    public static Dictionary<string, string>? MatchRoute(string routePattern, string path)
    {
        var pat = routePattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var seg = path.Split('?')[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pat.Length != seg.Length) return null;

        var params_ = new Dictionary<string, string>();
        for (int i = 0; i < pat.Length; i++)
        {
            if (pat[i].StartsWith(':')) params_[pat[i][1..]] = Uri.UnescapeDataString(seg[i]);
            else if (!pat[i].Equals(seg[i], StringComparison.OrdinalIgnoreCase)) return null;
        }
        return params_;
    }
}
