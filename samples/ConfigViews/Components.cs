// Server-side implementations of the p2 view-engine components the forum
// views use. Each takes the ViewNode straight from views/*.json and renders
// with the H builder — text is escaped at render time, so entity data (thread
// titles, bodies) is XSS-safe by construction.
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

    public static HtmlElement Text(ViewNode node)
    {
        var variant = node.PropStr("variant", "p");
        var tag = variant is "h1" or "h2" or "h3" or "p" ? variant : "p";
        return H.El(tag, H.Text(node.PropStr("content"))).Cls("text");
    }

    public static HtmlElement Button(ViewNode node)
    {
        var btn = H.Button(node.PropStr("label")).Cls("btn " + node.PropStr("variant", "default"));
        // Declarative action from config → LiveView event. The PoC dispatches
        // the one action the forum uses; the full action set maps the same way.
        var onClick = node.Prop("onClick");
        if (onClick?.TryGetProperty("action", out var act) == true && act.GetString() == "navigate")
            btn.On("navigate").Attr("data-to", onClick.Value.GetProperty("to").GetString() ?? "/");
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

        var rows = Visible(v, et.Slug);
        var box = H.Div().Cls("entitylist");

        // Toolbar: search + configured filterFields.
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

        // Table.
        var columns = node.Prop("columns") is { ValueKind: JsonValueKind.Array } cols
            ? cols.EnumerateArray().Select(c => c.GetString() ?? "").ToList()
            : new List<string> { et.TitleField };
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
            var version = (rec.Ref, string.Join("", rec.Fields.Select(kv => kv.Key + "=" + kv.Value)));
            tbody.Add(v.MemoRow($"row{rec.Id}", version, () =>
            {
                var tr = H.El("tr").Key(rec.Id).On("open", rec.Id).Cls("row");
                foreach (var col in columns) tr.Add(H.El("td", CellValue(v, et, rec, col)));
                return tr;
            }));
        }
        if (rows.Count == 0)
            tbody.Add(H.El("tr", H.El("td", H.Text("Nothing here yet.")).Attr("colspan", columns.Count.ToString()).Cls("empty")));

        box.Add(H.El("table", H.El("thead", thead), tbody));
        return box;
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
            foreach (var opt in v.S.Records.Values.Where(r => r.EntityType == refType?.Slug).OrderBy(r => Title(v, r)))
            {
                var o = H.Option(Title(v, opt)).Attr("value", opt.Id.ToString());
                if (current == opt.Id.ToString()) o.Attr("selected", "");
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

    private static List<EntityRecord> Visible(ConfigLiveView v, string slug)
    {
        var q = v.S.Search.Trim();
        return v.S.Records.Values.Where(r =>
                r.EntityType == slug
                && !r.Flag("hidden")
                && (v.S.CategoryFilter == "" || r.F("category") == v.S.CategoryFilter)
                && (v.S.PinnedFilter == "" || (r.Flag("pinned") ? "true" : "false") == v.S.PinnedFilter)
                && (q == "" || r.F("title").Contains(q, StringComparison.OrdinalIgnoreCase)
                            || r.F("body").Contains(q, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private static IEnumerable<EntityRecord> Sort(ConfigLiveView v, EntityTypeConfig et, List<EntityRecord> rows)
    {
        if (v.S.SortField == "")
            return rows.OrderByDescending(r => r.Flag("pinned")).ThenByDescending(r => r.Id);

        Func<EntityRecord, string> key = v.S.SortField switch
        {
            "referenceNumber" => r => r.Id.ToString("d10"),
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
            _ => H.Text(rec.F(col)),
        };
    }

    private static string RefTitle(ConfigLiveView v, EntityTypeConfig et, EntityRecord rec, string field)
    {
        var refTypeSlug = et.Field(field)?.ReferenceType;
        var refType = refTypeSlug == null ? null : v.Config.ResolveEntityType(refTypeSlug);
        return long.TryParse(rec.F(field), out var id) && v.S.Records.TryGetValue(id, out var target)
            ? target.F(refType?.TitleField is { Length: > 0 } tf ? tf : "name")
            : "";
    }

    private static string Title(ConfigLiveView v, EntityRecord rec)
    {
        var et = v.Config.ResolveEntityType(rec.EntityType);
        return rec.F(et?.TitleField is { Length: > 0 } tf ? tf : "name");
    }

    public static HtmlElement EntityDetail(ConfigLiveView v, ViewNode node, Dictionary<string, string> p)
    {
        if (!p.TryGetValue("id", out var idStr) || !long.TryParse(idStr, out var id)
            || !v.S.Records.TryGetValue(id, out var rec))
            return H.Div(H.P("This thread doesn't exist (it may have been removed.)")).Cls("gate");

        var viewing = v.Presence($"thread:{id}");
        var detail = H.Div(
            H.Div(
                H.Span(rec.Ref).Cls("ref"),
                rec.Flag("pinned") ? H.Span("📌 pinned").Cls("badge") : H.Span().Cls("lv-hidden"),
                rec.Flag("closed") ? H.Span("closed").Cls("badge closed") : H.Span().Cls("lv-hidden"),
                H.Span(viewing.Count == 1 ? "1 person here" : $"{viewing.Count} people here").Cls("viewing")
            ).Cls("meta"),
            H.H2(rec.F(node.PropStr("titleField", "title"))),
            H.Small($"by {rec.AuthorName} · {rec.CreatedAt:MMM d, HH:mm}")
        ).Cls("detail");

        // richtext body: escaped text, paragraphs on blank lines.
        var body = H.Div().Cls("body");
        foreach (var para in rec.F(node.PropStr("bodyField", "body"))
                     .Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            body.Add(H.P(para.Trim()));
        detail.Add(body);
        return detail;
    }

    public static HtmlElement EntityComments(ConfigLiveView v, ViewNode node, Dictionary<string, string> p)
    {
        if (!p.TryGetValue("id", out var idStr) || !long.TryParse(idStr, out var threadId)) return H.Div();
        v.S.Records.TryGetValue(threadId, out var thread);

        var replies = v.S.Records.Values.Where(r => r.EntityType == "comment" && r.ParentId == threadId)
            .OrderBy(r => r.Id).ToList();

        var box = H.Div(H.H3($"{node.PropStr("title", "Comments")} ({replies.Count})")).Cls("comments");
        var list = H.Ul().Cls("commentlist");
        foreach (var c in replies)
            list.Add(v.MemoRow($"c{c.Id}", c.F("body"), () => H.Li(
                H.Div(H.B(c.AuthorName), H.Small(c.CreatedAt.ToString("MMM d, HH:mm"))).Cls("chead"),
                H.P(c.F("body"))
            ).Key("c" + c.Id).Cls("comment")));
        box.Add(list);

        if (thread?.Flag("closed") == true)
            box.Add(H.Div(H.P("This thread is closed — replies are off.")).Cls("gate"));
        else if (v.S.UserName == null)
            box.Add(H.Div(H.P($"Sign in to {node.PropStr("action", "reply")}." )).Cls("gate"));
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

        var form = H.Form().On("create").Cls("entityform").Attr("data-no-reset", "");

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
            : et.Fields.Select(f => f.Name).ToList();

        foreach (var fname in fieldNames)
        {
            var f = et.Field(fname);
            if (f == null) continue;
            var row = H.Div(H.Label(f.Label + (f.Required ? " *" : ""))).Cls("formrow");
            var draft = v.S.Draft.GetValueOrDefault(f.Name, "");

            switch (f.Type)
            {
                case "richtext":
                    var ta = H.El("textarea").Attr("name", f.Name).Attr("rows", "6");
                    if (draft != "") ta.Add(H.Text(draft));
                    row.Add(ta);
                    break;
                case "reference" when f.ReferenceType != null:
                {
                    var refType = v.Config.ResolveEntityType(f.ReferenceType);
                    var sel = H.Select().Attr("name", f.Name);
                    sel.Add(H.Option("Choose…").Attr("value", ""));
                    foreach (var opt in v.S.Records.Values.Where(r => r.EntityType == refType?.Slug)
                                 .OrderBy(r => Title(v, r)))
                    {
                        var o = H.Option(Title(v, opt)).Attr("value", opt.Id.ToString());
                        if (draft == opt.Id.ToString()) o.Attr("selected", "");
                        sel.Add(o);
                    }
                    row.Add(sel);
                    break;
                }
                default:
                    row.Add(H.Input().Attr("name", f.Name).Attr("type", "text").Attr("value", draft));
                    break;
            }
            form.Add(row);
        }

        form.Add(H.Button("Create").Cls("btn primary"));
        return form;
    }
}
