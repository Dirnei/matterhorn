using System.Text.Json;

namespace Matterhorn.Persistence;

/// <summary>Persists scenes: scene name → (stable device key → target property payload).</summary>
public interface ISceneStore
{
    IReadOnlyDictionary<string, IReadOnlyDictionary<(ulong NodeId, ushort Endpoint), IReadOnlyDictionary<string, JsonElement>>> Load();
    void Save(IReadOnlyDictionary<string, IReadOnlyDictionary<(ulong NodeId, ushort Endpoint), IReadOnlyDictionary<string, JsonElement>>> scenes);
}
