using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using Matterhorn.Devices;

namespace Matterhorn.Matter;

/// <summary>
/// <see cref="IMatterController"/> over a python-matter-server WebSocket. Fire-and-forget commands
/// (<c>start_listening</c>, <c>device_command</c>) and request/response commands (<c>commission_with_code</c>,
/// <c>remove_node</c>, correlated by message id via <see cref="PendingRequests"/>) both share the one
/// socket; the receive loop routes replies to waiters and events to the stream.
/// Phase-1 automated tests use <see cref="FakeMatterController"/>.
/// </summary>
public sealed class PythonMatterServerController(string wsUrl) : IMatterController
{
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly PendingRequests _pending = new();
    private int _messageId;

    public async IAsyncEnumerable<MatterEvent> ConnectAndListen([EnumeratorCancellation] CancellationToken ct)
    {
        await _socket.ConnectAsync(new Uri(wsUrl), ct);
        // start_listening replies with the snapshot of nodes already on the fabric, then streams events.
        var listenId = Interlocked.Increment(ref _messageId);
        await Send(MatterServerProtocol.StartListening(listenId), ct);

        var buffer = new byte[64 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, ct);
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close) break;

                var frame = sb.ToString();
                // Replies to our commands carry a message_id; everything else is an event stream frame.
                if (MatterServerProtocol.TryParseResult(frame, out var reply))
                {
                    if (reply.MessageId == listenId)
                        foreach (var e in MatterServerProtocol.ParseNodeList(frame)) yield return e; // initial snapshot
                    else
                        _pending.Complete(reply);
                }
                else
                    foreach (var e in MatterServerProtocol.ParseIncoming(frame))
                        yield return e;
            }
        }
        finally
        {
            // Don't leave a commission/remove caller awaiting a reply that can never arrive.
            _pending.FailAll(new InvalidOperationException("python-matter-server connection closed"));
        }
    }

    public async Task InvokeCommand(ulong nodeId, ushort endpoint, CommandSpec command, CancellationToken ct) =>
        await Send(MatterServerProtocol.DeviceCommand(Interlocked.Increment(ref _messageId), nodeId, endpoint, command), ct);

    public async Task<ulong> Commission(string setupCode, CancellationToken ct)
    {
        var result = await Request(id => MatterServerProtocol.CommissionWithCode(id, setupCode), ct);
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
