using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using Matter2Mqtt.Devices;

namespace Matter2Mqtt.Matter;

/// <summary>
/// <see cref="IMatterController"/> over a python-matter-server WebSocket. Commissioning and
/// node lifecycle parsing are stubbed until the live message set is confirmed (spec §13);
/// Phase-1 automated tests use <see cref="FakeMatterController"/> (spec §11).
/// </summary>
public sealed class PythonMatterServerController(string wsUrl) : IMatterController
{
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _messageId;

    public async IAsyncEnumerable<MatterEvent> ConnectAndListen([EnumeratorCancellation] CancellationToken ct)
    {
        await _socket.ConnectAsync(new Uri(wsUrl), ct);
        await Send(MatterServerProtocol.StartListening(Interlocked.Increment(ref _messageId)), ct);

        var buffer = new byte[64 * 1024];
        while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
        {
            var sb = new StringBuilder();
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer, ct);
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            } while (!result.EndOfMessage);

            foreach (var e in MatterServerProtocol.ParseIncoming(sb.ToString()))
                yield return e;
        }
    }

    public async Task InvokeCommand(ulong nodeId, ushort endpoint, CommandSpec command, CancellationToken ct) =>
        await Send(MatterServerProtocol.DeviceCommand(Interlocked.Increment(ref _messageId), nodeId, endpoint, command), ct);

    public Task<ulong> Commission(string setupCode, CancellationToken ct) =>
        throw new NotImplementedException("Wire commission_with_code against the live server; see spec §13.");

    public Task RemoveNode(ulong nodeId, CancellationToken ct) =>
        throw new NotImplementedException("Wire remove_node against the live server; see spec §13.");

    private async Task Send(string json, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try { await _socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct); }
        finally { _sendLock.Release(); }
    }
}
