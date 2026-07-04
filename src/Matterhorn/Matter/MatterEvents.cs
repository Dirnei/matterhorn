using Matterhorn.Bridge;
using Matterhorn.Devices;

namespace Matterhorn.Matter;

/// <summary>Events surfaced by an <see cref="IMatterController"/> (spec §3).</summary>
public abstract record MatterEvent;
public record NodeAdded(EndpointInfo Endpoint) : MatterEvent;
public record NodeRemoved(ulong NodeId) : MatterEvent;
public record AttributeChanged(AttributeReading Reading) : MatterEvent;
public record ReachabilityChanged(ulong NodeId, ushort Endpoint, bool Reachable) : MatterEvent;
