namespace Matterhorn.Bridge;

/// <summary>Everything known about one logical device (node endpoint) at join time.</summary>
public record EndpointInfo(
    ulong NodeId, ushort Endpoint, string? VendorName, string? ProductName,
    ushort VendorId, ushort ProductId, string DeviceType, bool Reachable,
    IReadOnlyList<uint> ClusterIds, string Transport = "unknown", uint ColorFeatures = 0);

/// <summary>A Z2M-style exposes entry. <see cref="Access"/> is a bitmask (1=published, 2=set, 4=get).
/// <see cref="Values"/> lists the allowed values for <c>type == "enum"</c>.</summary>
public record ExposeEntry(
    string Type, string Property, int Access,
    string? ValueOn = null, string? ValueOff = null,
    int? ValueMin = null, int? ValueMax = null, string? Unit = null,
    IReadOnlyList<string>? Values = null);

/// <summary>A <c>bridge/devices</c> entry — the discovery contract.</summary>
public record DeviceDescriptor(
    string FriendlyName, string NodeId, ushort Endpoint,
    string? VendorName, string? ProductName, ushort VendorId, ushort ProductId,
    string DeviceType, bool Reachable, IReadOnlyList<ExposeEntry> Exposes, string Transport = "unknown");
