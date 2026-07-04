using System.Text.Json;

namespace Matter2Mqtt.Bridge;

/// <summary>Control-plane messages handled by <see cref="MatterGatewayActor"/> (spec §6, §8).</summary>
public record CommissionRequest(string Code, string Transaction);
public record RemoveRequest(string FriendlyName, string Transaction);
public record GetDevices;
public record GetDeviceState(string FriendlyName);
public record DeviceStateReply(bool Found, string? Json);
public record SetDevice(string FriendlyName, IReadOnlyDictionary<string, JsonElement> Payload);

/// <summary>Signals the broker (re)connected — re-announce all retained bridge + device state.</summary>
public record MqttConnected;
