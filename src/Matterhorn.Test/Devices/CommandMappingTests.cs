using System.Text.Json;
using Matterhorn.Devices;
using Matterhorn.Matter;

namespace Matterhorn.Test.Devices;

public class CommandMappingTests
{
    private static IReadOnlyDictionary<string, JsonElement> Payload(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private static CommandSpec Cmd(IDeviceWrite w) => Assert.IsType<CommandSpec>(w);
    private static AttributeWriteSpec Attr(IDeviceWrite w) => Assert.IsType<AttributeWriteSpec>(w);

    [Fact]
    public void State_ON_maps_to_OnOff_On()
    {
        var c = Cmd(Assert.Single(CommandMapping.Map(Payload("""{"state":"ON"}"""))));
        Assert.Equal(MatterClusters.OnOff, c.ClusterId);
        Assert.Equal("On", c.CommandName);
    }

    [Fact]
    public void State_OFF_maps_to_OnOff_Off()
    {
        var c = Cmd(Assert.Single(CommandMapping.Map(Payload("""{"state":"OFF"}"""))));
        Assert.Equal("Off", c.CommandName);
    }

    [Fact]
    public void Brightness_maps_to_MoveToLevelWithOnOff_with_level()
    {
        var c = Cmd(Assert.Single(CommandMapping.Map(Payload("""{"brightness":200}"""))));
        Assert.Equal(MatterClusters.LevelControl, c.ClusterId);
        Assert.Equal("MoveToLevelWithOnOff", c.CommandName);
        Assert.Equal(200, Assert.IsType<int>(c.Payload["level"]));
    }

    [Fact]
    public void Color_temp_maps_to_MoveToColorTemperature_mireds()
    {
        var c = Cmd(Assert.Single(CommandMapping.Map(Payload("""{"color_temp":370}"""))));
        Assert.Equal(MatterClusters.ColorControl, c.ClusterId);
        Assert.Equal("MoveToColorTemperature", c.CommandName);
        Assert.Equal(370, Assert.IsType<int>(c.Payload["colorTemperatureMireds"]));
    }

    [Fact]
    public void Combined_payload_maps_to_multiple_commands_in_order()
    {
        var cmds = CommandMapping.Map(Payload("""{"state":"ON","brightness":128}"""));
        Assert.Equal(2, cmds.Count);
        Assert.Equal("On", Cmd(cmds[0]).CommandName);
        Assert.Equal("MoveToLevelWithOnOff", Cmd(cmds[1]).CommandName);
    }

    [Fact]
    public void Unknown_key_is_ignored()
    {
        Assert.Empty(CommandMapping.Map(Payload("""{"nonsense":1}""")));
    }

    [Fact]
    public void Hue_and_saturation_together_produce_one_MoveToHueAndSaturation()
    {
        var c = Cmd(Assert.Single(CommandMapping.Map(Payload("""{"hue":100,"saturation":200}"""))));
        Assert.Equal(MatterClusters.ColorControl, c.ClusterId);
        Assert.Equal("MoveToHueAndSaturation", c.CommandName);
        Assert.Equal(100, Assert.IsType<int>(c.Payload["hue"]));
        Assert.Equal(200, Assert.IsType<int>(c.Payload["saturation"]));
    }

    [Fact]
    public void Hue_only_produces_MoveToHue()
    {
        var c = Cmd(Assert.Single(CommandMapping.Map(Payload("""{"hue":42}"""))));
        Assert.Equal("MoveToHue", c.CommandName);
        Assert.Equal(42, Assert.IsType<int>(c.Payload["hue"]));
    }

    [Fact]
    public void Saturation_only_produces_MoveToSaturation()
    {
        var c = Cmd(Assert.Single(CommandMapping.Map(Payload("""{"saturation":77}"""))));
        Assert.Equal("MoveToSaturation", c.CommandName);
        Assert.Equal(77, Assert.IsType<int>(c.Payload["saturation"]));
    }

    [Fact]
    public void State_OPEN_maps_to_WindowCovering_UpOrOpen()
    {
        var c = Cmd(Assert.Single(CommandMapping.Map(Payload("""{"state":"OPEN"}"""))));
        Assert.Equal(MatterClusters.WindowCovering, c.ClusterId);
        Assert.Equal("UpOrOpen", c.CommandName);
    }

    [Fact]
    public void State_STOP_maps_to_WindowCovering_StopMotion()
    {
        var c = Cmd(Assert.Single(CommandMapping.Map(Payload("""{"state":"STOP"}"""))));
        Assert.Equal("StopMotion", c.CommandName);
    }

    [Fact]
    public void Position_maps_to_GoToLiftPercentage_inverted()
    {
        var c = Cmd(Assert.Single(CommandMapping.Map(Payload("""{"position":70}"""))));
        Assert.Equal(MatterClusters.WindowCovering, c.ClusterId);
        Assert.Equal("GoToLiftPercentage", c.CommandName);
        Assert.Equal(30, Assert.IsType<int>(c.Payload["liftPercentageValue"]));
    }

    [Fact]
    public void State_LOCK_maps_to_DoorLock_LockDoor()
    {
        var c = Cmd(Assert.Single(CommandMapping.Map(Payload("""{"state":"LOCK"}"""))));
        Assert.Equal(MatterClusters.DoorLock, c.ClusterId);
        Assert.Equal("LockDoor", c.CommandName);
    }

    [Fact]
    public void State_UNLOCK_maps_to_DoorLock_UnlockDoor()
    {
        var c = Cmd(Assert.Single(CommandMapping.Map(Payload("""{"state":"UNLOCK"}"""))));
        Assert.Equal("UnlockDoor", c.CommandName);
    }

    [Fact]
    public void Heating_setpoint_maps_to_attribute_write_hundredths()
    {
        var a = Attr(Assert.Single(CommandMapping.Map(Payload("""{"occupied_heating_setpoint":21.5}"""))));
        Assert.Equal(MatterClusters.Thermostat, a.ClusterId);
        Assert.Equal(0x12u, a.AttributeId);
        Assert.Equal(2150, Assert.IsType<int>(a.Value));
    }

    [Fact]
    public void System_mode_heat_maps_to_attribute_write_enum()
    {
        var a = Attr(Assert.Single(CommandMapping.Map(Payload("""{"system_mode":"heat"}"""))));
        Assert.Equal(0x1Cu, a.AttributeId);
        Assert.Equal(4, Assert.IsType<int>(a.Value));
    }

    [Fact]
    public void Unrecognized_system_mode_emits_no_write()
    {
        Assert.Empty(CommandMapping.Map(Payload("""{"system_mode":"garbage"}""")));
    }

    [Fact]
    public void Fan_mode_medium_maps_to_attribute_write()
    {
        var a = Attr(Assert.Single(CommandMapping.Map(Payload("""{"fan_mode":"medium"}"""))));
        Assert.Equal(MatterClusters.FanControl, a.ClusterId);
        Assert.Equal(0x00u, a.AttributeId);
        Assert.Equal(2, Assert.IsType<int>(a.Value));
    }

    [Fact]
    public void Fan_percent_maps_to_percent_setting_write()
    {
        var a = Attr(Assert.Single(CommandMapping.Map(Payload("""{"percent":60}"""))));
        Assert.Equal(0x02u, a.AttributeId);
        Assert.Equal(60, Assert.IsType<int>(a.Value));
    }
}
