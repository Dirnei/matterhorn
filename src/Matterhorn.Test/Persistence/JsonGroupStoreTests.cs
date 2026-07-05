using Matterhorn.Persistence;

namespace Matterhorn.Test.Persistence;

public class JsonGroupStoreTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"groups-{Guid.NewGuid():N}.json");

    [Fact]
    public void Load_missing_file_returns_empty() => Assert.Empty(new JsonGroupStore(TempFile()).Load());

    [Fact]
    public void Save_then_load_round_trips_members()
    {
        var path = TempFile();
        try
        {
            new JsonGroupStore(path).Save(new Dictionary<string, IReadOnlyList<(ulong, ushort)>>
            {
                ["living_room"] = new (ulong, ushort)[] { (5, 1), (9, 1) },
            });

            var loaded = new JsonGroupStore(path).Load();
            Assert.Equal(new (ulong, ushort)[] { (5, 1), (9, 1) }, loaded["living_room"]);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_corrupt_file_returns_empty()
    {
        var path = TempFile();
        try { File.WriteAllText(path, "{ not json"); Assert.Empty(new JsonGroupStore(path).Load()); }
        finally { File.Delete(path); }
    }
}
