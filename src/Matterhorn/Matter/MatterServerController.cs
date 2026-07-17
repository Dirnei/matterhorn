using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using Matterhorn.Devices;

namespace Matterhorn.Matter;

/// <summary>
/// <see cref="IMatterController"/> over a Matter-server WebSocket (matterjs-server, or the archived
/// python-matter-server — same API). Fire-and-forget commands (<c>start_listening</c>,
/// <c>device_command</c>) and request/response commands (<c>commission_with_code</c>,
/// <c>remove_node</c>, correlated by message id via <see cref="PendingRequests"/>) both share the one
/// socket; the receive loop routes replies to waiters and events to the stream.
/// <para>
/// <see cref="ConnectAndListen"/> is a <em>durable</em> stream: it reconnects (fresh socket, backoff)
/// when the server drops — e.g. the controller host reboots — so the gateway never has to know the
/// transport blinked. On each (re)connection it re-issues <c>start_listening</c>, which replays the
/// node snapshot; the gateway absorbs that idempotently.
/// </para>
/// Automated tests use <see cref="FakeMatterController"/> instead.
/// </summary>
public sealed class MatterServerController(string wsUrl) : IMatterController
{
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    private static readonly List<MatterEvent> None = new();

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly PendingRequests _pending = new();
    // Replaced on every (re)connection; Send always uses the current one. A send during a
    // disconnected gap throws, which faults the calling command — the correct outcome.
    private volatile ClientWebSocket _socket = new();
    private int _messageId;

    public async IAsyncEnumerable<MatterEvent> ConnectAndListen([EnumeratorCancellation] CancellationToken ct)
    {
        var backoff = MinBackoff;
        while (!ct.IsCancellationRequested)
        {
            var socket = new ClientWebSocket();
            var listenId = 0;
            var connected = false;
            try
            {
                await socket.ConnectAsync(new Uri(wsUrl), ct);
                _socket = socket;
                // start_listening replies with the snapshot of nodes already on the fabric, then streams events.
                listenId = Interlocked.Increment(ref _messageId);
                await Send(MatterServerProtocol.StartListening(listenId), ct);
                connected = true;
            }
            catch (OperationCanceledException) { socket.Dispose(); yield break; }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[matter-server] connect to {wsUrl} failed: {e.Message}");
            }

            if (connected)
            {
                backoff = MinBackoff; // a good connection resets the backoff
                await foreach (var e in Listen(socket, listenId, ct))
                    yield return e;
            }

            // Session ended (drop or a failed connect): fault in-flight callers instead of leaving
            // them hung, discard the spent socket, then back off before trying again.
            _pending.FailAll(new InvalidOperationException("matter-server connection closed"));
            socket.Dispose();
            if (ct.IsCancellationRequested) break;
            var delay = backoff;
            var cancelled = false;
            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { cancelled = true; }
            if (cancelled) break;
            backoff = TimeSpan.FromMilliseconds(Math.Min(MaxBackoff.TotalMilliseconds, backoff.TotalMilliseconds * 2));
        }
    }

    /// <summary>One connection's receive loop. Returns (never throws for a drop) when the socket
    /// closes or errors, handing control back to the reconnect loop.</summary>
    private async IAsyncEnumerable<MatterEvent> Listen(
        ClientWebSocket socket, int listenId, [EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            string? frame = null;
            var ended = false;
            try { frame = await ReceiveFrame(socket, buffer, ct); }
            catch (OperationCanceledException) { throw; } // cancellation = shutdown, propagate
            catch (Exception e)
            {
                Console.Error.WriteLine($"[matter-server] connection lost: {e.Message}");
                ended = true;
            }
            if (ended || frame is null) break; // null = clean close

            List<MatterEvent> events;
            try { events = Classify(frame, listenId); }
            catch (Exception e)
            {
                // One malformed frame must not tear down an otherwise healthy session.
                Console.Error.WriteLine($"[matter-server] skipped an unparseable frame: {e.Message}");
                continue;
            }
            foreach (var e in events) yield return e;
        }
    }

    /// <summary>Turns a frame into events, or routes a command reply to its waiter (returning none).</summary>
    private List<MatterEvent> Classify(string frame, int listenId)
    {
        // Replies to our commands carry a message_id; everything else is an event stream frame.
        if (MatterServerProtocol.TryParseResult(frame, out var reply))
        {
            if (reply.MessageId == listenId)
                return MatterServerProtocol.ParseNodeList(frame).ToList(); // initial snapshot
            // A fire-and-forget command (device_command / write_attribute) has no registered waiter,
            // so its reply lands here unclaimed. Surface an error the controller reported for one of
            // those instead of dropping it silently — otherwise a rejected command looks like success.
            if (!_pending.Complete(reply) && reply.Error is not null)
                Console.Error.WriteLine($"[matter-server] command {reply.MessageId} rejected: {reply.Error}");
            return None;
        }
        return MatterServerProtocol.ParseIncoming(frame).ToList();
    }

    private static async Task<string?> ReceiveFrame(ClientWebSocket socket, byte[] buffer, CancellationToken ct)
    {
        var sb = new StringBuilder();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        } while (!result.EndOfMessage);
        return sb.ToString();
    }

    public async Task InvokeCommand(ulong nodeId, ushort endpoint, CommandSpec command, CancellationToken ct) =>
        await Send(MatterServerProtocol.DeviceCommand(Interlocked.Increment(ref _messageId), nodeId, endpoint, command), ct);

    public async Task WriteAttribute(ulong nodeId, ushort endpoint, uint clusterId, uint attributeId, object? value, CancellationToken ct) =>
        await Send(MatterServerProtocol.WriteAttribute(Interlocked.Increment(ref _messageId), nodeId, endpoint, clusterId, attributeId, value), ct);

    public async Task SetThreadDataset(string dataset, CancellationToken ct)
    {
        var result = await Request(id => MatterServerProtocol.SetThreadDataset(id, dataset), ct);
        if (result.Error is not null) throw new InvalidOperationException($"Setting the Thread dataset failed: {result.Error}");
    }

    public async Task<ulong> Commission(string setupCode, bool networkOnly, CancellationToken ct)
    {
        var result = await Request(id => MatterServerProtocol.CommissionWithCode(id, setupCode, networkOnly), ct);
        if (result.Error is not null) throw new InvalidOperationException($"Commissioning failed: {result.Error}");
        return result.NodeId ?? throw new InvalidOperationException("Commissioning reply carried no node_id");
    }

    public async Task RemoveNode(ulong nodeId, CancellationToken ct)
    {
        var result = await Request(id => MatterServerProtocol.RemoveNode(id, nodeId), ct);
        if (result.Error is not null) throw new InvalidOperationException($"Removing node {nodeId} failed: {result.Error}");
    }

    /// <summary>Sends a command under a fresh message id and awaits its correlated reply.</summary>
    private async Task<ServerResult> Request(Func<int, string> command, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _messageId);
        var reply = _pending.Register(id);
        await Send(command(id), ct);
        return await reply.WaitAsync(ct);
    }

    private async Task Send(string json, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try { await _socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct); }
        finally { _sendLock.Release(); }
    }
}
