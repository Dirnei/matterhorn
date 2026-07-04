using System.Text.Json;

namespace Matter2Mqtt.Devices;

/// <summary>Messages handled by <see cref="MatterEndpointActor"/>.</summary>
public record ApplyAttribute(AttributeReading Reading);
public record ApplySet(IReadOnlyDictionary<string, JsonElement> Payload);
public record SetReachable(bool Reachable);
