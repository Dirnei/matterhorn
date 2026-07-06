using System.Text.Json;
using Matterhorn.Persistence;

namespace Matterhorn.Test.Persistence;

public class JsonSceneStoreTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"scenes-{Guid.NewGuid():N}.json");
    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement.Clone();

    [Fact]
    public void Load_missing_file_returns_empty() => Assert.Empty(new JsonSceneStore(TempFile()).Load());

    [Fact]
    public void Save_then_load_round_trips_values()
    {
        var path = TempFile();
        try
        {
            new JsonSceneStore(path).Save(new Dictionary<string, IReadOnlyDictionary<(ulong, ushort), IReadOnlyDictionary<string, JsonElement>>>
            {
                ["movie"] = new Dictionary<(ulong, ushort), IReadOnlyDictionary<string, JsonElement>>
                {
                    [(5, 1)] = new Dictionary<string, JsonElement> { ["state"] = J("\"ON\""), ["brightness"] = J("40") },
                },
            });

            var loaded = new JsonSceneStore(path).Load();
            Assert.Equal("ON", loaded["movie"][(5, 1)]["state"].GetString());
            Assert.Equal(40, loaded["movie"][(5, 1)]["brightness"].GetInt32());
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_corrupt_file_returns_empty()
    {
        var path = TempFile();
        try { File.WriteAllText(path, "nonsense"); Assert.Empty(new JsonSceneStore(path).Load()); }
        finally { File.Delete(path); }
    }
}
