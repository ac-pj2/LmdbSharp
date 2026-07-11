// MutationBridge: the broadcaster bridge — p2's mutation pipeline feeding
// LiveView sessions.
//
// Subscribes to p2's SSE stream (GET /api/mutation-stream, the same feed its
// SPA uses for cache invalidation). On any mutation touching our entity
// types it re-reads the affected rows from PostgreSQL, refreshes the
// projection (RecordCache), and broadcasts the fresh records to every
// LiveView session — which re-render and patch.
//
// This is the review's projector seam in miniature: ONE event keeps the
// read model warm AND updates every screen. Note the loop closure: our own
// writes go through the p2 REST API, come back out of this stream, and
// update our sessions by the same path as writes made in the p2 SPA —
// there is no second code path to drift.
using System.Net.Http.Headers;
using System.Text.Json;
using Lmdb.LiveView;

namespace ConfigViews;

public sealed class MutationBridge : BackgroundService
{
    private readonly P2Options _opt;
    private readonly P2EntityStore _store;
    private readonly RecordCache _cache;
    private readonly LiveViewHub _hub;
    private readonly ILogger<MutationBridge> _log;

    public MutationBridge(P2Options opt, P2EntityStore store, RecordCache cache,
        LiveViewHub hub, ILogger<MutationBridge> log)
    {
        _opt = opt;
        _store = store;
        _cache = cache;
        _hub = hub;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        int backoff = 1;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ListenAsync(ct);
                backoff = 1;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _log.LogWarning("mutation stream dropped: {Message} — retrying in {Backoff}s",
                    e.Message, backoff);
            }
            await Task.Delay(TimeSpan.FromSeconds(backoff), ct);
            backoff = Math.Min(backoff * 2, 30);
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var req = new HttpRequestMessage(HttpMethod.Get, _opt.ApiBase + "/api/mutation-stream");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _store.Token());
        req.Headers.Add("X-System-Slug", _opt.SystemSlug);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        _log.LogInformation("mutation bridge connected to {Base}/api/mutation-stream", _opt.ApiBase);

        using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        string? eventName = null;
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) throw new IOException("stream ended");

            if (line.StartsWith("event: ")) { eventName = line[7..].Trim(); continue; }
            if (line.StartsWith("data: ") && eventName == "mutation")
                HandleMutation(line[6..]);
            if (line == "") eventName = null;
        }
    }

    private void HandleMutation(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var types = doc.RootElement.TryGetProperty("entityTypes", out var t)
                ? t.EnumerateArray().Select(x => x.GetString() ?? "").ToArray()
                : Array.Empty<string>();
            if (!types.Any(x => _opt.EntityTypes.Contains(x))) return;

            var ids = doc.RootElement.TryGetProperty("entityIds", out var i)
                ? i.EnumerateArray().Select(x => x.GetString() ?? "").ToArray()
                : Array.Empty<string>();
            if (ids.Length == 0) return;

            // Refresh the projection from PostgreSQL and patch every session.
            var fresh = _store.FetchByKeys(ids);
            foreach (var rec in fresh)
            {
                _cache.Upsert(rec);
                _hub.Broadcast("record", rec);
            }
            _log.LogInformation("bridged mutation: {Types} → {Count} records refreshed",
                string.Join(",", types), fresh.Count);
        }
        catch (Exception e)
        {
            _log.LogWarning("bad mutation event: {Message}", e.Message);
        }
    }
}
