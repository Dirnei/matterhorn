using Matterhorn.Persistence;

namespace Matterhorn.Test.Persistence;

/// <summary>Test double: holds the override map in memory and records saves.</summary>
public sealed class InMemoryNameStore : INameStore
{
    public Dictionary<(ulong NodeId, ushort Endpoint), string> Names { get; } = new();
    public IReadOnlyDictionary<(ulong NodeId, ushort Endpoint), string> Load() => Names;
    public void Save(IReadOnlyDictionary<(ulong NodeId, ushort Endpoint), string> names)
    {
        Names.Clear();
        foreach (var (k, v) in names) Names[k] = v;
    }
}
