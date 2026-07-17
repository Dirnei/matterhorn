using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Matterhorn.Devices;

namespace Matterhorn.Matter;

/// <summary>In-memory <see cref="IMatterController"/> for tests and dev.</summary>
public sealed class FakeMatterController : IMatterController
{
    private readonly Channel<MatterEvent> _channel = Channel.CreateUnbounded<MatterEvent>();
    private readonly List<(ulong, ushort, CommandSpec)> _invocations = new();
    private readonly List<(ulong, ushort, uint, uint, object?)> _attributeWrites = new();

    public IReadOnlyList<(ulong NodeId, ushort Endpoint, CommandSpec Cmd)> Invocations => _invocations;
    public IReadOnlyList<(ulong NodeId, ushort Endpoint, uint ClusterId, uint AttributeId, object? Value)> AttributeWrites => _attributeWrites;
    public Func<string, ulong> OnCommission { get; set; } = _ => 1;
    public List<ulong> Removed { get; } = new();

    /// <summary>Datasets handed to <see cref="SetThreadDataset"/>, in order.</summary>
    public List<string> ThreadDatasets { get; } = new();

    /// <summary>Every <see cref="Commission"/> call, so a test can assert on the network_only choice.</summary>
    public List<(string SetupCode, bool NetworkOnly)> Commissions { get; } = new();

    /// <summary>When set, <see cref="Commission"/> returns this (still-pending) task instead of
    /// completing synchronously — lets a test hold a commission in flight to prove the gateway
    /// stays responsive while it runs.</summary>
    public Task<ulong>? PendingCommission { get; set; }

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

    public Task WriteAttribute(ulong nodeId, ushort endpoint, uint clusterId, uint attributeId, object? value, CancellationToken ct)
    {
        lock (_attributeWrites) _attributeWrites.Add((nodeId, endpoint, clusterId, attributeId, value));
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

    /// <summary>When set, <see cref="SetThreadDataset"/> delegates to this — lets a test make the
    /// push fail the way a controller rejecting the dataset would.</summary>
    public Func<string, Task>? OnSetThreadDataset { get; set; }

    public Task SetThreadDataset(string dataset, CancellationToken ct)
    {
        ThreadDatasets.Add(dataset);
        return OnSetThreadDataset?.Invoke(dataset) ?? Task.CompletedTask;
    }

    public Task<ulong> Commission(string setupCode, bool networkOnly, CancellationToken ct)
    {
        Commissions.Add((setupCode, networkOnly));
        return PendingCommission ?? Task.FromResult(OnCommission(setupCode));
    }
    public Task RemoveNode(ulong nodeId, CancellationToken ct) { Removed.Add(nodeId); return Task.CompletedTask; }
}
