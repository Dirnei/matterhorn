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

/// <summary>Published on the EventStream when the device set changes (feeds SSE).</summary>
public record DeviceListChanged;

/// <summary>Route a partial state change to a device by its stable key (group/scene fan-out).
/// No-op if no endpoint is registered under the key.</summary>
public record RouteSet((ulong NodeId, ushort Endpoint) Key, IReadOnlyDictionary<string, JsonElement> Payload);

/// <summary>Read a device's current state by stable key; replied to with <see cref="Matterhorn.Devices.DeviceStateSnapshot"/>
/// (Found:false if no endpoint is registered under the key). Used by scene snapshot capture.</summary>
public record RouteGetState((ulong NodeId, ushort Endpoint) Key);

/// <summary>Published on the EventStream when a device joins or is renamed (upsert key→name).
/// Consumed by the group/scene supervisors' name↔key read-model.</summary>
public record DeviceRegistered((ulong NodeId, ushort Endpoint) Key, string FriendlyName);

/// <summary>Published on the EventStream per endpoint when a node is removed — drives group/scene pruning.</summary>
public record DeviceRemoved((ulong NodeId, ushort Endpoint) Key);

/// <summary>Startup wiring: hand the gateway the groups supervisor so it can forward group sets.</summary>
public record RegisterGroups(Akka.Actor.IActorRef Groups);

/// <summary>Startup wiring: hand the gateway the scenes supervisor (symmetry with RegisterGroups).</summary>
public record RegisterScenes(Akka.Actor.IActorRef Scenes);
