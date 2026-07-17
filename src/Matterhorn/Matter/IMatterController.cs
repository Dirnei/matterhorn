using Matterhorn.Devices;

namespace Matterhorn.Matter;

/// <summary>
/// The seam over the upstream Matter controller. All controller I/O sits
/// behind this interface so the upstream is swappable (a live matter-server WebSocket ↔ the fake).
/// </summary>
public interface IMatterController
{
    IAsyncEnumerable<MatterEvent> ConnectAndListen(CancellationToken ct);
    Task InvokeCommand(ulong nodeId, ushort endpoint, CommandSpec command, CancellationToken ct);
    Task WriteAttribute(ulong nodeId, ushort endpoint, uint clusterId, uint attributeId, object? value, CancellationToken ct);

    /// <summary>
    /// Hands the controller the Thread network credentials to pass on when it commissions a device.
    /// Must be called before commissioning a device that is not already on a network, because
    /// handing the dataset over is what the BLE session exists to do.
    /// </summary>
    Task SetThreadDataset(string dataset, CancellationToken ct);

    /// <summary>
    /// Commissions a device by its setup code. <paramref name="networkOnly"/> restricts the
    /// controller to devices already reachable over IP; clear it to also let it reach a
    /// factory-fresh device over BLE.
    /// </summary>
    Task<ulong> Commission(string setupCode, bool networkOnly, CancellationToken ct);
    Task RemoveNode(ulong nodeId, CancellationToken ct);
}
