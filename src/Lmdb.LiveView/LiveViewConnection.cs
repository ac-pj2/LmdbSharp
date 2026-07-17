// An in-process LiveView connection for embedded hosts — an Android WebView
// bridge, a desktop shell, a test harness. Same session lifecycle and wire
// format as a WebSocket connection; the caller pumps the transport instead of
// a socket: forward each outbound frame to the client runtime, and hand each
// client frame to Deliver.
using System.Runtime.CompilerServices;
using System.Text;

namespace Lmdb.LiveView;

public sealed class LiveViewConnection : IDisposable
{
    private readonly LiveViewHub _hub;

    internal LiveViewConnection(LiveViewHub hub, DeltaLiveView view)
    {
        _hub = hub;
        View = view;
    }

    public DeltaLiveView View { get; }

    /// <summary>Outbound frames (init, patches, navigation) in delivery order,
    /// as UTF-8 JSON — exactly the WebSocket wire format. Completes when the
    /// connection is disposed.</summary>
    public async IAsyncEnumerable<byte[]> ReadOutboundAsync(
        [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        await foreach (var frame in View.Outbound.Reader.ReadAllAsync(cancellation)
            .ConfigureAwait(false))
        {
            yield return frame;

            // Mirror the WebSocket pump: if patches were dropped because the
            // consumer fell behind, schedule a full resync on the view's loop.
            if (View.ConsumeResyncFlag())
                View.Inbox.Writer.TryWrite(new InboxMessage(InboxKind.Resync, null, null, default));
        }
    }

    /// <summary>Deliver one client event frame ({"t":...,"d":{...}}) from the
    /// client runtime — the body a WebSocket text message would carry.</summary>
    public void Deliver(string frame)
        => LiveViewHub.EnqueueEvent(Encoding.UTF8.GetBytes(frame), View);

    public void Dispose() => _hub.Teardown(View);
}
