using System.Text.Json;
using Matter2Mqtt.Devices;
using Matter2Mqtt.Matter;

namespace Matter2Mqtt.Test.Devices;

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
}
