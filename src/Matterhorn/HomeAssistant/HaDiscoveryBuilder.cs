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
