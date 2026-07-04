using Matterhorn.Persistence;

namespace Matterhorn.Test.Persistence;

/// <summary>Test double: loads empty and always throws on Save (simulates a read-only data dir).</summary>
public sealed class ThrowingNameStore : INameStore
{
    public IReadOnlyDictionary<(ulong NodeId, ushort Endpoint), string> Load() =>
        new Dictionary<(ulong, ushort), string>();
    public void Save(IReadOnlyDictionary<(ulong NodeId, ushort Endpoint), string> names) =>
        throw new UnauthorizedAccessException("data dir is read-only");
}
