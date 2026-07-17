using Matterhorn.Bridge;
using Matterhorn.Devices;

namespace Matterhorn.Matter;

/// <summary>Events surfaced by an <see cref="IMatterController"/>.</summary>
public abstract record MatterEvent;
public record NodeAdded(EndpointInfo Endpoint) : MatterEvent;
public record NodeRemoved(ulong NodeId) : MatterEvent;
public record AttributeChanged(AttributeReading Reading) : MatterEvent;
public record ReachabilityChanged(ulong NodeId, ushort Endpoint, bool Reachable) : MatterEvent;

/// <summary>
/// What the controller says about itself — the first frame it sends on connect. Worth surfacing
/// because <see cref="BluetoothEnabled"/> is the difference between "commissioning a new Thread
/// device works" and a failure with no obvious cause.
/// </summary>
public record ControllerInfo(string? SdkVersion, int SchemaVersion, bool BluetoothEnabled, bool ThreadCredentialsSet)
    : MatterEvent;
