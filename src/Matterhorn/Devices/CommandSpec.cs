namespace Matterhorn.Devices;

/// <summary>A Matter cluster command to invoke on a node endpoint.</summary>
public record CommandSpec(uint ClusterId, string CommandName, IReadOnlyDictionary<string, object?> Payload);
