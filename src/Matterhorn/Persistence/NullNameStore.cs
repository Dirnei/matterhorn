namespace Matterhorn.Persistence;

/// <summary>No-op store — the default when persistence is not configured (e.g. unit tests).</summary>
public sealed class NullNameStore : INameStore
{
    public static readonly NullNameStore Instance = new();
    public IReadOnlyDictionary<(ulong NodeId, ushort Endpoint), string> Load() =>
        new Dictionary<(ulong, ushort), string>();
    public void Save(IReadOnlyDictionary<(ulong NodeId, ushort Endpoint), string> names) { }
}
