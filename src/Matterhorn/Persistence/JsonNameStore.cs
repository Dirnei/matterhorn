using System.Text.Json;

namespace Matterhorn.Persistence;

/// <summary>
/// Stores the override map as one JSON object keyed "&lt;nodeId&gt;_&lt;endpoint&gt;" → custom name,
/// e.g. { "5_1": "living_room_lamp" }. A missing file loads as empty.
/// </summary>
public sealed class JsonNameStore(string path) : INameStore
{
    public IReadOnlyDictionary<(ulong NodeId, ushort Endpoint), string> Load()
    {
        var result = new Dictionary<(ulong, ushort), string>();
        if (!File.Exists(path)) return result;
        Dictionary<string, string>? raw;
        try { raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)); }
        catch (JsonException) { return result; }
        if (raw is null) return result;
        foreach (var (key, name) in raw)
            if (TryParseKey(key, out var parsed)) result[parsed] = name;
        return result;
    }

    public void Save(IReadOnlyDictionary<(ulong NodeId, ushort Endpoint), string> names)
    {
        var raw = names.ToDictionary(kv => $"{kv.Key.NodeId}_{kv.Key.Endpoint}", kv => kv.Value);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(raw, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static bool TryParseKey(string key, out (ulong, ushort) parsed)
    {
        parsed = default;
        var parts = key.Split('_');
        if (parts.Length != 2) return false;
        if (!ulong.TryParse(parts[0], out var node) || !ushort.TryParse(parts[1], out var ep)) return false;
        parsed = (node, ep);
        return true;
    }
}
