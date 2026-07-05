using System.Text.Json;
using Matterhorn.Devices;
using Matterhorn.Matter;

namespace Matterhorn.Test.Devices;

public class CommandMappingTests
{
    private static IReadOnlyDictionary<string, JsonElement> Payload(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void State_ON_maps_to_OnOff_On()
    {
        var cmds = CommandMapping.Map(Payload("""{"state":"ON"}"""));
        var c = Assert.Single(cmds);
        Assert.Equal(MatterClusters.OnOff, c.ClusterId);
        Assert.Equal("On", c.CommandName);
    }

    [Fact]
    public void State_OFF_maps_to_OnOff_Off()
    {
        var c = Assert.Single(CommandMapping.Map(Payload("""{"state":"OFF"}""")));
        Assert.Equal("Off", c.CommandName);
    }

    [Fact]
    public void Brightness_maps_to_MoveToLevelWithOnOff_with_level()
    {
        var c = Assert.Single(CommandMapping.Map(Payload("""{"brightness":200}""")));
        Assert.Equal(MatterClusters.LevelControl, c.ClusterId);
        Assert.Equal("MoveToLevelWithOnOff", c.CommandName);
        Assert.Equal(200, Assert.IsType<int>(c.Payload["level"]));
    }

    [Fact]
    public void Color_temp_maps_to_MoveToColorTemperature_mireds()
    {
        var c = Assert.Single(CommandMapping.Map(Payload("""{"color_temp":370}""")));
        Assert.Equal(MatterClusters.ColorControl, c.ClusterId);
        Assert.Equal("MoveToColorTemperature", c.CommandName);
        Assert.Equal(370, Assert.IsType<int>(c.Payload["colorTemperatureMireds"]));
    }

    [Fact]
    public void Combined_payload_maps_to_multiple_commands_in_order()
    {
        var cmds = CommandMapping.Map(Payload("""{"state":"ON","brightness":128}"""));
        Assert.Equal(2, cmds.Count);
        Assert.Equal("On", cmds[0].CommandName);
        Assert.Equal("MoveToLevelWithOnOff", cmds[1].CommandName);
    }

    [Fact]
    public void Unknown_key_is_ignored()
    {
        Assert.Empty(CommandMapping.Map(Payload("""{"nonsense":1}""")));
    }

    [Fact]
    public void Hue_and_saturation_together_produce_one_MoveToHueAndSaturation()
    {
        var c = Assert.Single(CommandMapping.Map(Payload("""{"hue":100,"saturation":200}""")));
        Assert.Equal(MatterClusters.ColorControl, c.ClusterId);
        Assert.Equal("MoveToHueAndSaturation", c.CommandName);
        Assert.Equal(100, Assert.IsType<int>(c.Payload["hue"]));
        Assert.Equal(200, Assert.IsType<int>(c.Payload["saturation"]));
    }

    [Fact]
    public void Hue_only_produces_MoveToHue()
    {
        var c = Assert.Single(CommandMapping.Map(Payload("""{"hue":42}""")));
        Assert.Equal("MoveToHue", c.CommandName);
        Assert.Equal(42, Assert.IsType<int>(c.Payload["hue"]));
    }

    [Fact]
    public void Saturation_only_produces_MoveToSaturation()
    {
        var c = Assert.Single(CommandMapping.Map(Payload("""{"saturation":77}""")));
        Assert.Equal("MoveToSaturation", c.CommandName);
        Assert.Equal(77, Assert.IsType<int>(c.Payload["saturation"]));
    }

    [Fact]
    public void Nested_color_object_maps_to_MoveToHueAndSaturation_with_matter_ranges()
    {
        // HA's json-schema light sends {"color":{"h":0-360,"s":0-100}}.
        var c = Assert.Single(CommandMapping.Map(Payload("""{"color":{"h":180,"s":100}}""")));
        Assert.Equal(MatterClusters.ColorControl, c.ClusterId);
        Assert.Equal("MoveToHueAndSaturation", c.CommandName);
        Assert.Equal(127, Assert.IsType<int>(c.Payload["hue"]));        // 180/360 * 254
        Assert.Equal(254, Assert.IsType<int>(c.Payload["saturation"])); // 100/100 * 254
    }

    [Fact]
    public void Nested_color_values_are_clamped_to_the_matter_range()
    {
        var c = Assert.Single(CommandMapping.Map(Payload("""{"color":{"h":400,"s":150}}""")));
        Assert.Equal(254, Assert.IsType<int>(c.Payload["hue"]));
        Assert.Equal(254, Assert.IsType<int>(c.Payload["saturation"]));
    }

    [Fact]
    public void Nested_color_with_h_only_maps_to_MoveToHue()
    {
        var c = Assert.Single(CommandMapping.Map(Payload("""{"color":{"h":90}}""")));
        Assert.Equal("MoveToHue", c.CommandName);
        Assert.Equal(64, Assert.IsType<int>(c.Payload["hue"])); // 90/360 * 254 = 63.5 -> 64
    }

    [Fact]
    public void Flat_hue_and_saturation_win_over_nested_color()
    {
        var c = Assert.Single(CommandMapping.Map(
            Payload("""{"hue":10,"saturation":20,"color":{"h":180,"s":50}}""")));
        Assert.Equal("MoveToHueAndSaturation", c.CommandName);
        Assert.Equal(10, Assert.IsType<int>(c.Payload["hue"])); // flat keys are already Matter-range
    }
}
