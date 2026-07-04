using System.Runtime.CompilerServices;
using System.Text.Json;
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

    /// <summary>
    /// When true, an invoked command is reflected back as the attribute change a real device would
    /// report (optimistic echo) — so a <c>/set</c> visibly updates retained state. Off by default.
    /// </summary>
    public bool EchoCommandsAsAttributes { get; set; }

    public void Emit(MatterEvent e) => _channel.Writer.TryWrite(e);

    public async IAsyncEnumerable<MatterEvent> ConnectAndListen([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var e in _channel.Reader.ReadAllAsync(ct))
            yield return e;
    }

    public Task InvokeCommand(ulong nodeId, ushort endpoint, CommandSpec command, CancellationToken ct)
    {
        lock (_invocations) _invocations.Add((nodeId, endpoint, command));
        if (EchoCommandsAsAttributes)
            foreach (var reading in Echo(nodeId, endpoint, command))
                Emit(new AttributeChanged(reading));
        return Task.CompletedTask;
    }

    private static IEnumerable<AttributeReading> Echo(ulong node, ushort ep, CommandSpec cmd)
    {
        static JsonElement El(object? v) => JsonSerializer.SerializeToElement(v);
        var list = new List<AttributeReading>();
        switch (cmd.CommandName)
        {
            case "On":
                list.Add(new(node, ep, MatterClusters.OnOff, 0, El(true)));
                break;
            case "Off":
                list.Add(new(node, ep, MatterClusters.OnOff, 0, El(false)));
                break;
            case "MoveToLevelWithOnOff":
                list.Add(new(node, ep, MatterClusters.OnOff, 0, El(true)));
                if (cmd.Payload.TryGetValue("level", out var level))
                    list.Add(new(node, ep, MatterClusters.LevelControl, 0, El(level)));
                break;
            case "MoveToColorTemperature":
                if (cmd.Payload.TryGetValue("colorTemperatureMireds", out var mireds))
                    list.Add(new(node, ep, MatterClusters.ColorControl, 7, El(mireds)));
                break;
        }
        return list;
    }

    public Task<ulong> Commission(string setupCode, CancellationToken ct) => Task.FromResult(OnCommission(setupCode));
    public Task RemoveNode(ulong nodeId, CancellationToken ct) { Removed.Add(nodeId); return Task.CompletedTask; }
}
