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

    [Fact]
    public void Temperature_expose_produces_a_sensor_config()
    {
        var temp = new ExposeEntry("numeric", "temperature", 1, Unit: "°C");
        var msg = Assert.Single(HaDiscoveryBuilder.Build(Descriptor("climate_1_1", temp), Topics, "homeassistant"));

        Assert.Equal("homeassistant/sensor/matterhorn_1_1/temperature/config", msg.Topic);
        var p = JsonDocument.Parse(msg.Payload).RootElement;
        Assert.Equal("matterhorn_1_1_temperature", p.GetProperty("unique_id").GetString());
        Assert.Equal("temperature", p.GetProperty("name").GetString());
        Assert.Equal("temperature", p.GetProperty("device_class").GetString());
        Assert.Equal("°C", p.GetProperty("unit_of_measurement").GetString());
        Assert.Equal("measurement", p.GetProperty("state_class").GetString());
        Assert.Equal("{{ value_json.temperature }}", p.GetProperty("value_template").GetString());
        Assert.Equal("matterhorn/climate_1_1", p.GetProperty("state_topic").GetString());
    }

    [Fact]
    public void Contact_expose_produces_an_inverted_door_binary_sensor()
    {
        var contact = new ExposeEntry("binary", "contact", 1);
        var msg = Assert.Single(HaDiscoveryBuilder.Build(Descriptor("window_1_1", contact), Topics, "homeassistant"));

        Assert.Equal("homeassistant/binary_sensor/matterhorn_1_1/contact/config", msg.Topic);
        var p = JsonDocument.Parse(msg.Payload).RootElement;
        Assert.Equal("door", p.GetProperty("device_class").GetString());
        // Matter/Z2M semantics: contact=true means closed; HA's door class: ON means open.
        Assert.Equal("{{ 'ON' if not value_json.contact else 'OFF' }}", p.GetProperty("value_template").GetString());
    }

    [Fact]
    public void Occupancy_expose_produces_an_occupancy_binary_sensor()
    {
        var occupancy = new ExposeEntry("binary", "occupancy", 1);
        var msg = Assert.Single(HaDiscoveryBuilder.Build(Descriptor("motion_1_1", occupancy), Topics, "homeassistant"));

        Assert.Equal("homeassistant/binary_sensor/matterhorn_1_1/occupancy/config", msg.Topic);
        var p = JsonDocument.Parse(msg.Payload).RootElement;
        Assert.Equal("occupancy", p.GetProperty("device_class").GetString());
        Assert.Equal("{{ 'ON' if value_json.occupancy else 'OFF' }}", p.GetProperty("value_template").GetString());
    }

    [Fact]
    public void Multi_sensor_device_produces_one_config_per_property()
    {
        var exposes = new ExposeEntry[]
        {
            new("numeric", "temperature", 1, Unit: "°C"),
            new("numeric", "humidity", 1, Unit: "%"),
            new("numeric", "battery", 1, ValueMin: 0, ValueMax: 100, Unit: "%"),
        };
        var msgs = HaDiscoveryBuilder.Build(Descriptor("climate_1_1", exposes), Topics, "homeassistant");

        Assert.Equal(3, msgs.Count);
        Assert.Contains(msgs, m => m.Topic == "homeassistant/sensor/matterhorn_1_1/humidity/config");
        Assert.Contains(msgs, m => m.Topic == "homeassistant/sensor/matterhorn_1_1/battery/config");
    }

    [Fact]
    public void Config_topics_are_stable_across_renames()
    {
        var before = HaDiscoveryBuilder.Build(Descriptor("bulb_1_1", State, Brightness), Topics, "homeassistant");
        var after = HaDiscoveryBuilder.Build(Descriptor("kitchen_lamp", State, Brightness), Topics, "homeassistant");

        Assert.Equal(before.Select(m => m.Topic), after.Select(m => m.Topic)); // same retained topics
        Assert.Contains("matterhorn/kitchen_lamp", after[0].Payload); // payloads follow the new name
    }
}
