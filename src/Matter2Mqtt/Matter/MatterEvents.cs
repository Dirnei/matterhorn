using Matter2Mqtt.Bridge;
using Matter2Mqtt.Devices;

namespace Matter2Mqtt.Matter;

/// <summary>Events surfaced by an <see cref="IMatterController"/> (spec §3).</summary>
public abstract record MatterEvent;
public record NodeAdded(EndpointInfo Endpoint) : MatterEvent;
public record NodeRemoved(ulong NodeId, ushort Endpoint) : MatterEvent;
public record AttributeChanged(AttributeReading Reading) : MatterEvent;
public record ReachabilityChanged(ulong NodeId, ushort Endpoint, bool Reachable) : MatterEvent;
