# Home Assistant MQTT Discovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Matterhorn announces its devices to Home Assistant via MQTT Discovery (retained config payloads under a discovery prefix), like Zigbee2Mqtt does. Opt-in via config.

**Architecture:** A pure `HaDiscoveryBuilder` (analogous to `ExposesBuilder`/`MqttTopics`) maps a `DeviceDescriptor` to a list of discovery `(topic, payload)` messages. `MatterGatewayActor` publishes them retained on join/rename/reconnect and clears them on remove. `MqttBridgeService` additionally subscribes to HA's birth topic (`{prefix}/status`) so discovery + states are re-announced when HA restarts. `CommandMapping` learns HA's nested color format; the read path additionally publishes an HA-shaped nested `color` object.

**Tech Stack:** .NET / ASP.NET Core, Akka.NET (ReceiveActor, Akka.TestKit.Xunit2), HiveMQtt, System.Text.Json, xUnit.

**Spec:** `docs/superpowers/specs/2026-07-05-ha-discovery-design.md`

## Global Constraints

- Opt-in: `HomeAssistant:Enabled` defaults to `false`; when off, behavior is byte-identical to today.
- Discovery prefix: `HomeAssistant:DiscoveryTopic`, default `homeassistant`.
- Config topic scheme: `{prefix}/{component}/matterhorn_{nodeId}_{endpoint}/{property}/config`; `unique_id` = `matterhorn_{nodeId}_{endpoint}_{property}`. Keyed on node/endpoint so renames overwrite the same topics.
- All discovery payloads are serialized with `Matterhorn.Configuration.JsonDefaults.SnakeCase`.
- Build: `dotnet build` (repo root). Tests: `dotnet test` (repo root); filter single class with `dotnet test --filter "FullyQualifiedName~<ClassName>"`.
- Commit after every task (conventional commits, e.g. `feat: ...`, `docs: ...`).

---

### Task 1: HaDiscoveryBuilder — light & switch

**Files:**
- Create: `src/Matterhorn/HomeAssistant/HaDiscoveryBuilder.cs`
- Test: `src/Matterhorn.Test/HomeAssistant/HaDiscoveryBuilderTests.cs`

**Interfaces:**
- Consumes: `DeviceDescriptor`, `ExposeEntry` (`Matterhorn.Bridge`), `MqttTopics` (`Matterhorn.Mqtt`), `JsonDefaults.SnakeCase` (`Matterhorn.Configuration`).
- Produces: `static IReadOnlyList<HaDiscoveryBuilder.DiscoveryMessage> HaDiscoveryBuilder.Build(DeviceDescriptor d, MqttTopics topics, string prefix)` with `sealed record DiscoveryMessage(string Topic, string Payload)` — Tasks 2 and 5 rely on exactly this signature.

- [ ] **Step 1: Write the failing tests**

Create `src/Matterhorn.Test/HomeAssistant/HaDiscoveryBuilderTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~HaDiscoveryBuilderTests"`
Expected: build FAILS with "The type or namespace name 'HomeAssistant' does not exist" (namespace `Matterhorn.HomeAssistant` missing).

- [ ] **Step 3: Implement the builder**

Create `src/Matterhorn/HomeAssistant/HaDiscoveryBuilder.cs`:

```csharp
using System.Text.Json;
using Matterhorn.Bridge;
using Matterhorn.Configuration;
using Matterhorn.Mqtt;

namespace Matterhorn.HomeAssistant;

/// <summary>
/// Builds Home Assistant MQTT Discovery configs from a device descriptor. Pure — the gateway
/// owns publishing. Topics and unique_ids key on node/endpoint (not the friendly name), so a
/// rename overwrites the same retained configs instead of orphaning them, and HA entity
/// history survives renames.
/// </summary>
public static class HaDiscoveryBuilder
{
    public sealed record DiscoveryMessage(string Topic, string Payload);

    public static IReadOnlyList<DiscoveryMessage> Build(DeviceDescriptor d, MqttTopics topics, string prefix)
    {
        var list = new List<DiscoveryMessage>();
        var props = d.Exposes.ToDictionary(e => e.Property);
        var id = $"matterhorn_{d.NodeId}_{d.Endpoint}";

        if (props.ContainsKey("state"))
            list.Add(props.ContainsKey("brightness") || props.ContainsKey("color_temp") || props.ContainsKey("hue")
                ? Light(d, topics, prefix, id, props)
                : Switch(d, topics, prefix, id));
        return list;
    }

    private static DiscoveryMessage Light(DeviceDescriptor d, MqttTopics topics, string prefix, string id,
        IReadOnlyDictionary<string, ExposeEntry> props)
    {
        var p = Common(d, topics, id, $"{id}_light");
        p["name"] = null; // the light IS the device -> HA uses the device name
        p["schema"] = "json";
        p["state_topic"] = topics.Device(d.FriendlyName);
        p["command_topic"] = $"{topics.Device(d.FriendlyName)}/set";
        if (props.ContainsKey("brightness")) { p["brightness"] = true; p["brightness_scale"] = 254; }
        var modes = new List<string>();
        if (props.ContainsKey("color_temp")) modes.Add("color_temp");
        if (props.ContainsKey("hue")) modes.Add("hs");
        if (modes.Count > 0) p["supported_color_modes"] = modes;
        if (props.TryGetValue("color_temp", out var ct) && ct.ValueMin is int min && ct.ValueMax is int max)
        {
            p["min_mireds"] = min;
            p["max_mireds"] = max;
        }
        return new($"{prefix}/light/{id}/light/config", JsonSerializer.Serialize(p, JsonDefaults.SnakeCase));
    }

    private static DiscoveryMessage Switch(DeviceDescriptor d, MqttTopics topics, string prefix, string id)
    {
        var p = Common(d, topics, id, $"{id}_switch");
        p["name"] = null;
        p["state_topic"] = topics.Device(d.FriendlyName);
        p["value_template"] = "{{ value_json.state }}";
        // HA's switch publishes payload_on/payload_off verbatim; only the single-attr set topic
        // accepts bare (non-JSON) values.
        p["command_topic"] = $"{topics.Device(d.FriendlyName)}/set/state";
        p["payload_on"] = "ON";
        p["payload_off"] = "OFF";
        return new($"{prefix}/switch/{id}/state/config", JsonSerializer.Serialize(p, JsonDefaults.SnakeCase));
    }

    private static Dictionary<string, object?> Common(DeviceDescriptor d, MqttTopics topics, string id, string uniqueId)
    {
        var device = new Dictionary<string, object?>
        {
            ["identifiers"] = new[] { id },
            ["name"] = d.FriendlyName,
            ["via_device"] = "matterhorn_bridge",
        };
        if (d.VendorName is not null) device["manufacturer"] = d.VendorName;
        if (d.ProductName is not null) device["model"] = d.ProductName;

        return new Dictionary<string, object?>
        {
            ["unique_id"] = uniqueId,
            ["availability"] = new object[]
            {
                new Dictionary<string, object?> { ["topic"] = topics.BridgeState(), ["value_template"] = "{{ value_json.state }}" },
                new Dictionary<string, object?> { ["topic"] = topics.Availability(d.FriendlyName) },
            },
            ["availability_mode"] = "all",
            ["device"] = device,
            ["origin"] = new Dictionary<string, object?>
            {
                ["name"] = "Matterhorn",
                ["sw"] = typeof(HaDiscoveryBuilder).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            },
        };
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~HaDiscoveryBuilderTests"`
Expected: 5 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Matterhorn/HomeAssistant/HaDiscoveryBuilder.cs src/Matterhorn.Test/HomeAssistant/HaDiscoveryBuilderTests.cs
git commit -m "feat: HA discovery builder for light and switch entities"
```

---

### Task 2: HaDiscoveryBuilder — binary_sensor & sensor

**Files:**
- Modify: `src/Matterhorn/HomeAssistant/HaDiscoveryBuilder.cs`
- Test: `src/Matterhorn.Test/HomeAssistant/HaDiscoveryBuilderTests.cs`

**Interfaces:**
- Consumes: `HaDiscoveryBuilder.Build` / `Common` from Task 1 (same file).
- Produces: `Build` additionally emits `binary_sensor` configs for `contact`/`occupancy` and `sensor` configs for `temperature`/`humidity`/`illuminance`/`battery`. Signature unchanged.

- [ ] **Step 1: Write the failing tests**

Append to `HaDiscoveryBuilderTests`:

```csharp
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~HaDiscoveryBuilderTests"`
Expected: the 5 new tests FAIL (empty result / `Assert.Single` fails); Task 1 tests still pass.

- [ ] **Step 3: Extend the builder**

In `HaDiscoveryBuilder.Build`, after the `state` block and before `return list;`, add:

```csharp
        // Matter/Z2M semantics: contact=true means closed; HA's door class: ON means open.
        if (props.ContainsKey("contact"))
            list.Add(BinarySensor(d, topics, prefix, id, "contact", "door",
                "{{ 'ON' if not value_json.contact else 'OFF' }}"));
        if (props.ContainsKey("occupancy"))
            list.Add(BinarySensor(d, topics, prefix, id, "occupancy", "occupancy",
                "{{ 'ON' if value_json.occupancy else 'OFF' }}"));
        foreach (var (property, deviceClass, unit) in SensorRules)
            if (props.ContainsKey(property))
                list.Add(Sensor(d, topics, prefix, id, property, deviceClass, unit));
```

Add to the class:

```csharp
    private static readonly (string Property, string DeviceClass, string Unit)[] SensorRules =
    [
        ("temperature", "temperature", "°C"),
        ("humidity", "humidity", "%"),
        ("illuminance", "illuminance", "lx"),
        ("battery", "battery", "%"),
    ];

    private static DiscoveryMessage BinarySensor(DeviceDescriptor d, MqttTopics topics, string prefix, string id,
        string property, string deviceClass, string valueTemplate)
    {
        var p = Common(d, topics, id, $"{id}_{property}");
        p["name"] = property;
        p["state_topic"] = topics.Device(d.FriendlyName);
        p["value_template"] = valueTemplate;
        p["device_class"] = deviceClass;
        return new($"{prefix}/binary_sensor/{id}/{property}/config", JsonSerializer.Serialize(p, JsonDefaults.SnakeCase));
    }

    private static DiscoveryMessage Sensor(DeviceDescriptor d, MqttTopics topics, string prefix, string id,
        string property, string deviceClass, string unit)
    {
        var p = Common(d, topics, id, $"{id}_{property}");
        p["name"] = property;
        p["state_topic"] = topics.Device(d.FriendlyName);
        p["value_template"] = $"{{{{ value_json.{property} }}}}";
        p["device_class"] = deviceClass;
        p["unit_of_measurement"] = unit;
        p["state_class"] = "measurement";
        return new($"{prefix}/sensor/{id}/{property}/config", JsonSerializer.Serialize(p, JsonDefaults.SnakeCase));
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~HaDiscoveryBuilderTests"`
Expected: 10 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Matterhorn/HomeAssistant/HaDiscoveryBuilder.cs src/Matterhorn.Test/HomeAssistant/HaDiscoveryBuilderTests.cs
git commit -m "feat: HA discovery configs for binary_sensor and sensor exposes"
```

---

### Task 3: CommandMapping accepts HA's nested color object

**Files:**
- Modify: `src/Matterhorn/Devices/CommandMapping.cs`
- Test: `src/Matterhorn.Test/Devices/CommandMappingTests.cs`

**Interfaces:**
- Consumes: existing `CommandMapping.Map(IReadOnlyDictionary<string, JsonElement>)`.
- Produces: `Map` additionally understands `{"color":{"h":0-360,"s":0-100}}` (also long-form `hue`/`saturation` keys inside `color`), normalized to Matter's 0–254 ranges. Flat `hue`/`saturation` keys keep priority.

- [ ] **Step 1: Write the failing tests**

Append to `CommandMappingTests`:

```csharp
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~CommandMappingTests"`
Expected: the 3 nested-color tests FAIL (`Assert.Single` fails, empty command list); `Flat_hue_and_saturation_win_over_nested_color` already passes.

- [ ] **Step 3: Extend CommandMapping**

In `CommandMapping.Map`, after the existing flat hue/saturation block (after the `else if (hasSat)` branch), add:

```csharp
        // HA's json-schema light sends color nested: {"color":{"h":0-360,"s":0-100}} (long-form
        // hue/saturation keys allowed). Normalize to Matter's 0-254 ranges. Flat keys win.
        if (!hasHue && !hasSat && setPayload.TryGetValue("color", out var color)
            && color.ValueKind == JsonValueKind.Object)
        {
            double? h = color.TryGetProperty("h", out var hEl) ? hEl.GetDouble()
                      : color.TryGetProperty("hue", out var hLong) ? hLong.GetDouble() : null;
            double? s = color.TryGetProperty("s", out var sEl) ? sEl.GetDouble()
                      : color.TryGetProperty("saturation", out var sLong) ? sLong.GetDouble() : null;
            static int Scale(double value, double max) => Math.Clamp((int)Math.Round(value / max * 254), 0, 254);
            if (h is not null && s is not null)
                cmds.Add(new(MatterClusters.ColorControl, "MoveToHueAndSaturation",
                    new Dictionary<string, object?> { ["hue"] = Scale(h.Value, 360), ["saturation"] = Scale(s.Value, 100) }));
            else if (h is not null)
                cmds.Add(new(MatterClusters.ColorControl, "MoveToHue",
                    new Dictionary<string, object?> { ["hue"] = Scale(h.Value, 360), ["direction"] = 0 }));
            else if (s is not null)
                cmds.Add(new(MatterClusters.ColorControl, "MoveToSaturation",
                    new Dictionary<string, object?> { ["saturation"] = Scale(s.Value, 100) }));
        }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~CommandMappingTests"`
Expected: all CommandMapping tests PASS (12 total).

- [ ] **Step 5: Commit**

```bash
git add src/Matterhorn/Devices/CommandMapping.cs src/Matterhorn.Test/Devices/CommandMappingTests.cs
git commit -m "feat: accept HA nested color object in /set payloads"
```

---

### Task 4: Read path — HA-shaped color state

**Files:**
- Modify: `src/Matterhorn/Devices/PropertyMapping.cs:22`
- Modify: `src/Matterhorn/Devices/MatterEndpointActor.cs` (`OnAttribute`)
- Test: `src/Matterhorn.Test/Devices/PropertyMappingTests.cs`, `src/Matterhorn.Test/Devices/MatterEndpointActorTests.cs`

**Interfaces:**
- Consumes: `PropertyMapping.Map`, `MatterEndpointActor` internals (`_state`, `OnAttribute`).
- Produces: state payloads for color devices additionally carry `"color":{"h":0-360,"s":0-100}` (HA json-schema shape) next to the flat Matter-range `hue`/`saturation`; `color_mode` value for color-temperature mode changes from `"ct"` to `"color_temp"` (the value both HA and Z2M use).

- [ ] **Step 1: Write the failing tests**

Append to `PropertyMappingTests` (readings are built the same way as the existing tests in that file — follow the local pattern):

```csharp
    [Fact]
    public void Color_temperature_mode_maps_to_color_temp()
    {
        // HA's json-schema light (and Z2M) call this mode "color_temp", not "ct".
        var props = PropertyMapping.Map(new[]
        {
            new AttributeReading(1, 1, MatterClusters.ColorControl, 8, JsonDocument.Parse("2").RootElement),
        });
        Assert.Equal("color_temp", props["color_mode"]);
    }
```

Append to `MatterEndpointActorTests` (ensure the file's usings include `System.Text.Json`, `Matterhorn.Matter`, `Matterhorn.Mqtt`, `Matterhorn.Test.Mqtt` — add any that are missing):

```csharp
    [Fact]
    public void Hue_and_saturation_also_publish_a_nested_ha_color_object()
    {
        var mqtt = new InMemoryMqttPublisher();
        var actor = Sys.ActorOf(MatterEndpointActor.Props("lamp", 1, 1, new FakeMatterController(), mqtt, new MqttTopics("matterhorn")));

        actor.Tell(new ApplyAttribute(new AttributeReading(1, 1, MatterClusters.ColorControl, 0,
            JsonDocument.Parse("127").RootElement))); // hue 127 -> h 180
        actor.Tell(new ApplyAttribute(new AttributeReading(1, 1, MatterClusters.ColorControl, 1,
            JsonDocument.Parse("254").RootElement))); // saturation 254 -> s 100

        AwaitAssert(() => Assert.Contains(mqtt.Messages, m =>
            m.Topic == "matterhorn/lamp" && m.Payload.Contains("\"color\":{\"h\":180,\"s\":100}")));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~PropertyMappingTests|FullyQualifiedName~MatterEndpointActorTests"`
Expected: both new tests FAIL (`"ct"` instead of `"color_temp"`; no `color` object in the payload).

- [ ] **Step 3: Implement**

In `PropertyMapping.cs` line 22, change the color-mode rule:

```csharp
        new(MatterClusters.ColorControl, 8, "color_mode",
            v => v.GetInt32() switch { 0 => "hs", 1 => "xy", 2 => "color_temp", _ => "unknown" }),
```

In `MatterEndpointActor.OnAttribute`, call a new sync step after the mapping loop:

```csharp
    private void OnAttribute(ApplyAttribute msg)
    {
        foreach (var (k, v) in PropertyMapping.Map(new[] { msg.Reading }))
            _state[k] = v;
        SyncNestedColor();
        var json = JsonSerializer.Serialize(_state);
        _mqtt.PublishRetained(_topics.Device(_name), json);
        Context.System.EventStream.Publish(new DeviceStateChanged(_name, json));
    }

    // HA's json-schema light reads color nested ({"color":{"h":0-360,"s":0-100}}); keep that
    // object in sync with the flat Matter-range hue/saturation properties.
    private void SyncNestedColor()
    {
        var hasHue = _state.TryGetValue("hue", out var hue) && hue is int;
        var hasSat = _state.TryGetValue("saturation", out var sat) && sat is int;
        if (!hasHue && !hasSat) return;
        _state["color"] = new Dictionary<string, object?>
        {
            ["h"] = hasHue ? (int)Math.Round((int)hue! * 360.0 / 254) : 0,
            ["s"] = hasSat ? (int)Math.Round((int)sat! * 100.0 / 254) : 0,
        };
    }
```

- [ ] **Step 4: Run the full test suite**

Run: `dotnet test`
Expected: all tests PASS (the existing `Maps_hue_saturation_and_color_mode` test asserts `"hs"` and is unaffected).

- [ ] **Step 5: Commit**

```bash
git add src/Matterhorn/Devices/PropertyMapping.cs src/Matterhorn/Devices/MatterEndpointActor.cs src/Matterhorn.Test/Devices/PropertyMappingTests.cs src/Matterhorn.Test/Devices/MatterEndpointActorTests.cs
git commit -m "feat: publish HA-shaped nested color state"
```

---

### Task 5: Gateway publishes/clears discovery configs

**Files:**
- Modify: `src/Matterhorn/Bridge/BridgeMessages.cs`
- Modify: `src/Matterhorn/Bridge/MatterGatewayActor.cs`
- Test: `src/Matterhorn.Test/Bridge/MatterGatewayActorTests.cs`

**Interfaces:**
- Consumes: `HaDiscoveryBuilder.Build(DeviceDescriptor, MqttTopics, string)` from Task 1.
- Produces: `MatterGatewayActor.Props(IMatterController controller, IMqttPublisher mqtt, MqttTopics topics, INameStore? names = null, string? haDiscoveryPrefix = null)` — `null` prefix = feature off. New message `public record HaStatusOnline;` in `Matterhorn.Bridge` — Task 6 routes it.

- [ ] **Step 1: Write the failing tests**

Append to `MatterGatewayActorTests`:

```csharp
    [Fact]
    public void NodeAdded_publishes_ha_discovery_configs_when_enabled()
    {
        var fake = new FakeMatterController();
        var mqtt = new InMemoryMqttPublisher();
        var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, mqtt, new MqttTopics("matterhorn"), null, "homeassistant"));

        fake.Emit(new NodeAdded(Light(1)));

        AwaitAssert(() => Assert.Contains(mqtt.Messages, m =>
            m.Topic == "homeassistant/light/matterhorn_1_1/light/config" && m.Retained
            && m.Payload.Contains("\"unique_id\":\"matterhorn_1_1_light\"")));
    }

    [Fact]
    public void NodeAdded_publishes_no_ha_discovery_when_disabled()
    {
        var fake = new FakeMatterController();
        var mqtt = new InMemoryMqttPublisher();
        var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, mqtt, new MqttTopics("matterhorn")));

        fake.Emit(new NodeAdded(Light(1)));

        AwaitAssert(() => Assert.Contains(mqtt.Messages, m => m.Topic == "matterhorn/bridge/devices"));
        Assert.DoesNotContain(mqtt.Messages, m => m.Topic.StartsWith("homeassistant/"));
    }

    [Fact]
    public void NodeRemoved_clears_ha_discovery_configs()
    {
        var fake = new FakeMatterController();
        var mqtt = new InMemoryMqttPublisher();
        var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, mqtt, new MqttTopics("matterhorn"), null, "homeassistant"));
        fake.Emit(new NodeAdded(Light(1)));
        AwaitAssert(() => Assert.Contains(mqtt.Messages, m => m.Topic == "homeassistant/light/matterhorn_1_1/light/config"));

        fake.Emit(new NodeRemoved(1));

        // Retained empty payload deletes the config -> the entity disappears from HA.
        AwaitAssert(() => Assert.Contains(mqtt.Messages, m =>
            m.Topic == "homeassistant/light/matterhorn_1_1/light/config" && m.Payload == "" && m.Retained));
    }

    [Fact]
    public void Rename_republishes_ha_discovery_on_the_same_topic_with_the_new_name()
    {
        var fake = new FakeMatterController();
        var mqtt = new InMemoryMqttPublisher();
        var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, mqtt, new MqttTopics("matterhorn"), new InMemoryNameStore(), "homeassistant"));
        fake.Emit(new NodeAdded(Light(5)));
        AwaitAssert(() => Assert.Single(gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result));

        Assert.True(gw.Ask<RenameResult>(new RenameRequest("bulb_5_1", "lamp", "tx1")).Result.Ok);

        AwaitAssert(() => Assert.Contains(mqtt.Messages, m =>
            m.Topic == "homeassistant/light/matterhorn_5_1/light/config" // topic unchanged
            && m.Payload.Contains("\"state_topic\":\"matterhorn/lamp\"")));
    }

    [Fact]
    public void HaStatusOnline_republishes_discovery_for_all_devices()
    {
        var fake = new FakeMatterController();
        var mqtt = new InMemoryMqttPublisher();
        var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, mqtt, new MqttTopics("matterhorn"), null, "homeassistant"));
        fake.Emit(new NodeAdded(Light(1)));
        AwaitAssert(() => Assert.Contains(mqtt.Messages, m => m.Topic == "homeassistant/light/matterhorn_1_1/light/config"));
        while (mqtt.Messages.TryDequeue(out _)) { } // drain, then expect a fresh announcement

        gw.Tell(new HaStatusOnline());

        AwaitAssert(() => Assert.Contains(mqtt.Messages, m =>
            m.Topic == "homeassistant/light/matterhorn_1_1/light/config" && m.Payload != ""));
    }

    [Fact]
    public void MqttConnected_republishes_ha_discovery()
    {
        var fake = new FakeMatterController();
        var mqtt = new InMemoryMqttPublisher();
        var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, mqtt, new MqttTopics("matterhorn"), null, "homeassistant"));
        fake.Emit(new NodeAdded(Light(1)));
        AwaitAssert(() => Assert.Contains(mqtt.Messages, m => m.Topic == "homeassistant/light/matterhorn_1_1/light/config"));
        while (mqtt.Messages.TryDequeue(out _)) { }

        gw.Tell(new MqttConnected());

        AwaitAssert(() => Assert.Contains(mqtt.Messages, m =>
            m.Topic == "homeassistant/light/matterhorn_1_1/light/config" && m.Payload != ""));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~MatterGatewayActorTests"`
Expected: build FAILS (`Props` has no 5th parameter; `HaStatusOnline` unknown).

- [ ] **Step 3: Implement**

In `src/Matterhorn/Bridge/BridgeMessages.cs`, add after `MqttConnected`:

```csharp
/// <summary>HA's birth message appeared on {discoveryPrefix}/status — re-announce discovery configs and states.</summary>
public record HaStatusOnline;
```

In `MatterGatewayActor.cs`:

1. Add `using Matterhorn.HomeAssistant;` and a field + constructor/Props parameter (`null` = disabled):

```csharp
    private readonly string? _haPrefix;

    public static Props Props(IMatterController controller, IMqttPublisher mqtt, MqttTopics topics,
        INameStore? names = null, string? haDiscoveryPrefix = null) =>
        Akka.Actor.Props.Create(() => new MatterGatewayActor(controller, mqtt, topics, names, haDiscoveryPrefix));

    public MatterGatewayActor(IMatterController controller, IMqttPublisher mqtt, MqttTopics topics,
        INameStore? names = null, string? haDiscoveryPrefix = null)
    {
        _controller = controller; _mqtt = mqtt; _topics = topics; _haPrefix = haDiscoveryPrefix;
        ...
```

2. Add a handler next to `Receive<MqttConnected>`:

```csharp
        Receive<HaStatusOnline>(_ =>
        {
            foreach (var reg in _byName.Values)
            {
                PublishHaDiscovery(reg.Descriptor);
                reg.Actor.Tell(new Republish());
            }
        });
```

3. Add the two helpers:

```csharp
    private void PublishHaDiscovery(DeviceDescriptor descriptor)
    {
        if (_haPrefix is null) return;
        foreach (var m in HaDiscoveryBuilder.Build(descriptor, _topics, _haPrefix))
            _mqtt.PublishRetained(m.Topic, m.Payload);
    }

    // A retained empty payload deletes the config topic -> HA removes the entity.
    private void ClearHaDiscovery(DeviceDescriptor descriptor)
    {
        if (_haPrefix is null) return;
        foreach (var m in HaDiscoveryBuilder.Build(descriptor, _topics, _haPrefix))
            _mqtt.PublishRetained(m.Topic, "");
    }
```

4. Wire the call sites:
   - `OnNodeAdded`: after `PublishDevices();` add `PublishHaDiscovery(descriptor);`
   - `OnNodeRemoved`: inside the `foreach`, after `Context.Stop(reg.Actor);` add `ClearHaDiscovery(reg.Descriptor);`
   - `DoRename`: after `PublishDevices();` add `PublishHaDiscovery(updated.Descriptor);`
   - `AnnounceAll`: change the loop to also publish discovery:

```csharp
    private void AnnounceAll()
    {
        _mqtt.PublishRetained(_topics.BridgeState(), """{"state":"online"}""");
        PublishDevices();
        foreach (var reg in _byName.Values)
        {
            PublishHaDiscovery(reg.Descriptor);
            reg.Actor.Tell(new Republish());
        }
    }
```

- [ ] **Step 4: Run the full test suite**

Run: `dotnet test`
Expected: all tests PASS (existing gateway tests compile unchanged thanks to the optional parameter).

- [ ] **Step 5: Commit**

```bash
git add src/Matterhorn/Bridge/BridgeMessages.cs src/Matterhorn/Bridge/MatterGatewayActor.cs src/Matterhorn.Test/Bridge/MatterGatewayActorTests.cs
git commit -m "feat: gateway publishes HA discovery configs over the device lifecycle"
```

---

### Task 6: Config keys, HA status routing, host wiring

**Files:**
- Modify: `src/Matterhorn/Configuration/MatterhornConfig.cs`
- Modify: `src/Matterhorn/Mqtt/MqttCommandRouter.cs`
- Modify: `src/Matterhorn/Mqtt/MqttBridgeService.cs`
- Modify: `src/Matterhorn/Program.cs:51`
- Test: `src/Matterhorn.Test/Mqtt/MqttCommandRouterTests.cs`

**Interfaces:**
- Consumes: `HaStatusOnline` from Task 5.
- Produces: `MatterhornConfig` gains `bool HaEnabled` and `string HaDiscoveryTopic`; `MqttCommandRouter.Route(MqttTopics topics, string topic, string payload, ICanTell gateway, string? haStatusTopic = null)`.

- [ ] **Step 1: Write the failing tests**

Append to `MqttCommandRouterTests`:

```csharp
    [Fact]
    public void Ha_status_online_forwards_HaStatusOnline()
    {
        var gw = CreateTestProbe();
        MqttCommandRouter.Route(_topics, "homeassistant/status", "online", gw.Ref, "homeassistant/status");
        gw.ExpectMsg<HaStatusOnline>();
    }

    [Fact]
    public void Ha_status_offline_forwards_nothing()
    {
        var gw = CreateTestProbe();
        MqttCommandRouter.Route(_topics, "homeassistant/status", "offline", gw.Ref, "homeassistant/status");
        gw.ExpectNoMsg(TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public void Ha_status_topic_is_ignored_when_discovery_is_disabled()
    {
        var gw = CreateTestProbe();
        MqttCommandRouter.Route(_topics, "homeassistant/status", "online", gw.Ref);
        gw.ExpectNoMsg(TimeSpan.FromMilliseconds(200));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~MqttCommandRouterTests"`
Expected: build FAILS (`Route` has no 5th parameter).

- [ ] **Step 3: Implement**

`MatterhornConfig.cs` — full record after the change:

```csharp
using Microsoft.Extensions.Configuration;

namespace Matterhorn.Configuration;

/// <summary>Strongly-typed service configuration.</summary>
public record MatterhornConfig(
    string ControllerWsUrl, string ControllerKind,
    string MqttHost, int MqttPort, string? MqttUser, string? MqttPassword,
    string BaseTopic, bool RestEnabled, int RestPort, string? ApiKey, string? ThreadDataset,
    string NamesFile, bool HaEnabled, string HaDiscoveryTopic)
{
    public static MatterhornConfig FromConfiguration(IConfiguration c) => new(
        ControllerWsUrl: c["Controller:WsUrl"] ?? "ws://localhost:5580/ws",
        ControllerKind: c["Controller:Kind"] ?? "matterjs-server",
        MqttHost: c["Mqtt:Host"] ?? "localhost",
        MqttPort: int.TryParse(c["Mqtt:Port"], out var p) ? p : 1883,
        MqttUser: c["Mqtt:User"], MqttPassword: c["Mqtt:Password"],
        BaseTopic: c["Mqtt:BaseTopic"] ?? "matterhorn",
        RestEnabled: !bool.TryParse(c["Rest:Enabled"], out var re) || re,
        RestPort: int.TryParse(c["Rest:Port"], out var rp) ? rp : 8090,
        ApiKey: c["Rest:ApiKey"], ThreadDataset: c["Thread:Dataset"],
        NamesFile: c["Storage:NamesFile"] ?? "data/names.json",
        HaEnabled: bool.TryParse(c["HomeAssistant:Enabled"], out var ha) && ha,
        HaDiscoveryTopic: c["HomeAssistant:DiscoveryTopic"] ?? "homeassistant");
}
```

`MqttCommandRouter.Route` — new signature and first block:

```csharp
    public static void Route(MqttTopics topics, string topic, string payload, ICanTell gateway,
        string? haStatusTopic = null)
    {
        // HA publishes its birth message here on startup; re-announce so entities reappear.
        if (haStatusTopic is not null && topic == haStatusTopic)
        {
            if (payload.Trim().Equals("online", StringComparison.OrdinalIgnoreCase))
                gateway.Tell(new HaStatusOnline(), ActorRefs.NoSender);
            return;
        }

        if (topics.TryParseSet(topic, out var name, out var attr))
        ...
```

`MqttBridgeService` — inject config, subscribe, and pass the status topic through:

```csharp
public sealed class MqttBridgeService(
    HiveMQClient client, MqttTopics topics, MatterhornConfig cfg, ActorRegistry registry, ILogger<MqttBridgeService> logger)
    : BackgroundService
{
    private readonly string? _haStatusTopic = cfg.HaEnabled ? $"{cfg.HaDiscoveryTopic}/status" : null;
```

(add `using Matterhorn.Configuration;`), change the routing call to

```csharp
            try { MqttCommandRouter.Route(topics, e.PublishMessage.Topic ?? "", e.PublishMessage.PayloadAsString ?? "", gateway, _haStatusTopic); }
```

and extend `SubscribeInbound`:

```csharp
        if (_haStatusTopic is not null)
            await client.SubscribeAsync(_haStatusTopic, QualityOfService.AtLeastOnceDelivery);
```

`Program.cs:51` — pass the prefix into the gateway:

```csharp
        var gw = system.ActorOf(MatterGatewayActor.Props(controller, publisher, topics, names,
            cfg.HaEnabled ? cfg.HaDiscoveryTopic : null), "gateway");
```

(`MatterhornConfig` is already a DI singleton, so `MqttBridgeService` needs no extra registration.)

- [ ] **Step 4: Run the full test suite and build**

Run: `dotnet build && dotnet test`
Expected: build succeeds, all tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Matterhorn/Configuration/MatterhornConfig.cs src/Matterhorn/Mqtt/MqttCommandRouter.cs src/Matterhorn/Mqtt/MqttBridgeService.cs src/Matterhorn/Program.cs src/Matterhorn.Test/Mqtt/MqttCommandRouterTests.cs
git commit -m "feat: opt-in HomeAssistant discovery config and HA birth-message handling"
```

---

### Task 7: Documentation

**Files:**
- Modify: `docs/guide/configuration.md`
- Modify: `docs/guide/control.md`

**Interfaces:** none (docs only).

- [ ] **Step 1: Document the config keys**

In the table in `docs/guide/configuration.md`, add two rows after the `Mqtt__BaseTopic` row:

```markdown
| — | `HomeAssistant__Enabled` | publish [HA MQTT Discovery](/guide/control#home-assistant) configs | `false` |
| — | `HomeAssistant__DiscoveryTopic` | HA discovery prefix | `homeassistant` |
```

- [ ] **Step 2: Document the feature**

Append this section to `docs/guide/control.md` (read the page first and match its tone/heading level; adjust the wording if it clashes):

```markdown
## Home Assistant

With `HomeAssistant__Enabled=true`, Matterhorn announces every device to Home Assistant via
[MQTT Discovery](https://www.home-assistant.io/integrations/mqtt/#mqtt-discovery) — the same
mechanism Zigbee2MQTT uses. Lights (on/off, brightness, color temperature, hue/saturation)
appear as a single `light` entity, plain on/off devices as a `switch`, and contact, occupancy,
temperature, humidity, illuminance, and battery readings as `binary_sensor`/`sensor` entities.

Entities keep their identity across renames (discovery keys on the Matter node, not the friendly
name), availability follows both the bridge state and the per-device availability topic, and when
Home Assistant restarts, Matterhorn re-announces everything automatically. Removing a device
clears its retained discovery configs, so the entities disappear from HA as well.

The discovery prefix defaults to `homeassistant`; set `HomeAssistant__DiscoveryTopic` if your HA
instance uses a custom one.
```

- [ ] **Step 3: Commit**

```bash
git add docs/guide/configuration.md docs/guide/control.md
git commit -m "docs: document Home Assistant MQTT discovery"
```
