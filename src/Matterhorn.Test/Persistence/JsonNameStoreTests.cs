using Matterhorn.Persistence;

namespace Matterhorn.Test.Persistence;

public class JsonNameStoreTests
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"names-{Guid.NewGuid():N}.json");

    [Fact]
    public void Load_missing_file_returns_empty()
    {
        var store = new JsonNameStore(TempFile());
        Assert.Empty(store.Load());
    }

    [Fact]
    public void Save_then_load_round_trips_entries()
    {
        var path = TempFile();
        try
        {
            var store = new JsonNameStore(path);
            store.Save(new Dictionary<(ulong, ushort), string>
            {
                [(5UL, (ushort)1)] = "living_room_lamp",
                [(9UL, (ushort)1)] = "desk_bulb",
            });

            var loaded = new JsonNameStore(path).Load();
            Assert.Equal("living_room_lamp", loaded[(5UL, 1)]);
            Assert.Equal("desk_bulb", loaded[(9UL, 1)]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Save_overwrites_a_previous_store_and_leaves_no_temp_file()
    {
        var path = TempFile();
        try
        {
            var store = new JsonNameStore(path);
            store.Save(new Dictionary<(ulong, ushort), string> { [(5UL, (ushort)1)] = "old_name" });
            store.Save(new Dictionary<(ulong, ushort), string> { [(5UL, (ushort)1)] = "new_name" });

            var loaded = new JsonNameStore(path).Load();
            Assert.Equal("new_name", loaded[(5UL, 1)]);
            Assert.Single(loaded);
            Assert.False(File.Exists(path + ".tmp")); // atomic swap leaves no temp file behind
        }
        finally { File.Delete(path); }
    }
}
