using System.Text.Json;

namespace Matter2Mqtt.Devices;

/// <summary>Messages handled by <see cref="MatterEndpointActor"/>.</summary>
public record ApplyAttribute(AttributeReading Reading);
public record ApplySet(IReadOnlyDictionary<string, JsonElement> Payload);
public record SetReachable(bool Reachable);

/// <summary>Re-publish current retained state + availability (e.g. after an MQTT reconnect).</summary>
public record Republish;

/// <summary>Ask an endpoint for its current state; replied to with <see cref="DeviceStateSnapshot"/>.</summary>
public record GetState;
public record DeviceStateSnapshot(bool Found, IReadOnlyDictionary<string, object?>? State);
