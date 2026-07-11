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
    private readonly IRecordProjection _cache;
    private readonly LiveViewHub _hub;
    private readonly ILogger<MutationBridge> _log;

    public MutationBridge(P2Options opt, P2EntityStore store, IRecordProjection cache,
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

        // SSE only delivers LIVE events — anything that happened between startup
        // (or a stream drop) and this connect was missed. Catch up by diffing
        // the store against the projection, exactly like a startup reconcile.
        // Retries in the background if PostgreSQL is temporarily unavailable.
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 40 && !ct.IsCancellationRequested; i++)
            {
                if (CatchUp()) return;
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }
        }, ct);

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

    private bool CatchUp()
    {
        try
        {
            var current = _store.LoadAll();
            var known = _cache.Snapshot().ToDictionary(r => r.Key);
            int changed = 0;
            foreach (var rec in current)
            {
                if (known.TryGetValue(rec.Key, out var old)
                    && old.Ref == rec.Ref
                    && old.Fields.Count == rec.Fields.Count
                    && old.Fields.All(kv => rec.Fields.GetValueOrDefault(kv.Key) == kv.Value))
                    continue;
                _cache.Upsert(rec);
                _hub.Broadcast("record", rec);
                changed++;
            }
            var currentKeys = current.Select(r => r.Key).ToHashSet();
            foreach (var goneKey in known.Keys.Where(k => !currentKeys.Contains(k)))
            {
                _cache.Remove(goneKey);
                _hub.Broadcast("remove-record", goneKey);
                changed++;
            }
            if (changed > 0)
                _log.LogInformation("bridge catch-up: {Changed} records reconciled", changed);
            return true;
        }
        catch (Exception e)
        {
            _log.LogWarning("bridge catch-up failed ({Message}) — will retry", e.Message.Split('\n')[0]);
            return false;
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

            // AID-2026-2373 — comment mutations reference the PARENT entity;
            // sync its comment records (covers SPA-side replies, edits, removals).
            var source = doc.RootElement.TryGetProperty("source", out var s) ? s.GetString() : null;
            if (source == "comment")
            {
                var current = _store.FetchCommentsByEntity(ids);
                var currentKeys = current.Select(c => c.Key).ToHashSet();
                foreach (var c in current)
                {
                    _cache.Upsert(c);
                    _hub.Broadcast("record", c);
                }
                foreach (var gone in _cache.Snapshot()
                             .Where(r => r.EntityType == "comment" && ids.Contains(r.ParentKey)
                                         && !currentKeys.Contains(r.Key)))
                {
                    _cache.Remove(gone.Key);
                    _hub.Broadcast("remove-record", gone.Key);
                }
                _log.LogInformation("bridged comment mutation: {Count} comments synced", current.Count);
                return;
            }

            // Refresh the projection from PostgreSQL and patch every session.
            var fresh = _store.FetchByKeys(ids);
            foreach (var rec in fresh)
            {
                _cache.Upsert(rec);
                _hub.Broadcast("record", rec);
            }
            // Ids the fetch did NOT return were (soft-)deleted — drop them.
            foreach (var gone in ids.Except(fresh.Select(r => r.Key)))
            {
                _cache.Remove(gone);
                _hub.Broadcast("remove-record", gone);
            }
            _log.LogInformation("bridged mutation: {Types} → {Fresh} refreshed, {Gone} removed",
                string.Join(",", types), fresh.Count, ids.Length - fresh.Count);
        }
        catch (Exception e)
        {
            _log.LogWarning("bad mutation event: {Message}", e.Message);
        }
    }
}
