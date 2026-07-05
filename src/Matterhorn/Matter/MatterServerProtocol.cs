using System.Text.Json;
using Matterhorn.Bridge;
using Matterhorn.Devices;

namespace Matterhorn.Matter;

/// <summary>
/// A reply to a command we sent, correlated by <c>message_id</c>. <see cref="NodeId"/> is set
/// when the result carries one (e.g. <c>commission_with_code</c>); <see cref="Error"/> is set
/// when the server returned an <c>error_code</c> instead of a result.
/// </summary>
public sealed record ServerResult(int MessageId, ulong? NodeId, string? Error);

/// <summary>
/// Pure WS-JSON ⇄ domain codec for the matter-server WebSocket protocol (matterjs-server /
/// python-matter-server).
/// Isolated here so the wire format stays behind <see cref="IMatterController"/>.
/// </summary>
public static class MatterServerProtocol
{
    public static string StartListening(int messageId) =>
        JsonSerializer.Serialize(new { message_id = messageId.ToString(), command = "start_listening" });

    public static string CommissionWithCode(int messageId, string setupCode) =>
        JsonSerializer.Serialize(new
        {
            message_id = messageId.ToString(),
            command = "commission_with_code",
            // network_only forces on-network (IP) commissioning: a software controller has no
            // Bluetooth radio, and a multi-admin device we're joining is already on Wi-Fi.
            args = new { code = setupCode, network_only = true },
        });

    public static string RemoveNode(int messageId, ulong nodeId) =>
        JsonSerializer.Serialize(new
        {
            message_id = messageId.ToString(),
            command = "remove_node",
            args = new { node_id = nodeId },
        });

    public static string DeviceCommand(int messageId, ulong nodeId, ushort endpoint, CommandSpec cmd) =>
        JsonSerializer.Serialize(new
        {
            message_id = messageId.ToString(),
            command = "device_command",
            args = new
            {
                node_id = nodeId,
                endpoint_id = endpoint,
                cluster_id = cmd.ClusterId,
                command_name = cmd.CommandName,
                payload = cmd.Payload,
            }
        });

    /// <summary>
    /// Classifies an incoming frame: command replies carry a <c>message_id</c>, events don't.
    /// Returns true (and fills <paramref name="result"/>) only for reply frames.
    /// </summary>
    public static bool TryParseResult(string json, out ServerResult result)
    {
        result = null!;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("message_id", out var idElement)
            || !int.TryParse(idElement.GetString(), out var messageId))
            return false;

        if (root.TryGetProperty("error_code", out _))
        {
            var details = root.TryGetProperty("details", out var d) ? d.GetString() : null;
            result = new ServerResult(messageId, null, details ?? "unknown error");
            return true;
        }

        ulong? nodeId = null;
        if (root.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.Object
            && res.TryGetProperty("node_id", out var n) && n.ValueKind == JsonValueKind.Number)
            nodeId = n.GetUInt64();

        result = new ServerResult(messageId, nodeId, null);
        return true;
    }

    public static IEnumerable<MatterEvent> ParseIncoming(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("event", out var evt) || !root.TryGetProperty("data", out var data))
            yield break;

        switch (evt.GetString())
        {
            // attribute_updated data is [nodeId, "<endpoint>/<cluster>/<attribute>", value].
            case "attribute_updated" when data.ValueKind == JsonValueKind.Array && data.GetArrayLength() == 3:
                var path = data[1].GetString()!.Split('/');
                yield return new AttributeChanged(new AttributeReading(
                    (ulong)data[0].GetInt64(), ushort.Parse(path[0]), uint.Parse(path[1]), uint.Parse(path[2]),
                    data[2].Clone()));
                break;

            // node_added data is the full MatterNodeData object. Emit the device (NodeAdded) first, then
            // seed its current state from the snapshot's cached attribute values (NodeAdded must precede
            // the readings so the endpoint actor exists before they route to it).
            case "node_added":
                foreach (var added in ParseNode(data)) yield return added;
                foreach (var reading in ParseNodeState(data)) yield return reading;
                break;

            // node_updated carries the full node again; surface its availability as reachability
            // (a device that joined available:false mid-interview flips to reachable here).
            case "node_updated":
                foreach (var updated in ParseNode(data))
                    yield return new ReachabilityChanged(
                        updated.Endpoint.NodeId, updated.Endpoint.Endpoint, updated.Endpoint.Reachable);
                break;

            // node_removed data is just the node id.
            case "node_removed" when data.ValueKind == JsonValueKind.Number:
                yield return new NodeRemoved(data.GetUInt64());
                break;
        }
    }

    /// <summary>Expands a <c>start_listening</c> reply (the array of already-commissioned nodes) into
    /// <see cref="NodeAdded"/> events, so devices on the fabric survive a Matterhorn restart.</summary>
    public static IEnumerable<MatterEvent> ParseNodeList(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var node in result.EnumerateArray())
        {
            foreach (var added in ParseNode(node)) yield return added;
            foreach (var reading in ParseNodeState(node)) yield return reading;
        }
    }

    /// <summary>
    /// Reads the mappable current-state attributes out of a full node snapshot (the array from
    /// <c>start_listening</c>, or a <c>node_added</c> payload) as <see cref="AttributeChanged"/> events,
    /// so retained state is seeded on (re)connect from the values the matter-server already cached
    /// rather than waiting for the next <c>attribute_updated</c>. Root-endpoint (0) attributes are
    /// skipped — endpoint 0 is the node, not a device, and has no endpoint actor to route to.
    /// </summary>
    public static IEnumerable<AttributeChanged> ParseNodeState(JsonElement node)
    {
        if (!node.TryGetProperty("attributes", out var attrs) || attrs.ValueKind != JsonValueKind.Object)
            yield break;

        var nodeId = node.GetProperty("node_id").GetUInt64();
        foreach (var attr in attrs.EnumerateObject())
        {
            var parts = attr.Name.Split('/');
            if (parts.Length != 3
                || !ushort.TryParse(parts[0], out var ep) || ep == 0
                || !uint.TryParse(parts[1], out var cluster)
                || !uint.TryParse(parts[2], out var attribute)
                || !PropertyMapping.IsMappable(cluster, attribute))
                continue;

            yield return new AttributeChanged(new AttributeReading(nodeId, ep, cluster, attribute, attr.Value.Clone()));
        }
    }

    /// <summary>
    /// Expands one matter-server node (MatterNodeData) into a <see cref="NodeAdded"/> per
    /// application endpoint. Basic Information (cluster 0x0028 on endpoint 0) supplies vendor/product
    /// identity; each non-root endpoint's Descriptor (0x001D) ServerList becomes its cluster set and
    /// DeviceTypeList its type. Endpoint 0 is the root node, not a device, so it is skipped.
    /// </summary>
    public static IEnumerable<NodeAdded> ParseNode(JsonElement node)
    {
        if (!node.TryGetProperty("attributes", out var attrs) || attrs.ValueKind != JsonValueKind.Object)
            yield break;

        var nodeId = node.GetProperty("node_id").GetUInt64();
        var reachable = !node.TryGetProperty("available", out var av) || av.ValueKind != JsonValueKind.False;

        string? vendorName = null, productName = null;
        ushort vendorId = 0, productId = 0;
        var transport = "unknown";
        var serverLists = new Dictionary<ushort, List<uint>>();
        var deviceTypes = new Dictionary<ushort, uint>();
        var colorFeatures = new Dictionary<ushort, uint>();

        foreach (var attr in attrs.EnumerateObject())
        {
            var parts = attr.Name.Split('/');
            if (parts.Length != 3
                || !ushort.TryParse(parts[0], out var ep)
                || !uint.TryParse(parts[1], out var cluster)
                || !uint.TryParse(parts[2], out var attribute))
                continue;

            if (ep == 0 && cluster == MatterClusters.BasicInformation)
            {
                switch (attribute)
                {
                    case 1: vendorName = attr.Value.GetString(); break;
                    case 2: vendorId = ReadUInt16(attr.Value); break;
                    case 3: productName = attr.Value.GetString(); break;
                    case 4: productId = ReadUInt16(attr.Value); break;
                }
            }
            else if (ep != 0 && cluster == MatterClusters.Descriptor)
            {
                if (attribute == 1) serverLists[ep] = ReadUInt32List(attr.Value);      // ServerList
                else if (attribute == 0 && TryReadDeviceType(attr.Value, out var dt))  // DeviceTypeList
                    deviceTypes[ep] = dt;
            }
            else if (ep == 0 && cluster == MatterClusters.NetworkCommissioning && attribute == 0xFFFC
                     && attr.Value.ValueKind == JsonValueKind.Number && attr.Value.TryGetUInt32(out var netFm))
                transport = DecodeTransport(netFm);
            else if (cluster == MatterClusters.ColorControl && attribute == 0xFFFC
                     && attr.Value.ValueKind == JsonValueKind.Number && attr.Value.TryGetUInt32(out var colFm))
                colorFeatures[ep] = colFm;
        }

        foreach (var (ep, clusters) in serverLists)
        {
            var type = deviceTypes.TryGetValue(ep, out var dt) ? DeviceTypeName(dt) : "Unknown";
            yield return new NodeAdded(new EndpointInfo(
                nodeId, ep, vendorName, productName, vendorId, productId, type, reachable, clusters,
                transport, colorFeatures.GetValueOrDefault(ep)));
        }
    }

    private static ushort ReadUInt16(JsonElement v) =>
        v.ValueKind == JsonValueKind.Number && v.TryGetUInt16(out var n) ? n : (ushort)0;

    private static List<uint> ReadUInt32List(JsonElement arr)
    {
        var list = new List<uint>();
        if (arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Number && e.TryGetUInt32(out var n)) list.Add(n);
        return list;
    }

    private static bool TryReadDeviceType(JsonElement list, out uint deviceType)
    {
        deviceType = 0;
        if (list.ValueKind != JsonValueKind.Array) return false;
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Number && entry.TryGetUInt32(out var flat)) { deviceType = flat; return true; }
            if (entry.ValueKind != JsonValueKind.Object) continue;
            // TLV structs serialize with numeric field-id keys ("0" = DeviceType); tolerate named keys.
            foreach (var key in new[] { "0", "device_type", "deviceType" })
                if (entry.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out var n))
                {
                    deviceType = n;
                    return true;
                }
        }
        return false;
    }

    private static string DecodeTransport(uint featureMap) =>
        (featureMap & 0x02) != 0 ? "thread"
        : (featureMap & 0x01) != 0 ? "wifi"
        : (featureMap & 0x04) != 0 ? "ethernet"
        : "unknown";

    private static string DeviceTypeName(uint deviceType) => deviceType switch
    {
        0x0100 => "On/Off Light",
        0x0101 => "Dimmable Light",
        0x010C => "Color Temperature Light",
        0x010D => "Extended Color Light",
        0x010A => "On/Off Plug-in Unit",
        0x010B => "Dimmable Plug-in Unit",
        0x0015 => "Contact Sensor",
        0x0107 => "Occupancy Sensor",
        0x0106 => "Light Sensor",
        0x0302 => "Temperature Sensor",
        0x0307 => "Humidity Sensor",
        0x0305 => "Pressure Sensor",
        0x0306 => "Flow Sensor",
        0x000A => "Door Lock",
        0x0202 => "Window Covering",
        0x0301 => "Thermostat",
        _ => $"0x{deviceType:X4}",
    };
}
