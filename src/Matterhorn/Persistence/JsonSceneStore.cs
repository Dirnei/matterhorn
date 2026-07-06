using System.Text.Json;

namespace Matterhorn.Persistence;

/// <summary>Stores scenes as { "<scene>": { "<node>_<endpoint>": { "<prop>": value, ... } } }.
/// Missing/corrupt file loads empty; writes swap atomically via a temp file.</summary>
public sealed class JsonSceneStore(string path) : ISceneStore
{
    public IReadOnlyDictionary<string, IReadOnlyDictionary<(ulong, ushort), IReadOnlyDictionary<string, JsonElement>>> Load()
    {
        var result = new Dictionary<string, IReadOnlyDictionary<(ulong, ushort), IReadOnlyDictionary<string, JsonElement>>>();
        if (!File.Exists(path)) return result;
        Dictionary<string, Dictionary<string, Dictionary<string, JsonElement>>>? raw;
        try { raw = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Dictionary<string, JsonElement>>>>(File.ReadAllText(path)); }
        catch (JsonException) { return result; }
        if (raw is null) return result;
        foreach (var (scene, members) in raw)
        {
            var map = new Dictionary<(ulong, ushort), IReadOnlyDictionary<string, JsonElement>>();
            foreach (var (keyStr, props) in members)
                if (DeviceKeys.TryParse(keyStr, out var key)) map[key] = props;
            result[scene] = map;
        }
        return result;
    }

    public void Save(IReadOnlyDictionary<string, IReadOnlyDictionary<(ulong, ushort), IReadOnlyDictionary<string, JsonElement>>> scenes)
    {
        var raw = scenes.ToDictionary(
            s => s.Key,
            s => s.Value.ToDictionary(m => DeviceKeys.Format(m.Key), m => m.Value));
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(raw, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }
}
