using System.Text.Json;
using Matterhorn.Bridge;
using Matterhorn.HomeAssistant;
using Matterhorn.Mqtt;

namespace Matterhorn.Test.HomeAssistant;

public class HaDiscoveryBuilderTests
{
    private static readonly MqttTopics Topics = new("matterhorn");

    internal static DeviceDescriptor Descriptor(string name, params ExposeEntry[] exposes) =>
        new(name, "1", 1, "Nanoleaf", "Bulb", 4442, 3, "Light", true, exposes);

    internal static readonly ExposeEntry State = new("binary", "state", 7, ValueOn: "ON", ValueOff: "OFF");
    internal static readonly ExposeEntry Brightness = new("numeric", "brightness", 7, ValueMin: 0, ValueMax: 254);
    internal static readonly ExposeEntry Hue = new("numeric", "hue", 7, ValueMin: 0, ValueMax: 254);
    internal static readonly ExposeEntry Saturation = new("numeric", "saturation", 7, ValueMin: 0, ValueMax: 254);
    internal static readonly ExposeEntry ColorTemp = new("numeric", "color_temp", 7, ValueMin: 147, ValueMax: 500, Unit: "mired");

    [Fact]
    public void Color_light_produces_one_json_schema_light_config()
    {
        var msgs = HaDiscoveryBuilder.Build(
            Descriptor("bulb_1_1", State, Brightness, Hue, Saturation, ColorTemp), Topics, "homeassistant");

        var msg = Assert.Single(msgs);
        Assert.Equal("homeassistant/light/matterhorn_1_1/light/config", msg.Topic);
        var p = JsonDocument.Parse(msg.Payload).RootElement;
        Assert.Equal("matterhorn_1_1_light", p.GetProperty("unique_id").GetString());
        Assert.Equal(JsonValueKind.Null, p.GetProperty("name").ValueKind); // entity takes the device name
        Assert.Equal("json", p.GetProperty("schema").GetString());
        Assert.Equal("matterhorn/bulb_1_1", p.GetProperty("state_topic").GetString());
        Assert.Equal("matterhorn/bulb_1_1/set", p.GetProperty("command_topic").GetString());
        Assert.True(p.GetProperty("brightness").GetBoolean());
        Assert.Equal(254, p.GetProperty("brightness_scale").GetInt32());
        Assert.Equal(new[] { "color_temp", "hs" },
            p.GetProperty("supported_color_modes").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(147, p.GetProperty("min_mireds").GetInt32());
        Assert.Equal(500, p.GetProperty("max_mireds").GetInt32());
    }

    [Fact]
    public void Light_config_carries_device_availability_and_origin_blocks()
    {
        var msg = Assert.Single(HaDiscoveryBuilder.Build(Descriptor("bulb_1_1", State, Brightness), Topics, "homeassistant"));

        var p = JsonDocument.Parse(msg.Payload).RootElement;
        var device = p.GetProperty("device");
        Assert.Equal("matterhorn_1_1", device.GetProperty("identifiers")[0].GetString());
        Assert.Equal("bulb_1_1", device.GetProperty("name").GetString());
        Assert.Equal("Nanoleaf", device.GetProperty("manufacturer").GetString());
        Assert.Equal("Bulb", device.GetProperty("model").GetString());
        Assert.Equal("matterhorn_bridge", device.GetProperty("via_device").GetString());

        Assert.Equal("all", p.GetProperty("availability_mode").GetString());
        var availability = p.GetProperty("availability").EnumerateArray().ToArray();
        Assert.Equal("matterhorn/bridge/state", availability[0].GetProperty("topic").GetString());
        Assert.Equal("{{ value_json.state }}", availability[0].GetProperty("value_template").GetString());
        Assert.Equal("matterhorn/bulb_1_1/availability", availability[1].GetProperty("topic").GetString());

        Assert.Equal("Matterhorn", p.GetProperty("origin").GetProperty("name").GetString());
    }

    [Fact]
    public void Dimmable_light_without_color_omits_color_modes_and_mireds()
    {
        var msg = Assert.Single(HaDiscoveryBuilder.Build(Descriptor("bulb_1_1", State, Brightness), Topics, "homeassistant"));

        var p = JsonDocument.Parse(msg.Payload).RootElement;
        Assert.True(p.GetProperty("brightness").GetBoolean());
        Assert.False(p.TryGetProperty("supported_color_modes", out _));
        Assert.False(p.TryGetProperty("min_mireds", out _));
    }

    [Fact]
    public void OnOff_only_device_produces_a_switch_config()
    {
        var msg = Assert.Single(HaDiscoveryBuilder.Build(Descriptor("plug_1_1", State), Topics, "homeassistant"));

        Assert.Equal("homeassistant/switch/matterhorn_1_1/state/config", msg.Topic);
        var p = JsonDocument.Parse(msg.Payload).RootElement;
        Assert.Equal("matterhorn_1_1_switch", p.GetProperty("unique_id").GetString());
        Assert.Equal("matterhorn/plug_1_1", p.GetProperty("state_topic").GetString());
        Assert.Equal("{{ value_json.state }}", p.GetProperty("value_template").GetString());
        // Bare ON/OFF payloads only route through the single-attr set topic.
        Assert.Equal("matterhorn/plug_1_1/set/state", p.GetProperty("command_topic").GetString());
        Assert.Equal("ON", p.GetProperty("payload_on").GetString());
        Assert.Equal("OFF", p.GetProperty("payload_off").GetString());
    }

    [Fact]
    public void Device_without_mappable_exposes_produces_nothing()
    {
        Assert.Empty(HaDiscoveryBuilder.Build(Descriptor("mystery_1_1"), Topics, "homeassistant"));
    }
}
