namespace Matterhorn.Persistence;

/// <summary>Persists group membership, keyed by group name → stable device keys.</summary>
public interface IGroupStore
{
    IReadOnlyDictionary<string, IReadOnlyList<(ulong NodeId, ushort Endpoint)>> Load();
    void Save(IReadOnlyDictionary<string, IReadOnlyList<(ulong NodeId, ushort Endpoint)>> groups);
}
