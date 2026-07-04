namespace Matterhorn.Persistence;

/// <summary>Persists the user's device name overrides, keyed by stable (nodeId, endpoint).</summary>
public interface INameStore
{
    IReadOnlyDictionary<(ulong NodeId, ushort Endpoint), string> Load();
    void Save(IReadOnlyDictionary<(ulong NodeId, ushort Endpoint), string> names);
}
