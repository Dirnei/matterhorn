using System.Text.Json;

namespace Matter2Mqtt.Devices;

/// <summary>A single Matter attribute value read from a node endpoint.</summary>
public record AttributeReading(ulong NodeId, ushort Endpoint, uint ClusterId, uint AttributeId, JsonElement Value);
