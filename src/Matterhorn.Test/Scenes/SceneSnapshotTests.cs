using System.Text.Json;
using Matterhorn.Scenes;

namespace Matterhorn.Test.Scenes;

public class SceneSnapshotTests
{
    // A realistic Extended Color Light state as reported by the endpoint actor: note it carries
    // BOTH a color_temp and hue/saturation, plus a color_mode saying which one is actually active,
    // and a read-only temperature that must never land in a scene.
    private static Dictionary<string, object?> Light(string state, string colorMode) => new()
    {
        ["state"] = state,
        ["brightness"] = 112,
        ["color_mode"] = colorMode,
        ["color_temp"] = 340,
        ["hue"] = 145,
        ["saturation"] = 143,
        ["temperature"] = 21.5,   // read-only — must be dropped
    };

    [Fact]
    public void Off_device_stores_only_its_off_state()
    {
        var snap = SceneSnapshot.Project(Light("OFF", "hs"));

        Assert.Equal("OFF", snap["state"].GetString());
        // Storing brightness/colour would re-light the device on recall (MoveToLevelWithOnOff).
        Assert.False(snap.ContainsKey("brightness"));
        Assert.False(snap.ContainsKey("hue"));
        Assert.False(snap.ContainsKey("saturation"));
        Assert.False(snap.ContainsKey("color_temp"));
    }

    [Fact]
    public void On_device_in_hs_mode_stores_hue_saturation_not_color_temp()
    {
        var snap = SceneSnapshot.Project(Light("ON", "hs"));

        Assert.Equal("ON", snap["state"].GetString());
        Assert.Equal(112, snap["brightness"].GetInt32());
        Assert.Equal(145, snap["hue"].GetInt32());
        Assert.Equal(143, snap["saturation"].GetInt32());
        Assert.False(snap.ContainsKey("color_temp")); // inactive representation dropped
        Assert.False(snap.ContainsKey("temperature")); // read-only dropped
    }

    [Fact]
    public void On_device_in_ct_mode_stores_color_temp_not_hue_saturation()
    {
        var snap = SceneSnapshot.Project(Light("ON", "ct"));

        Assert.Equal("ON", snap["state"].GetString());
        Assert.Equal(112, snap["brightness"].GetInt32());
        Assert.Equal(340, snap["color_temp"].GetInt32());
        Assert.False(snap.ContainsKey("hue"));
        Assert.False(snap.ContainsKey("saturation"));
    }

    [Fact]
    public void On_device_without_color_mode_falls_back_to_hue_saturation()
    {
        var snap = SceneSnapshot.Project(new Dictionary<string, object?>
        {
            ["state"] = "ON", ["brightness"] = 50, ["hue"] = 10, ["saturation"] = 20,
        });

        Assert.Equal(10, snap["hue"].GetInt32());
        Assert.Equal(20, snap["saturation"].GetInt32());
    }

    [Fact]
    public void On_plug_with_only_state_stores_just_state()
    {
        var snap = SceneSnapshot.Project(new Dictionary<string, object?> { ["state"] = "ON" });

        Assert.Equal("ON", snap["state"].GetString());
        Assert.Single(snap);
    }
}
