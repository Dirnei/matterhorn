using System.Text.Json;
using Matterhorn.Devices;
using Matterhorn.Matter;

namespace Matterhorn.Test.Matter;

public class MatterServerProtocolTests
{
    [Fact]
    public void StartListening_has_command_and_message_id()
    {
        var json = MatterServerProtocol.StartListening(3);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("start_listening", doc.RootElement.GetProperty("command").GetString());
        Assert.Equal("3", doc.RootElement.GetProperty("message_id").GetString());
    }

    [Fact]
    public void DeviceCommand_carries_node_endpoint_cluster_command()
    {
        var cmd = new CommandSpec(MatterClusters.OnOff, "On", new Dictionary<string, object?>());
        var json = MatterServerProtocol.DeviceCommand(7, 1, 1, cmd);
        using var doc = JsonDocument.Parse(json);
        var args = doc.RootElement.GetProperty("args");
        Assert.Equal("device_command", doc.RootElement.GetProperty("command").GetString());
        Assert.Equal(1, args.GetProperty("node_id").GetInt32());
        Assert.Equal(1, args.GetProperty("endpoint_id").GetInt32());
        Assert.Equal(6, args.GetProperty("cluster_id").GetInt32());
        Assert.Equal("On", args.GetProperty("command_name").GetString());
    }

    [Fact]
    public void WriteAttribute_serializes_write_attribute_command()
    {
        var json = MatterServerProtocol.WriteAttribute(7, nodeId: 42, endpoint: 1, clusterId: 0x0201, attributeId: 0x0012, value: 2100);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("7", root.GetProperty("message_id").GetString());
        Assert.Equal("write_attribute", root.GetProperty("command").GetString());
        var args = root.GetProperty("args");
        Assert.Equal(42UL, args.GetProperty("node_id").GetUInt64());
        Assert.Equal("1/513/18", args.GetProperty("attribute_path").GetString());
        Assert.Equal(2100, args.GetProperty("value").GetInt32());
    }

    [Fact]
    public void ParseIncoming_attribute_update_yields_AttributeChanged()
    {
        // matter-server attribute path format: "<endpoint>/<cluster>/<attribute>"
        var json = """
        {"event":"attribute_updated","data":[1, "1/6/0", true]}
        """;
        var evt = Assert.Single(MatterServerProtocol.ParseIncoming(json));
        var ac = Assert.IsType<AttributeChanged>(evt);
        Assert.Equal(1ul, ac.Reading.NodeId);
        Assert.Equal((ushort)1, ac.Reading.Endpoint);
        Assert.Equal(MatterClusters.OnOff, ac.Reading.ClusterId);
        Assert.Equal(0u, ac.Reading.AttributeId);
        Assert.True(ac.Reading.Value.GetBoolean());
    }

    [Fact]
    public void CommissionWithCode_carries_code_and_forces_network_only()
    {
        var json = MatterServerProtocol.CommissionWithCode(9, "MT:ABC123");
        using var doc = JsonDocument.Parse(json);
        var args = doc.RootElement.GetProperty("args");
        Assert.Equal("commission_with_code", doc.RootElement.GetProperty("command").GetString());
        Assert.Equal("9", doc.RootElement.GetProperty("message_id").GetString());
        Assert.Equal("MT:ABC123", args.GetProperty("code").GetString());
        // On-network commissioning: no Bluetooth radio on a software controller, and the multi-admin
        // device is already on Wi-Fi, so we only need to join its fabric over IP.
        Assert.True(args.GetProperty("network_only").GetBoolean());
    }

    [Fact]
    public void RemoveNode_carries_node_id()
    {
        var json = MatterServerProtocol.RemoveNode(4, 12345678901234);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("remove_node", doc.RootElement.GetProperty("command").GetString());
        Assert.Equal("4", doc.RootElement.GetProperty("message_id").GetString());
        Assert.Equal(12345678901234ul, doc.RootElement.GetProperty("args").GetProperty("node_id").GetUInt64());
    }

    [Fact]
    public void TryParseResult_success_extracts_message_id_and_node_id()
    {
        // commission_with_code replies with the freshly interviewed node on the same message_id.
        var json = """
        {"message_id":"9","result":{"node_id":42,"date_commissioned":"2026-07-04"}}
        """;
        Assert.True(MatterServerProtocol.TryParseResult(json, out var result));
        Assert.Equal(9, result.MessageId);
        Assert.Equal(42ul, result.NodeId);
        Assert.Null(result.Error);
    }

    [Fact]
    public void TryParseResult_null_result_has_no_node_id_and_no_error()
    {
        // remove_node acknowledges with a null result.
        var json = """{"message_id":"4","result":null}""";
        Assert.True(MatterServerProtocol.TryParseResult(json, out var result));
        Assert.Equal(4, result.MessageId);
        Assert.Null(result.NodeId);
        Assert.Null(result.Error);
    }

    [Fact]
    public void TryParseResult_error_surfaces_details()
    {
        var json = """
        {"message_id":"9","error_code":1,"details":"Commissioning failed: timeout"}
        """;
        Assert.True(MatterServerProtocol.TryParseResult(json, out var result));
        Assert.Equal(9, result.MessageId);
        Assert.Contains("timeout", result.Error);
    }

    [Fact]
    public void TryParseResult_returns_false_for_event_frame()
    {
        var json = """{"event":"attribute_updated","data":[1,"1/6/0",true]}""";
        Assert.False(MatterServerProtocol.TryParseResult(json, out _));
    }

    // A single dimmable bulb: root endpoint 0 carries Basic Information + its own Descriptor;
    // the light lives on endpoint 1 (Descriptor cluster 0x001D=29, ServerList attr 1, DeviceTypeList attr 0).
    private const string BulbNode = """
    {"node_id":42,"available":true,"is_bridge":false,"attributes":{
        "0/40/1":"Nanoleaf","0/40/2":4442,"0/40/3":"Essentials A19","0/40/4":3,
        "0/29/0":[{"0":22,"1":1}],"0/29/1":[29,31,40,48],
        "1/29/0":[{"0":257,"1":1}],"1/29/1":[6,8,768,29],
        "1/6/0":true,"1/8/0":254
    }}
    """;

    [Fact]
    public void ParseIncoming_node_added_yields_one_device_per_application_endpoint()
    {
        var json = $$"""{"event":"node_added","data":{{BulbNode}}}""";
        // Only endpoint 1 is a device — the root endpoint 0 is skipped even though it has a Descriptor.
        var info = Assert.Single(MatterServerProtocol.ParseIncoming(json).OfType<NodeAdded>()).Endpoint;
        Assert.Equal(42ul, info.NodeId);
        Assert.Equal((ushort)1, info.Endpoint);
        Assert.Equal("Nanoleaf", info.VendorName);
        Assert.Equal("Essentials A19", info.ProductName);
        Assert.Equal((ushort)4442, info.VendorId);
        Assert.Equal((ushort)3, info.ProductId);
        Assert.True(info.Reachable);
        Assert.Contains(MatterClusters.OnOff, info.ClusterIds);
        Assert.Contains(MatterClusters.LevelControl, info.ClusterIds);
        Assert.Contains(MatterClusters.ColorControl, info.ClusterIds);
    }

    [Fact]
    public void ParseIncoming_node_added_maps_device_type_to_a_name()
    {
        var json = $$"""{"event":"node_added","data":{{BulbNode}}}""";
        var info = Assert.Single(MatterServerProtocol.ParseIncoming(json).OfType<NodeAdded>()).Endpoint;
        Assert.Contains("Light", info.DeviceType); // 0x0101 → "Dimmable Light"
    }

    [Fact]
    public void ParseIncoming_node_added_uses_available_false_as_unreachable()
    {
        var json = """
        {"event":"node_added","data":{"node_id":7,"available":false,"attributes":{
            "1/29/1":[6],"1/6/0":false
        }}}
        """;
        var info = Assert.Single(MatterServerProtocol.ParseIncoming(json).OfType<NodeAdded>()).Endpoint;
        Assert.False(info.Reachable);
    }

    [Fact]
    public void ParseIncoming_node_added_yields_a_device_per_endpoint_on_a_bridge()
    {
        var json = """
        {"event":"node_added","data":{"node_id":9,"available":true,"is_bridge":true,"attributes":{
            "0/40/3":"Hub","0/29/1":[29,31],
            "1/29/1":[6],"1/29/0":[{"0":256,"1":1}],
            "2/29/1":[1030],"2/29/0":[{"0":263,"1":1}]
        }}}
        """;
        var endpoints = MatterServerProtocol.ParseIncoming(json)
            .Cast<NodeAdded>().Select(n => n.Endpoint.Endpoint).ToHashSet();
        Assert.Equal(new HashSet<ushort> { 1, 2 }, endpoints);
    }

    [Fact]
    public void ParseIncoming_node_removed_yields_node_scoped_removal()
    {
        var evt = Assert.Single(MatterServerProtocol.ParseIncoming("""{"event":"node_removed","data":42}"""));
        Assert.Equal(42ul, Assert.IsType<NodeRemoved>(evt).NodeId);
    }

    [Fact]
    public void ParseIncoming_node_updated_yields_reachability_per_endpoint()
    {
        // node_updated carries the full node; we surface its availability as reachability so a device
        // that reported available:false mid-interview flips to reachable once it settles.
        var json = """
        {"event":"node_updated","data":{"node_id":42,"available":true,"attributes":{
            "1/29/1":[6],"2/29/1":[1030]
        }}}
        """;
        var events = MatterServerProtocol.ParseIncoming(json).Cast<ReachabilityChanged>().ToList();
        Assert.All(events, e => Assert.True(e.Reachable));
        Assert.Equal(new HashSet<ushort> { 1, 2 }, events.Select(e => e.Endpoint).ToHashSet());
        Assert.All(events, e => Assert.Equal(42ul, e.NodeId));
    }

    [Fact]
    public void ParseNodeList_expands_the_start_listening_snapshot()
    {
        // start_listening replies with the array of nodes already on the fabric (survives restarts).
        var json = $$"""{"message_id":"1","result":[{{BulbNode}}]}""";
        var evt = Assert.Single(MatterServerProtocol.ParseNodeList(json).OfType<NodeAdded>());
        Assert.Equal(42ul, evt.Endpoint.NodeId);
    }

    [Fact]
    public void ParseNode_seeds_current_state_from_the_snapshot_attributes()
    {
        // The snapshot already carries last-known values (OnOff 1/6/0=true, LevelControl 1/8/0=254);
        // surface them as AttributeChanged so retained state is populated immediately on (re)connect
        // instead of waiting for the next attribute_updated event.
        var json = $$"""{"event":"node_added","data":{{BulbNode}}}""";
        var readings = MatterServerProtocol.ParseIncoming(json).OfType<AttributeChanged>()
            .Select(a => a.Reading).ToList();

        var state = Assert.Single(readings, r => r.ClusterId == MatterClusters.OnOff && r.AttributeId == 0);
        Assert.Equal((ushort)1, state.Endpoint);
        Assert.Equal(42ul, state.NodeId);
        Assert.True(state.Value.GetBoolean());

        var level = Assert.Single(readings, r => r.ClusterId == MatterClusters.LevelControl && r.AttributeId == 0);
        Assert.Equal(254, level.Value.GetInt32());
    }

    [Fact]
    public void ParseNode_seeds_state_only_for_mappable_attributes()
    {
        // Identity/topology attributes (Descriptor ServerList, feature maps, BasicInformation) are not
        // device state, so they must not leak into the seeded readings.
        var json = $$"""{"event":"node_added","data":{{BulbNode}}}""";
        var readings = MatterServerProtocol.ParseIncoming(json).OfType<AttributeChanged>().ToList();
        Assert.All(readings, r => Assert.True(PropertyMapping.IsMappable(r.Reading.ClusterId, r.Reading.AttributeId)));
    }

    [Fact]
    public void ParseNode_emits_NodeAdded_before_the_seeded_state()
    {
        // Ordering matters: the gateway must spawn the endpoint actor (NodeAdded) before the seeded
        // readings route to it, so every NodeAdded has to precede the first AttributeChanged.
        var json = $$"""{"event":"node_added","data":{{BulbNode}}}""";
        var events = MatterServerProtocol.ParseIncoming(json).ToList();
        var lastNodeAdded = events.FindLastIndex(e => e is NodeAdded);
        var firstReading = events.FindIndex(e => e is AttributeChanged);
        Assert.True(firstReading == -1 || lastNodeAdded < firstReading);
    }

    [Fact]
    public void ParseNodeList_seeds_current_state_across_restarts()
    {
        // The whole point: after a Matterhorn restart the start_listening snapshot rehydrates state.
        var json = $$"""{"message_id":"1","result":[{{BulbNode}}]}""";
        var readings = MatterServerProtocol.ParseNodeList(json).OfType<AttributeChanged>()
            .Select(a => a.Reading).ToList();
        Assert.Contains(readings, r => r.ClusterId == MatterClusters.OnOff && r.Value.GetBoolean());
        Assert.Contains(readings, r => r.ClusterId == MatterClusters.LevelControl && r.Value.GetInt32() == 254);
    }

    [Fact]
    public void ParseNode_reads_wifi_transport_and_color_features()
    {
        var json = """
        {"event":"node_added","data":{"node_id":1,"available":true,"attributes":{
            "0/49/65532":1,
            "1/29/1":[6,8,768],"1/768/65532":25
        }}}
        """;
        var info = Assert.IsType<NodeAdded>(Assert.Single(MatterServerProtocol.ParseIncoming(json))).Endpoint;
        Assert.Equal("wifi", info.Transport);
        Assert.Equal(25u, info.ColorFeatures);
    }

    [Theory]
    [InlineData(1, "wifi")]
    [InlineData(2, "thread")]
    [InlineData(4, "ethernet")]
    [InlineData(3, "thread")]   // Thread wins when both bits set
    [InlineData(0, "unknown")]
    public void ParseNode_decodes_transport_from_network_commissioning(int featureMap, string expected)
    {
        var json = "{\"event\":\"node_added\",\"data\":{\"node_id\":1,\"available\":true,\"attributes\":{"
                 + "\"0/49/65532\":" + featureMap + ",\"1/29/1\":[6]}}}";
        var info = Assert.IsType<NodeAdded>(Assert.Single(MatterServerProtocol.ParseIncoming(json))).Endpoint;
        Assert.Equal(expected, info.Transport);
    }
}
