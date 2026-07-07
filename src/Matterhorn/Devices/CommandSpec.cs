namespace Matterhorn.Devices;

/// <summary>A single write action a <c>/set</c> expands to — a cluster command or an attribute write.</summary>
public interface IDeviceWrite;

/// <summary>A Matter cluster command to invoke on a node endpoint.</summary>
public record CommandSpec(uint ClusterId, string CommandName, IReadOnlyDictionary<string, object?> Payload) : IDeviceWrite;

/// <summary>A Matter attribute write (used where a cluster is configured by attribute, not command).</summary>
public record AttributeWriteSpec(uint ClusterId, uint AttributeId, object? Value) : IDeviceWrite;
