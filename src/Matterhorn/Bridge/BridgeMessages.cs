using System.Text.Json;

namespace Matterhorn.Bridge;

/// <summary>Control-plane messages handled by <see cref="MatterGatewayActor"/>.</summary>
public record CommissionRequest(string Code, string Transaction);
public record RemoveRequest(string FriendlyName, string Transaction);
/// <summary>Reply to a <see cref="RemoveRequest"/>: whether the device was known (drives REST 202 vs 404).</summary>
public record RemoveAccepted(bool Found);
public record RenameRequest(string FromName, string ToName, string Transaction);

/// <summary>Reply to a <see cref="RenameRequest"/>. Error is one of: invalid_name, not_found, name_taken.</summary>
public record RenameResult(bool Ok, string? Error, string? NewName = null);
public record GetDevices;
public record GetDeviceState(string FriendlyName);
public record SetDevice(string FriendlyName, IReadOnlyDictionary<string, JsonElement> Payload);

/// <summary>Signals the broker (re)connected — re-announce all retained bridge + device state.</summary>
public record MqttConnected;

/// <summary>HA's birth message appeared on {discoveryPrefix}/status — re-announce discovery configs and states.</summary>
public record HaStatusOnline;

/// <summary>Published on the EventStream when the device set changes (feeds SSE).</summary>
public record DeviceListChanged;
