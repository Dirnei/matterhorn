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
    public void ParseIncoming_attribute_update_yields_AttributeChanged()
    {
        // python-matter-server attribute path format: "<endpoint>/<cluster>/<attribute>"
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
}
