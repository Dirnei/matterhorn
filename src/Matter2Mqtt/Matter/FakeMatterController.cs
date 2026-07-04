using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Matter2Mqtt.Devices;

namespace Matter2Mqtt.Matter;

/// <summary>In-memory <see cref="IMatterController"/> for tests and dev (spec §11).</summary>
public sealed class FakeMatterController : IMatterController
{
    private readonly Channel<MatterEvent> _channel = Channel.CreateUnbounded<MatterEvent>();
    private readonly List<(ulong, ushort, CommandSpec)> _invocations = new();

    public IReadOnlyList<(ulong NodeId, ushort Endpoint, CommandSpec Cmd)> Invocations => _invocations;
    public Func<string, ulong> OnCommission { get; set; } = _ => 1;
    public List<ulong> Removed { get; } = new();

    public void Emit(MatterEvent e) => _channel.Writer.TryWrite(e);

    public async IAsyncEnumerable<MatterEvent> ConnectAndListen([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var e in _channel.Reader.ReadAllAsync(ct))
            yield return e;
    }

    public Task InvokeCommand(ulong nodeId, ushort endpoint, CommandSpec command, CancellationToken ct)
    {
        lock (_invocations) _invocations.Add((nodeId, endpoint, command));
        return Task.CompletedTask;
    }

    public Task<ulong> Commission(string setupCode, CancellationToken ct) => Task.FromResult(OnCommission(setupCode));
    public Task RemoveNode(ulong nodeId, CancellationToken ct) { Removed.Add(nodeId); return Task.CompletedTask; }
}
