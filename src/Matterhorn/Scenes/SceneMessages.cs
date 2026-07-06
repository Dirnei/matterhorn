using System.Text.Json;

namespace Matterhorn.Scenes;

/// <summary>Tell a <see cref="SceneActor"/> to fan out its stored values.</summary>
public record RecallScene;

/// <summary>Supervisor pushes a scene's new stored snapshot to its entity.</summary>
public record UpdateSceneValues(
    IReadOnlyDictionary<(ulong NodeId, ushort Endpoint), IReadOnlyDictionary<string, JsonElement>> Values);
