// Server-side implementations of the p2 view-engine components — Phase 2
// covers every component the coaching-hub system's views use (100% of the
// system renders), plus generated default layouts for entity types without
// explicit views (the platform's fallback cascade).
//
// Each renderer takes the ViewNode straight from views/*.json. Text is
// escaped at render time; {{...}} prop interpolation and *Expr props run
// through the platform's own expression engine.
using System.Text.Json;
using Lmdb.LiveView;

namespace ConfigViews;

internal static class Components
{
    // ── layout & content ──

    public static HtmlElement Page(ConfigLiveView v, ViewNode node, Dictionary<string, string> p)
    {
        var page = H.Div(H.H1(node.PropStr("title"))).Cls("page");
        foreach (var child in node.Children) page.Add(v.RenderNode(child, p));
        return page;
    }

    public static HtmlElement Stack(ConfigLiveView v, ViewNode node, Dictionary<string, string> p)
    {
        var gap = node.Prop("gap")?.ValueKind == JsonValueKind.Number ? node.Prop("gap")!.Value.GetInt32() : 3;
        var stack = H.Div().Cls("stack").Attr("style", $"gap:{gap * 4}px");
        foreach (var child in node.Children) stack.Add(v.RenderNode(child, p));
        return stack;
    }

    public static HtmlElement Group(ConfigLiveView v, ViewNode node, Dictionary<string, string> p)
    {
        var g = H.Div().Cls("stack").Attr("style", "gap:16px");
        foreach (var child in node.Children) g.Add(v.RenderNode(child, p));
        return g;
    }

    public static HtmlElement Section(ConfigLiveView v, ViewNode node, Dictionary<string, string> p)
    {
        var title = node.PropStr("titleExpr") != ""
            ? v.EvalString(node.PropStr("titleExpr"))
            : v.Interpolate(node.PropStr("title"));
        var section = H.Section(H.H2(title)).Cls("cfg-section");
        foreach (var child in node.Children) section.Add(v.RenderNode(child, p));
        return section;
    }

    public static HtmlElement Card(ConfigLiveView v, ViewNode node, Dictionary<string, string> p)
    {
        var card = H.Div().Cls("cfg-card");
        var title = v.Interpolate(node.PropStr("title"));
        if (title != "") card.Add(H.H3(title));
        foreach (var child in node.Children) card.Add(v.RenderNode(child, p));
        return card;
    }

    public static HtmlElement Columns(ConfigLiveView v, ViewNode node, Dictionary<string, string> p)
    {
        var cols = node.Prop("cols") is { ValueKind: JsonValueKind.Array } c
            ? c.EnumerateArray().Select(x => x.GetInt32()).ToArray() : new[] { 1, 1 };
        var grid = H.Div().Cls("cfg-columns")
            .Attr("style", $"grid-template-columns:{string.Join(" ", cols.Select(x => x + "fr"))}");
        foreach (var child in node.Children) grid.Add(v.RenderNode(child, p));
        return grid;
    }

    public static HtmlElement Text(ConfigLiveView v, ViewNode node)
    {
        var content = node.PropStr("contentExpr") != ""
            ? v.EvalString(node.PropStr("contentExpr"))
            : v.Interpolate(node.PropStr("content"));
        var variant = node.PropStr("variant", "p");
        var tag = variant is "h1" or "h2" or "h3" or "p" ? variant : "p";
        return H.El(tag, H.Text(content)).Cls("text");
    }

    public static HtmlElement Image(ConfigLiveView v, ViewNode node)
    {
        var src = v.Interpolate(node.PropStr("src"));
        if (src == "") return H.Span().Cls("lv-hidden");
        var style = $"width:{node.PropStr("width", "100%")};height:{node.PropStr("height", "auto")};object-fit:cover";
        var img = H.El("img").Attr("src", src)
            .Attr("alt", v.Interpolate(node.PropStr("alt"))).Attr("style", style);
        if (node.Prop("rounded")?.GetBoolean() == true) img.Cls("rounded");
        return img;
    }

    public static HtmlElement FieldValue(ConfigLiveView v, ViewNode node)
        => RenderFieldValue(node.PropStr("type", "text"), v.Interpolate(node.PropStr("value")));

    /// <summary>Display rendering for the platform's field types.</summary>
    internal static HtmlElement RenderFieldValue(string type, string value) => type switch
    {
        "richtext" => RichText(value),
        "boolean" => H.Span(value == "true" ? "✓" : "—").Cls("fv-bool"),
        "tags" => H.Span().Cls("fv-tags").AddRange(SplitTags(value)
            .Select(t => (HtmlNode)H.Span("#" + t).Cls("tagchip"))),
        "image" => value == "" ? H.Span().Cls("lv-hidden")
            : H.El("img").Attr("src", value).Attr("style", "max-width:100%;border-radius:8px"),
        "date" or "datetime" => H.Span(value.Split('T')[0]).Cls("fv-date"),
        "url" => H.A(value).Attr("href", value).Attr("target", "_blank").Attr("rel", "noopener"),
        "rating" => H.Span(int.TryParse(value, out var r)
            ? new string('★', Math.Clamp(r, 0, 5)) + new string('☆', 5 - Math.Clamp(r, 0, 5)) : value).Cls("fv-rating"),
        _ => H.Span(value),
    };

    internal static HtmlElement RichText(string value)
    {
        var body = H.Div().Cls("body");
        if (value.Contains('<'))
        {
            // The platform stores richtext as HTML (TipTap). Parse it into the
            // render tree and SANITIZE: whitelist tags, strip scripts/styles/
            // event handlers/javascript: URLs. Text still escapes at render.
            var parsed = HtmlParser.Parse(value);
            foreach (var child in SanitizeChildren(parsed)) body.Add(child);
            return body;
        }
        foreach (var para in value.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            body.Add(H.P(para.Trim()));
        return body;
    }

    private static readonly HashSet<string> RichTextTags = new()
    {
        "p", "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "li", "strong", "em",
        "b", "i", "u", "s", "a", "br", "hr", "blockquote", "code", "pre", "img",
        "span", "div", "table", "thead", "tbody", "tr", "th", "td",
    };

    private static List<HtmlNode> SanitizeChildren(HtmlElement el)
    {
        var result = new List<HtmlNode>();
        foreach (var child in el.Children)
        {
            switch (child)
            {
                case HtmlText text:
                    result.Add(new HtmlText { Text = text.Text });
                    break;
                case HtmlElement e when e.Tag is "script" or "style" or "iframe" or "object" or "embed" or "form":
                    break; // dropped entirely, including content
                case HtmlElement e when !RichTextTags.Contains(e.Tag):
                    result.AddRange(SanitizeChildren(e)); // unknown wrapper — keep its content
                    break;
                case HtmlElement e:
                {
                    var clean = new HtmlElement { Tag = e.Tag };
                    foreach (var (name, val) in e.Attributes)
                    {
                        bool ok = name switch
                        {
                            "href" or "src" => val.StartsWith("http://") || val.StartsWith("https://") || val.StartsWith("/"),
                            "alt" or "title" or "colspan" or "rowspan" => true,
                            _ => false,
                        };
                        if (ok) clean.Attributes[name] = val;
                    }
                    if (e.Tag == "a") { clean.Attributes["rel"] = "noopener"; clean.Attributes["target"] = "_blank"; }
                    foreach (var sub in SanitizeChildren(e)) clean.Children.Add(sub);
                    result.Add(clean);
                    break;
                }
            }
        }
        return result;
    }

    private static IEnumerable<string> SplitTags(string value)
        => value.TrimStart('[').TrimEnd(']').Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().Trim('"')).Where(t => t != "");

    public static HtmlElement Button(ConfigLiveView v, ViewNode node)
    {
        var btn = H.Button(node.PropStr("label")).Cls("btn " + node.PropStr("variant", "default"));
        var onClick = node.Prop("onClick");
        if (onClick?.TryGetProperty("action", out var act) == true && act.GetString() == "navigate")
            btn.On("navigate").Attr("data-to",
                v.Interpolate(onClick.Value.GetProperty("to").GetString() ?? "/"));
        return btn;
    }

    public static HtmlElement MemberGate(ConfigLiveView v, ViewNode node, Dictionary<string, string> p)
    {
        if (v.S.UserName != null)
        {
            var wrap = H.Div().Cls("membergate");
            foreach (var child in node.Children) wrap.Add(v.RenderNode(child, p));
            return wrap;
        }
        return H.Div(
            H.P($"Sign in to {node.PropStr("action", "continue")}."),
            H.Small("Use the account picker in the header — this demo fakes auth.")
        ).Cls("gate");
    }

    // ── entity components ──

    public static HtmlElement EntityList(ConfigLiveView v, ViewNode node)
    {
        var et = v.Config.ResolveEntityType(node.PropStr("entityType"));
        if (et == null) return H.Div(H.Small("unknown entity type")).Cls("todo");

        // member-home variants: compact display, favoritesOnly, filter props.
        if (node.PropStr("display") == "compact")
            return CompactList(v, et, node);
        if (node.PropStr("display") == "card")
            return CardList(v, et, node);

        var rows = Visible(v, et.Slug, node);
        var box = H.Div().Cls("entitylist");

        if (node.Prop("searchable")?.GetBoolean() == true || node.Prop("filterFields") != null)
        {
            var bar = H.Div().Cls("toolbar");
            if (node.Prop("searchable")?.GetBoolean() == true)
                bar.Add(H.Input().Attr("name", "q").Attr("placeholder", $"Search {et.Name.ToLowerInvariant()}s…")
                    .Attr("value", v.S.Search).On("search").Debounce(250));
            if (node.Prop("filterFields") is { ValueKind: JsonValueKind.Array } ff)
                foreach (var f in ff.EnumerateArray())
                    bar.Add(FilterControl(v, et, f.GetProperty("field").GetString() ?? "",
                        f.TryGetProperty("label", out var l) ? l.GetString() ?? "" : ""));
            box.Add(bar);
        }

        var columns = node.Prop("columns") is { ValueKind: JsonValueKind.Array } cols
            ? cols.EnumerateArray().Select(c => c.GetString() ?? "").ToList()
            : DefaultViews.DefaultColumns(et);
        bool sortable = node.Prop("sortable")?.GetBoolean() == true;

        var thead = H.El("tr");
        foreach (var col in columns)
        {
            var th = H.El("th", H.Text(ColumnLabel(et, col)
                + (v.S.SortField == col ? (v.S.SortAsc ? " ↑" : " ↓") : "")));
            if (sortable) th.On("sort").Attr("data-field", col).Cls("sortable");
            thead.Add(th);
        }

        var tbody = H.El("tbody");
        foreach (var rec in Sort(v, et, rows))
        {
            var version = (rec.Ref, string.Join("", rec.Fields.Select(kv => kv.Key + "=" + kv.Value)));
            tbody.Add(v.MemoRow($"row{rec.Key}", version, () =>
            {
                var tr = H.El("tr").Key(rec.Key).On("open", rec.Key).Cls("row")
                    .Attr("data-type", et.Slug);
                foreach (var col in columns) tr.Add(H.El("td", CellValue(v, et, rec, col)));
                return tr;
            }));
        }
        if (rows.Count == 0)
            tbody.Add(H.El("tr", H.El("td", H.Text("Nothing here yet.")).Attr("colspan", columns.Count.ToString()).Cls("empty")));

        box.Add(H.El("table", H.El("thead", thead), tbody));
        return box;
    }

    private static HtmlElement CardList(ConfigLiveView v, EntityTypeConfig et, ViewNode node)
    {
        var rows = Visible(v, et.Slug, node).OrderByDescending(r => r.CreatedAt).ToList();
        if (rows.Count == 0)
            return H.Div(H.Small(node.PropStr("emptyText", "Nothing here yet."))).Cls("compact-empty");

        var imageField = node.PropStr("imageField");
        var grid = H.Div().Cls("cardgrid");
        foreach (var rec in rows)
        {
            var title = rec.F(et.TitleField is { Length: > 0 } tf ? tf : "title");
            var version = (title, rec.F(imageField), rec.F("description"), rec.F("excerpt"));
            grid.Add(v.MemoRow($"cl{rec.Key}", version, () =>
            {
                var card = H.Div().Cls("cfg-card clickable").Key(rec.Key)
                    .On("open", rec.Key).Attr("data-type", et.Slug);
                if (imageField != "" && rec.F(imageField) != "")
                    card.Add(H.El("img").Attr("src", rec.F(imageField))
                        .Attr("style", "width:100%;height:110px;object-fit:cover;border-radius:8px"));
                card.Add(H.B(title));
                var blurb = rec.F("description") is { Length: > 0 } d ? d : rec.F("excerpt");
                if (blurb.Length > 110) blurb = blurb[..110] + "…";
                if (blurb != "") card.Add(H.P(blurb));
                return card;
            }));
        }
        return grid;
    }

    private static HtmlElement CompactList(ConfigLiveView v, EntityTypeConfig et, ViewNode node)
    {
        // favoritesOnly needs the platform's favorites subsystem — out of PoC scope.
        if (node.Prop("favoritesOnly")?.GetBoolean() == true)
            return H.Div(H.Small("Favorites live in the platform's favorites subsystem (not projected yet).")).Cls("todo");

        var rows = Visible(v, et.Slug, node)
            .OrderByDescending(r => r.CreatedAt)
            .Take(node.Prop("pageSize")?.GetInt32() ?? 5).ToList();
        if (rows.Count == 0)
            return H.Div(H.Small("Nothing here yet.")).Cls("compact-empty");

        var list = H.Ul().Cls("compactlist");
        foreach (var rec in rows)
            list.Add(H.Li(
                H.Span(rec.F(et.TitleField is { Length: > 0 } tf ? tf : "title")).Cls("cl-title"),
                H.Small(rec.CreatedAt.ToString("MMM d"))
            ).Key("cl" + rec.Key).On("open", rec.Key).Attr("data-type", et.Slug).Cls("row"));
        return list;
    }

    private static HtmlElement FilterControl(ConfigLiveView v, EntityTypeConfig et, string field, string label)
    {
        var current = field == "category" ? v.S.CategoryFilter : v.S.PinnedFilter;
        var sel = H.Select().Attr("name", "filter-" + field).On("change").Cls("filter");
        sel.Add(H.Option($"{(label == "" ? field : label)}: all").Attr("value", ""));

        var fieldCfg = et.Field(field);
        if (fieldCfg?.Type == "reference" && fieldCfg.ReferenceType != null)
        {
            var refType = v.Config.ResolveEntityType(fieldCfg.ReferenceType);
            foreach (var opt in v.S.Records.Values.Where(r => r.EntityType == refType?.Slug)
                         .OrderBy(r => Title(v, r), StringComparer.OrdinalIgnoreCase))
            {
                var o = H.Option(Title(v, opt)).Attr("value", opt.Key);
                if (current == opt.Key) o.Attr("selected", "");
                sel.Add(o);
            }
        }
        else if (fieldCfg?.Type == "boolean")
        {
            foreach (var (val, lbl) in new[] { ("true", "yes"), ("false", "no") })
            {
                var o = H.Option(lbl).Attr("value", val);
                if (current == val) o.Attr("selected", "");
                sel.Add(o);
            }
        }
        return sel;
    }

    private static List<EntityRecord> Visible(ConfigLiveView v, string slug, ViewNode? node = null)
    {
        var q = v.S.Search.Trim();
        var rows = v.S.Records.Values.Where(r =>
                r.EntityType == slug
                && !r.Flag("hidden")
                && (v.S.CategoryFilter == "" || (ResolveRef(v, v.S.CategoryFilter) is { } cat
                        ? RefEquals(r, "category", cat) : r.F("category") == v.S.CategoryFilter))
                && (v.S.PinnedFilter == "" || (r.Flag("pinned") ? "true" : "false") == v.S.PinnedFilter)
                && (q == "" || r.F("title").Contains(q, StringComparison.OrdinalIgnoreCase)
                            || r.F("body").Contains(q, StringComparison.OrdinalIgnoreCase)));

        // Declarative filter prop, values interpolated: {"author": "{{user.id}}"}.
        if (node?.Prop("filter") is { ValueKind: JsonValueKind.Object } filter)
            foreach (var f in filter.EnumerateObject())
            {
                var want = v.Interpolate(f.Value.GetString() ?? "");
                rows = rows.Where(r => r.F(f.Name) == want);
            }

        return rows.ToList();
    }

    private static IEnumerable<EntityRecord> Sort(ConfigLiveView v, EntityTypeConfig et, List<EntityRecord> rows)
    {
        if (v.S.SortField == "")
            return rows.OrderByDescending(r => r.Flag("pinned")).ThenByDescending(r => r.CreatedAt);

        Func<EntityRecord, string> key = v.S.SortField switch
        {
            "referenceNumber" => r => r.Ref,
            "category" => r => RefTitle(v, et, r, "category"),
            _ => r => r.F(v.S.SortField),
        };
        return v.S.SortAsc ? rows.OrderBy(key) : rows.OrderByDescending(key);
    }

    private static string ColumnLabel(EntityTypeConfig et, string col) => col switch
    {
        "referenceNumber" => "Ref",
        _ => et.Field(col)?.Label ?? char.ToUpper(col[0]) + col[1..],
    };

    private static HtmlNode CellValue(ConfigLiveView v, EntityTypeConfig et, EntityRecord rec, string col)
    {
        if (col == "referenceNumber") return H.Span(rec.Ref).Cls("ref");
        var f = et.Field(col);
        return f?.Type switch
        {
            "boolean" => rec.Flag(col) ? H.Span(col == "pinned" ? "📌" : "✓") : H.Text(""),
            "reference" => H.Span(RefTitle(v, et, rec, col)).Cls("tagchip"),
            null => H.Text(rec.F(col)),
            _ => RenderFieldValue(f.Type, rec.F(col)),
        };
    }

    private static string RefTitle(ConfigLiveView v, EntityTypeConfig et, EntityRecord rec, string field)
    {
        var refTypeSlug = et.Field(field)?.ReferenceType;
        var refType = refTypeSlug == null ? null : v.Config.ResolveEntityType(refTypeSlug);
        var target = ResolveRef(v, rec.F(field));
        return target?.F(refType?.TitleField is { Length: > 0 } tf ? tf : "name") ?? "";
    }

    private static string Title(ConfigLiveView v, EntityRecord rec)
    {
        var et = v.Config.ResolveEntityType(rec.EntityType);
        return rec.F(et?.TitleField is { Length: > 0 } tf ? tf : "name");
    }

    /// <summary>Reference fields may hold either the target's id (GUID) or its
    /// reference number — the platform normalizes to refnumbers on save. Match both.</summary>
    private static bool RefEquals(EntityRecord rec, string field, EntityRecord target)
    {
        var val = rec.F(field);
        return val != "" && (val == target.Key || val == target.Ref);
    }

    private static EntityRecord? ResolveRef(ConfigLiveView v, string value)
        => value == "" ? null
         : v.S.Records.TryGetValue(value, out var byKey) ? byKey
         : v.S.Records.Values.FirstOrDefault(r => r.Ref == value && r.Ref != "");

    /// <summary>Category cards with roll-up counts — forum-category-list.json.</summary>
    public static HtmlElement EntityCardGrid(ConfigLiveView v, ViewNode node)
    {
        var et = v.Config.ResolveEntityType(node.PropStr("entityType"));
        if (et == null) return H.Div(H.Small("unknown entity type")).Cls("todo");

        var rollupType = v.Config.ResolveEntityType(node.PropStr("rollupEntityType"));
        var rollupField = node.PropStr("rollupReferenceField");
        var items = Visible(v, et.Slug).OrderBy(r => r.F("order")).ThenBy(r => Title(v, r)).ToList();
        if (items.Count == 0)
            return H.Div(H.P(node.PropStr("emptyText", "Nothing here yet."))).Cls("gate");

        var grid = H.Div().Cls("cardgrid");
        foreach (var item in items)
        {
            var rollups = rollupType == null ? new List<EntityRecord>() :
                v.S.Records.Values.Where(r => r.EntityType == rollupType.Slug && RefEquals(r, rollupField, item)).ToList();
            var lastActivity = rollups.Count > 0 ? rollups.Max(r => r.CreatedAt) : (DateTime?)null;

            var version = (item.F("name"), item.F("description"), rollups.Count, lastActivity?.ToString("O") ?? "");
            grid.Add(v.MemoRow($"card{item.Key}", version, () => H.Div(
                H.Div(
                    H.Span(item.F(node.PropStr("iconField", "icon"))).Cls("cg-icon"),
                    H.B(item.F(node.PropStr("titleField", "name")))
                ).Cls("cg-head"),
                H.P(item.F(node.PropStr("descriptionField", "description"))),
                H.Small($"{rollups.Count} {node.PropStr("rollupLabel", "items")}"
                    + (lastActivity != null && node.Prop("showLastActivity")?.GetBoolean() == true
                        ? $" · active {lastActivity:MMM d}" : ""))
            ).Cls("cfg-card clickable").Key(item.Key).On("open", item.Key).Attr("data-type", et.Slug)));
        }
        return grid;
    }

    /// <summary>Child entity list (threads of a category) — forum-category-detail.json.</summary>
    public static HtmlElement EntityChildren(ConfigLiveView v, ViewNode node, Dictionary<string, string> p)
    {
        var childType = v.Config.ResolveEntityType(node.PropStr("childEntityType"));
        if (childType == null || !p.TryGetValue("id", out var parentKey))
            return H.Div(H.Small("unknown child type")).Cls("todo");

        var refField = node.PropStr("referenceField");
        v.S.Records.TryGetValue(parentKey, out var parent);
        var children = v.S.Records.Values
            .Where(r => r.EntityType == childType.Slug && !r.Flag("hidden")
                && (parent != null ? RefEquals(r, refField, parent) : r.F(refField) == parentKey))
            .OrderByDescending(r => r.CreatedAt).ToList();

        var box = H.Div(H.H3($"{node.PropStr("title", "Items")} ({children.Count})")).Cls("children");
        if (children.Count == 0)
        {
            box.Add(H.P(node.PropStr("emptyText", "Nothing here yet.")).Cls("compact-empty"));
            return box;
        }

        var list = H.Ul().Cls("childlist");
        var excerptField = node.PropStr("excerptField", "body");
        foreach (var c in children)
        {
            var excerpt = c.F(excerptField);
            if (excerpt.Length > 140) excerpt = excerpt[..140] + "…";
            var replies = v.S.Records.Values.Count(r => r.EntityType == "comment" && r.ParentKey == c.Key);
            var version = (c.F("title"), excerpt, replies);
            list.Add(v.MemoRow($"child{c.Key}", version, () => H.Li(
                H.Div(H.B(c.F(node.PropStr("titleField", "title"))),
                      H.Small($"{c.AuthorName} · {replies} replies")).Cls("chead"),
                H.Small(excerpt)
            ).Key("ch" + c.Key).On("open", c.Key).Attr("data-type", childType.Slug).Cls("comment row")));
        }
        box.Add(list);
        return box;
    }

    /// <summary>Article cards (hero, excerpt, reading time) — member-home.json.</summary>
    public static HtmlElement ArticleCardGrid(ConfigLiveView v, ViewNode node)
    {
        var et = v.Config.ResolveEntityType(node.PropStr("entityType", "article"));
        if (et == null) return H.Div(H.Small("unknown entity type")).Cls("todo");

        var rows = Visible(v, et.Slug)
            .Where(r => node.Prop("featured")?.GetBoolean() != true || r.Flag("featured"))
            .OrderByDescending(r => r.F("publishedDate"))
            .Take(node.Prop("limit")?.GetInt32() ?? 6).ToList();
        if (rows.Count == 0)
            return H.Div(H.Small("No articles yet.")).Cls("compact-empty");

        var grid = H.Div().Cls("cardgrid");
        foreach (var a in rows)
        {
            var version = (a.F("title"), a.F("excerpt"), a.F("heroImage"));
            grid.Add(v.MemoRow($"art{a.Key}", version, () =>
            {
                var card = H.Div().Cls("cfg-card clickable").Key(a.Key)
                    .On("open", a.Key).Attr("data-type", et.Slug);
                if (a.F("heroImage") != "")
                    card.Add(H.El("img").Attr("src", a.F("heroImage"))
                        .Attr("style", "width:100%;height:120px;object-fit:cover;border-radius:8px"));
                card.Add(H.B(a.F("title")));
                var excerpt = a.F("excerpt");
                if (excerpt.Length > 110) excerpt = excerpt[..110] + "…";
                card.Add(H.P(excerpt));
                if (a.F("readingTime") != "")
                    card.Add(H.Small($"{a.F("readingTime")} min read"));
                return card;
            }));
        }
        return grid;
    }

    public static HtmlElement EntityDetail(ConfigLiveView v, ViewNode node, Dictionary<string, string> p)
    {
        if (!p.TryGetValue("id", out var key) || !v.S.Records.TryGetValue(key, out var rec))
            return H.Div(H.P("This item doesn't exist (it may have been removed).")).Cls("gate");
        var et = v.Config.ResolveEntityType(rec.EntityType);

        var viewing = v.Presence($"thread:{key}");
        var detail = H.Div(
            H.Div(
                H.Span(rec.Ref).Cls("ref"),
                rec.Flag("pinned") ? H.Span("📌 pinned").Cls("badge") : H.Span().Cls("lv-hidden"),
                rec.Flag("closed") ? H.Span("closed").Cls("badge closed") : H.Span().Cls("lv-hidden"),
                H.Span(viewing.Count <= 1 ? "" : $"{viewing.Count} people here").Cls("viewing")
            ).Cls("meta"),
            H.H2(rec.F(node.PropStr("titleField", et?.TitleField ?? "title"))),
            H.Small($"by {rec.AuthorName} · {rec.CreatedAt:MMM d, HH:mm}")
        ).Cls("detail");

        var bodyField = node.PropStr("bodyField", "body");
        if (rec.F(bodyField) != "") detail.Add(RichText(rec.F(bodyField)));

        // displayFields: config-selected extra fields, rendered by type.
        if (node.Prop("displayFields") is { ValueKind: JsonValueKind.Array } df)
            foreach (var fEl in df.EnumerateArray())
            {
                var fname = fEl.GetString() ?? "";
                var f = et?.Field(fname);
                if (f == null || rec.F(fname) == "") continue;
                detail.Add(H.Div(H.Small(f.Label), RenderFieldValue(f.Type, rec.F(fname))).Cls("formrow"));
            }

        return detail;
    }

    public static HtmlElement EntityComments(ConfigLiveView v, ViewNode node, Dictionary<string, string> p)
    {
        if (!p.TryGetValue("id", out var threadKey)) return H.Div();
        v.S.Records.TryGetValue(threadKey, out var thread);

        var replies = v.S.Records.Values.Where(r => r.EntityType == "comment" && r.ParentKey == threadKey)
            .OrderBy(r => r.CreatedAt).ToList();

        var box = H.Div(H.H3($"{node.PropStr("title", "Comments")} ({replies.Count})")).Cls("comments");
        var list = H.Ul().Cls("commentlist");
        foreach (var c in replies)
            list.Add(v.MemoRow($"c{c.Key}", c.F("body"), () => H.Li(
                H.Div(H.B(c.AuthorName), H.Small(c.CreatedAt.ToString("MMM d, HH:mm"))).Cls("chead"),
                H.P(c.F("body"))
            ).Key("c" + c.Key).Cls("comment")));
        box.Add(list);

        if (thread?.Flag("closed") == true)
            box.Add(H.Div(H.P("This thread is closed — replies are off.")).Cls("gate"));
        else if (v.S.UserName == null)
            box.Add(H.Div(H.P($"Sign in to {node.PropStr("action", "reply")}.")).Cls("gate"));
        else
            box.Add(H.Form(
                H.El("textarea").Attr("name", "body").Attr("placeholder", "Write a reply…").Attr("rows", "3"),
                H.Button("Reply").Cls("btn primary")
            ).On("reply").Cls("replyform"));

        return box;
    }

    public static HtmlElement EntityForm(ConfigLiveView v, ViewNode node)
    {
        var et = v.Config.ResolveEntityType(node.PropStr("entityType"));
        if (et == null) return H.Div(H.Small("unknown entity type")).Cls("todo");

        var form = H.Form().On("create").Cls("entityform")
            .Attr("data-no-reset", "").Attr("data-entitytype", et.Slug);

        if (v.S.FormErrors.Count > 0)
        {
            var errs = H.Ul().Cls("errors");
            foreach (var e in v.S.FormErrors) errs.Add(H.Li(e));
            form.Add(errs);
        }

        var sections = node.Prop("sections");
        var fieldNames = sections is { ValueKind: JsonValueKind.Array }
            ? sections.Value.EnumerateArray().SelectMany(s =>
                s.GetProperty("fields").EnumerateArray().Select(f => f.GetString() ?? "")).ToList()
            : et.Fields.Where(f => f.Name is not ("slug" or "author")).Select(f => f.Name).ToList();

        foreach (var fname in fieldNames)
        {
            var f = et.Field(fname);
            if (f == null) continue;
            var row = H.Div(H.Label(f.Label + (f.Required ? " *" : ""))).Cls("formrow");
            // Draft (validation-error retention) → query param (?category=...) → empty.
            var value = v.S.Draft.GetValueOrDefault(f.Name,
                v.S.Query.GetValueOrDefault(f.Name, ""));

            row.Add(FormInput(v, f, value));
            form.Add(row);
        }

        form.Add(H.Button("Create").Cls("btn primary"));
        return form;
    }

    /// <summary>Form input per platform field type.</summary>
    private static HtmlElement FormInput(ConfigLiveView v, FieldConfig f, string value)
    {
        switch (f.Type)
        {
            case "richtext":
                var ta = H.El("textarea").Attr("name", f.Name).Attr("rows", "6");
                if (value != "") ta.Add(H.Text(value));
                return ta;

            case "reference" when f.ReferenceType != null:
            {
                var refType = v.Config.ResolveEntityType(f.ReferenceType);
                var sel = H.Select().Attr("name", f.Name);
                sel.Add(H.Option("Choose…").Attr("value", ""));
                foreach (var opt in v.S.Records.Values.Where(r => r.EntityType == refType?.Slug)
                             .OrderBy(r => r.F(refType?.TitleField is { Length: > 0 } tf ? tf : "name")))
                {
                    var o = H.Option(opt.F(refType?.TitleField is { Length: > 0 } tf2 ? tf2 : "name"))
                        .Attr("value", opt.Key);
                    if (value == opt.Key) o.Attr("selected", "");
                    sel.Add(o);
                }
                return sel;
            }

            case "boolean":
            {
                var cb = H.Input().Attr("name", f.Name).Attr("type", "checkbox").Attr("value", "true");
                if (value == "true") cb.Attr("checked", "");
                return cb;
            }

            case "number" or "currency" or "rating" or "duration":
                return H.Input().Attr("name", f.Name).Attr("type", "number").Attr("value", value);
            case "date":
                return H.Input().Attr("name", f.Name).Attr("type", "date").Attr("value", value);
            case "datetime":
                return H.Input().Attr("name", f.Name).Attr("type", "datetime-local").Attr("value", value);
            case "email":
                return H.Input().Attr("name", f.Name).Attr("type", "email").Attr("value", value);
            case "url" or "image":
                return H.Input().Attr("name", f.Name).Attr("type", "url").Attr("value", value)
                    .Attr("placeholder", f.Type == "image" ? "https://… (image URL)" : "https://…");
            case "phone":
                return H.Input().Attr("name", f.Name).Attr("type", "tel").Attr("value", value);
            case "tags":
                return H.Input().Attr("name", f.Name).Attr("type", "text").Attr("value", value)
                    .Attr("placeholder", "comma, separated, tags");
            default:
                return H.Input().Attr("name", f.Name).Attr("type", "text").Attr("value", value);
        }
    }
}
