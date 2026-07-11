// DevPanel: a drop-in observability drawer for any LiveView. Add it as the
// last child of your root tree:
//
//     root.Children.Add(DevPanel.Render(this));
//     root.Children.Add(DevPanel.Render(this, ("sim ticks", ticks.ToString())));
//
// It renders:
//   - a floating "⚙" toggle button (client-side toggle, slide transition)
//   - server column: this session's render/diff timings, memo hit rate,
//     patch counts/bytes, session count — live via normal patches
//   - client column + wire log: data-lv-ignore zones that the client runtime
//     fills automatically when it sees the lv-dev-client element (connection
//     state, frames/bytes in/out, ops applied, apply time, last patch frames)
//
// Styles ship inside the panel; no page CSS or JS required.
namespace Lmdb.LiveView;

public static class DevPanel
{
    /// <summary>Build the dev drawer for <paramref name="view"/>. Values shown are
    /// from the previous render (stats update after rendering). Extra rows land
    /// at the end of the server column.</summary>
    public static HtmlElement Render(DeltaLiveView view, params (string Label, string Value)[] extra)
    {
        var s = view.Stats;

        var server = H.Div(
            H.H3("server · this session"),
            Row("renders", s.Renders.ToString()),
            Row("last render", $"{s.LastRenderMicros:f0} µs"),
            Row("last diff", $"{s.LastDiffMicros:f0} µs"),
            Row("memo hit rate", $"{s.MemoHitRate:p1} ({s.MemoHits}/{s.MemoHits + s.MemoMisses})"),
            Row("patch msgs", s.PatchMessages.ToString()),
            Row("patch bytes", FormatBytes(s.PatchBytes)),
            Row("tpl cache", $"{s.TemplateHits} hits / {s.TemplateDefs} defs"),
            Row("sessions", (view.Hub?.SessionCount ?? 1).ToString())
        ).Cls("lv-dev-col");
        foreach (var (label, value) in extra)
            server.Add(Row(label, value));

        return H.Div(
            H.Style(Css),
            H.Button("⚙").Cls("lv-dev-toggle").Client("toggle #lv-dev with lv-slide")
                .Attr("type", "button").Attr("aria-label", "Dev panel"),
            H.Div(
                server,
                H.Div(H.H3("client · this browser")).Cls("lv-dev-col").Id("lv-dev-client").Ignore(),
                H.Div(H.H3("wire · last frames")).Cls("lv-dev-col lv-dev-wide").Id("lv-dev-log").Ignore()
            ).Id("lv-dev").Hidden()
        ).Id("lv-devpanel");
    }

    private static HtmlElement Row(string label, string value)
        => H.Div(H.Small(label), H.Span(value)).Cls("lv-dev-row");

    private static string FormatBytes(long b) =>
        b > 1024 * 1024 ? $"{b / (1024.0 * 1024):f1} MB" : b > 1024 ? $"{b / 1024.0:f1} KB" : b + " B";

    // No '<' anywhere in this CSS (the tree parser treats style content as raw
    // text but a literal '<' would still confuse re-parsing).
    private const string Css = """
        .lv-dev-toggle { position: fixed; right: 14px; bottom: 14px; z-index: 40;
            width: 38px; height: 38px; border-radius: 50%; border: 1px solid #333a4d;
            background: #12151f; color: #8b93a7; font-size: 16px; cursor: pointer; }
        .lv-dev-toggle:hover { color: #e4e6eb; }
        #lv-dev { position: fixed; left: 0; right: 0; bottom: 0; z-index: 30;
            display: flex; gap: 28px; padding: 14px 28px 14px 28px; max-height: 220px;
            overflow: auto; background: rgba(13,16,23,0.96); border-top: 1px solid #333a4d;
            font-family: ui-monospace, 'SF Mono', Menlo, monospace; font-size: 12px;
            color: #dde1ea; }
        .lv-slide-in { animation: lv-slide-in 0.2s ease-out; }
        .lv-slide-out { animation: lv-slide-out 0.18s ease-in forwards; }
        @keyframes lv-slide-in { from { transform: translateY(100%); } to { transform: none; } }
        @keyframes lv-slide-out { from { transform: none; } to { transform: translateY(100%); } }
        .lv-dev-col { min-width: 230px; }
        .lv-dev-col.lv-dev-wide { flex: 1; overflow: hidden; }
        #lv-dev h3 { font-size: 11px; color: #5b8def; text-transform: uppercase;
            letter-spacing: 0.08em; margin: 0 0 8px 0; }
        .lv-dev-row { display: flex; justify-content: space-between; gap: 16px; padding: 1.5px 0; }
        .lv-dev-row small { color: #8b93a7; }
        .lv-dev-row span { font-variant-numeric: tabular-nums; }
        #lv-dev-log .lv-dev-op { color: #8b93a7; white-space: nowrap; overflow: hidden;
            text-overflow: ellipsis; padding: 1.5px 0; }
        #lv-dev-log .lv-dev-op b { color: #dde1ea; font-weight: 500; }
        """;
}
