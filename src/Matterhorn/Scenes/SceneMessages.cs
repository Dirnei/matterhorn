using System.Text.Json;

namespace Matterhorn.Scenes;

/// <summary>Tell a <see cref="SceneActor"/> to fan out its stored values.</summary>
public record RecallScene;

/// <summary>Supervisor pushes a scene's new stored snapshot to its entity.</summary>
public record UpdateSceneValues(
    IReadOnlyDictionary<(ulong NodeId, ushort Endpoint), IReadOnlyDictionary<string, JsonElement>> Values);

/// <summary>Store (create/overwrite) a scene. When <see cref="ExplicitState"/> is null, the supervisor
/// captures a live snapshot of each device's current settable properties via the gateway.</summary>
public record StoreScene(string Name, IReadOnlyList<string> Devices,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>>? ExplicitState, string Transaction);

public record DeleteScene(string Name, string Transaction);
public record RenameScene(string From, string To, string Transaction);
public record RecallSceneByName(string Name, string Transaction);

/// <summary>Reply to scene ops. Error is one of: invalid_name, not_found, name_taken.</summary>
public record SceneOpResult(bool Ok, string? Error, string? Name = null);

public record GetScenes;
public record SceneView(string FriendlyName, IReadOnlyList<string> Members);

/// <summary>Published on the EventStream when the scene list changes (feeds SSE).</summary>
public record SceneListChanged;
