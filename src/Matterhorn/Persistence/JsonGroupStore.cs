using System.Text.Json;

namespace Matterhorn.Persistence;

/// <summary>Stores groups as { "<group>": { "members": ["<node>_<endpoint>", ...] } }.
/// Missing/corrupt file loads empty; writes swap atomically via a temp file (never a torn write).</summary>
public sealed class JsonGroupStore(string path) : IGroupStore
{
    private sealed record Entry(List<string> members);

    public IReadOnlyDictionary<string, IReadOnlyList<(ulong, ushort)>> Load()
    {
        var result = new Dictionary<string, IReadOnlyList<(ulong, ushort)>>();
        if (!File.Exists(path)) return result;
        Dictionary<string, Entry>? raw;
        try { raw = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path)); }
        catch (JsonException) { return result; }
        if (raw is null) return result;
        foreach (var (name, entry) in raw)
        {
            var keys = new List<(ulong, ushort)>();
            foreach (var m in entry.members ?? new())
                if (DeviceKeys.TryParse(m, out var key)) keys.Add(key);
            result[name] = keys;
        }
        return result;
    }

    public void Save(IReadOnlyDictionary<string, IReadOnlyList<(ulong, ushort)>> groups)
    {
        var raw = groups.ToDictionary(
            kv => kv.Key,
            kv => new Entry(kv.Value.Select(DeviceKeys.Format).ToList()));
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(raw, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }
}
