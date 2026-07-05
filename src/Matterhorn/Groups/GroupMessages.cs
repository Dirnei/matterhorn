using System.Text.Json;

namespace Matterhorn.Groups;

/// <summary>Tell a <see cref="GroupActor"/> to fan out a set to its members and echo it.</summary>
public record ApplyGroupSet(IReadOnlyDictionary<string, JsonElement> Payload);

/// <summary>Supervisor pushes a group's new member set to its entity.</summary>
public record UpdateGroupMembers(IReadOnlyList<(ulong NodeId, ushort Endpoint)> Members);

/// <summary>Supervisor pushes a group's new name; the entity clears the old retained topic.</summary>
public record RenameGroupEntity(string NewName);

/// <summary>Create-or-replace a group with an initial member set (device friendly names).</summary>
public record CreateGroup(string Name, IReadOnlyList<string> MemberDevices, string Transaction);
public record DeleteGroup(string Name, string Transaction);
public record RenameGroup(string From, string To, string Transaction);
public record AddGroupMember(string Group, string Device, string Transaction);
public record RemoveGroupMember(string Group, string Device, string Transaction);

/// <summary>A set addressed to a group (from REST PATCH or MQTT &lt;group&gt;/set).</summary>
public record GroupSet(string Name, IReadOnlyDictionary<string, System.Text.Json.JsonElement> Payload);

/// <summary>Reply. Error ∈ invalid_name | not_found | name_taken | collides_with_device.</summary>
public record GroupOpResult(bool Ok, string? Error, string? Name = null);

public record GetGroups;
public record GroupView(string FriendlyName, IReadOnlyList<string> Members);

/// <summary>EventStream: the set of group names changed (gateway consumes for rename-collision checks).</summary>
public record GroupNamesChanged(IReadOnlySet<string> Names);

/// <summary>EventStream: the group list changed (SSE consumes).</summary>
public record GroupListChanged;
