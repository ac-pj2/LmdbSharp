// Loads a p2 system-config directory (views/*.json + entity-types/*.json) —
// the same files the platform's React ViewRenderer consumes, unmodified.
using System.Net.Http.Json;
using System.Text.Json;

namespace ConfigViews;

public static class ConfigLoader
{
    /// <summary>Load the DEPLOYED config straight from the live platform's API
    /// (views via /api/views + by-route, entity types, navigation) — the same
    /// definitions its SPA renders. Falls back to the directory on failure.</summary>
    public static SystemConfigSet LoadFromApi(P2Options opt, string fallbackDir)
    {
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(opt.ApiBase) };
            var login = http.PostAsJsonAsync("/api/auth/login",
                new { email = opt.Email, password = opt.Password }).GetAwaiter().GetResult();
            login.EnsureSuccessStatusCode();
            using var loginDoc = JsonDocument.Parse(login.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            var token = loginDoc.RootElement.GetProperty("data").GetProperty("token").GetString();
            http.DefaultRequestHeaders.Authorization = new("Bearer", token);
            http.DefaultRequestHeaders.Add("X-System-Slug", opt.SystemSlug);

            var set = new SystemConfigSet();

            using (var doc = Get(http, "/api/views"))
                foreach (var v in doc.RootElement.GetProperty("data").EnumerateArray())
                {
                    var route = v.GetProperty("route").GetString();
                    if (string.IsNullOrEmpty(route)) continue;
                    using var full = Get(http, "/api/views/by-route?route=" + Uri.EscapeDataString(route));
                    var view = full.RootElement.GetProperty("data").Deserialize<ViewDefinition>(Opts);
                    if (view is { Route.Length: > 0 }) set.Views.Add(view);
                }

            using (var doc = Get(http, $"/api/systems/{opt.SystemSlug}/entity-types"))
                foreach (var t in doc.RootElement.GetProperty("data").EnumerateArray())
                {
                    var et = t.Deserialize<EntityTypeConfig>(Opts);
                    if (et is { Slug.Length: > 0 }) set.EntityTypes[et.Slug] = et;
                }

            using (var doc = Get(http, $"/api/systems/{opt.SystemSlug}/navigation"))
            {
                var nav = doc.RootElement.GetProperty("data").Deserialize<List<NavItem>>(Opts);
                if (nav != null) set.Navigation.AddRange(nav);
            }

            set.Views.Sort((a, b) => RouteSpecificity(b.Route).CompareTo(RouteSpecificity(a.Route)));
            Console.WriteLine($"[config] live platform: {set.Views.Count} views, {set.EntityTypes.Count} entity types");
            return set;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[config] live API load failed ({e.Message}) — falling back to {fallbackDir}");
            return Load(fallbackDir);
        }
    }

    private static JsonDocument Get(HttpClient http, string path)
    {
        var res = http.GetAsync(path).GetAwaiter().GetResult();
        res.EnsureSuccessStatusCode();
        return JsonDocument.Parse(res.Content.ReadAsStringAsync().GetAwaiter().GetResult());
    }

    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static SystemConfigSet Load(string configDir)
    {
        var set = new SystemConfigSet();

        foreach (var file in Directory.GetFiles(Path.Combine(configDir, "views"), "*.json"))
        {
            var view = JsonSerializer.Deserialize<ViewDefinition>(File.ReadAllText(file), Opts);
            if (view != null && view.Route != "") set.Views.Add(view);
        }

        var navFile = Path.Combine(configDir, "navigation.json");
        if (File.Exists(navFile))
        {
            var nav = JsonSerializer.Deserialize<List<NavItem>>(File.ReadAllText(navFile), Opts);
            if (nav != null) set.Navigation.AddRange(nav);
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
