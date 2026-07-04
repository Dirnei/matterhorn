using System.Text.Json;
using Akka.Actor;
using Akka.TestKit.Xunit2;
using Matter2Mqtt.Bridge;
using Matter2Mqtt.Matter;
using Matter2Mqtt.Mqtt;
using Matter2Mqtt.Test.Mqtt;

namespace Matter2Mqtt.Test.Bridge;

public class MatterGatewayActorTests : TestKit
{
    private static EndpointInfo Light(ulong node) =>
        new(node, 1, "Nanoleaf", "Bulb", 4442, 3, "OnOffDimmableLight", true,
            new[] { MatterClusters.OnOff, MatterClusters.LevelControl });

    [Fact]
    public void NodeAdded_publishes_bridge_devices_and_registers_device()
    {
        var fake = new FakeMatterController();
        var mqtt = new InMemoryMqttPublisher();
        var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, mqtt, new MqttTopics("matter2mqtt")));

        fake.Emit(new NodeAdded(Light(1)));

        AwaitAssert(() => Assert.Contains(mqtt.Messages, m => m.Topic == "matter2mqtt/bridge/devices"));

        var devices = gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result;
        Assert.Single(devices);
        Assert.Equal("bulb_1_1", devices[0].FriendlyName);
    }

    [Fact]
    public void SetDevice_routes_to_endpoint_and_invokes_controller()
    {
        var fake = new FakeMatterController();
        var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, new InMemoryMqttPublisher(), new MqttTopics("matter2mqtt")));
        fake.Emit(new NodeAdded(Light(9)));
        AwaitAssert(() => Assert.NotEmpty(gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result));

        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"state":"ON"}""")!;
        gw.Tell(new SetDevice("bulb_9_1", payload));

        AwaitAssert(() => Assert.Contains(fake.Invocations, i => i.NodeId == 9 && i.Cmd.CommandName == "On"));
    }

    [Fact]
    public void CommissionRequest_publishes_response_with_node_id()
    {
        var fake = new FakeMatterController { OnCommission = _ => 55 };
        var mqtt = new InMemoryMqttPublisher();
        var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, mqtt, new MqttTopics("matter2mqtt")));

        gw.Tell(new CommissionRequest("MT:XXX", "tx1"));

        AwaitAssert(() =>
        {
            var resp = Assert.Single(mqtt.Messages, m => m.Topic == "matter2mqtt/bridge/response/commission");
            Assert.Contains("\"tx1\"", resp.Payload);
            Assert.Contains("55", resp.Payload);
        });
    }
}
