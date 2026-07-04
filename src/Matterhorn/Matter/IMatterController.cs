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
    Task<ulong> Commission(string setupCode, CancellationToken ct);
    Task RemoveNode(ulong nodeId, CancellationToken ct);
}
