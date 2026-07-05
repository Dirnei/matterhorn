using System.Text.Json;

namespace Matterhorn.Groups;

/// <summary>Tell a <see cref="GroupActor"/> to fan out a set to its members and echo it.</summary>
public record ApplyGroupSet(IReadOnlyDictionary<string, JsonElement> Payload);

/// <summary>Supervisor pushes a group's new member set to its entity.</summary>
public record UpdateGroupMembers(IReadOnlyList<(ulong NodeId, ushort Endpoint)> Members);

/// <summary>Supervisor pushes a group's new name; the entity clears the old retained topic.</summary>
public record RenameGroupEntity(string NewName);
