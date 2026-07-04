using System.Text.Json;
using Akka.Actor;
using Akka.TestKit.Xunit2;
using Matterhorn.Bridge;
using Matterhorn.Devices;
using Matterhorn.Matter;
using Matterhorn.Mqtt;
using Matterhorn.Persistence;
using Matterhorn.Test.Mqtt;
using Matterhorn.Test.Persistence;

namespace Matterhorn.Test.Bridge;

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
        var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, mqtt, new MqttTopics("matterhorn")));

        fake.Emit(new NodeAdded(Light(1)));

        AwaitAssert(() => Assert.Contains(mqtt.Messages, m => m.Topic == "matterhorn/bridge/devices"));

        var devices = gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result;
        Assert.Single(devices);
        Assert.Equal("bulb_1_1", devices[0].FriendlyName);
    }

    [Fact]
    public void Bridge_devices_payload_is_snake_case()
    {
        var fake = new FakeMatterController();
        var mqtt = new InMemoryMqttPublisher();
        var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, mqtt, new MqttTopics("matterhorn")));

        fake.Emit(new NodeAdded(Light(1)));

        AwaitAssert(() =>
        {
            var msg = Assert.Single(mqtt.Messages, m => m.Topic == "matterhorn/bridge/devices");
            Assert.Contains("\"friendly_name\":\"bulb_1_1\"", msg.Payload);
            Assert.Contains("\"value_on\":\"ON\"", msg.Payload);
            Assert.DoesNotContain("FriendlyName", msg.Payload);
            Assert.DoesNotContain("\"value_on\":null", msg.Payload); // null fields omitted
        });
    }

    [Fact]
    public void SetDevice_routes_to_endpoint_and_invokes_controller()
    {
        var fake = new FakeMatterController();
        var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, new InMemoryMqttPublisher(), new MqttTopics("matterhorn")));
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
        var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, mqtt, new MqttTopics("matterhorn")));

        gw.Tell(new CommissionRequest("MT:XXX", "tx1"));

        AwaitAssert(() =>
        {
            var resp = Assert.Single(mqtt.Messages, m => m.Topic == "matterhorn/bridge/response/commission");
            Assert.Contains("\"tx1\"", resp.Payload);
            Assert.Contains("55", resp.Payload);
        });
    }

    [Fact]
    public void Attribute_after_node_added_publishes_device_state()
    {
        // Regression: an attribute emitted right after NodeAdded must not be dropped — the gateway
        // registers the endpoint before it processes the following attribute (FIFO mailbox).
        var fake = new FakeMatterController();
        var mqtt = new InMemoryMqttPublisher();
        var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, mqtt, new MqttTopics("matterhorn")));

        fake.Emit(new NodeAdded(Light(4)));
        fake.Emit(new AttributeChanged(new AttributeReading(4, 1, MatterClusters.OnOff, 0,
            JsonDocument.Parse("true").RootElement)));

        AwaitAssert(() => Assert.Contains(mqtt.Messages,
            m => m.Topic == "matterhorn/bulb_4_1" && m.Payload.Contains("\"state\":\"ON\"")));
    }

    [Fact]
    public void Gateway_stays_responsive_while_a_commission_is_in_flight()
    {
        // Regression: OnCommission must not block the actor for the whole (30-60s) commission,
        // or GetDevices/attribute routing stall. A pending commission is held open while we Ask.
        var pending = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeMatterController { PendingCommission = pending.Task };
        var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, new InMemoryMqttPublisher(), new MqttTopics("matterhorn")));
        fake.Emit(new NodeAdded(Light(1)));
        AwaitAssert(() => Assert.Single(gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result));

        gw.Tell(new CommissionRequest("MT:XXX", "tx1"));

        // Must answer well within the Ask timeout even though the commission has not completed.
        var devices = gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices(), TimeSpan.FromSeconds(2)).Result;
        Assert.Single(devices);
        pending.SetResult(99); // let the commission finish so nothing dangles
    }

    [Fact]
    public void ReachabilityChanged_updates_the_descriptor_and_republishes()
    {
        var fake = new FakeMatterController();
        var mqtt = new InMemoryMqttPublisher();
        var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, mqtt, new MqttTopics("matterhorn")));
        // Joins unreachable (as our bulb did mid-interview).
        fake.Emit(new NodeAdded(new EndpointInfo(1, 1, "Nanoleaf", "Bulb", 4442, 3, "Dimmable Light", false,
            new[] { MatterClusters.OnOff, MatterClusters.LevelControl })));
        AwaitAssert(() => Assert.False(gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result[0].Reachable));

        fake.Emit(new ReachabilityChanged(1, 1, true));

        AwaitAssert(() => Assert.True(gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result[0].Reachable));
    }

    [Fact]
    public void NodeAdded_carries_transport_into_the_descriptor()
    {
        var fake = new FakeMatterController();
        var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, new InMemoryMqttPublisher(), new MqttTopics("matterhorn")));
        fake.Emit(new NodeAdded(new EndpointInfo(1, 1, "V", "P", 1, 1, "Extended Color Light", true,
            new[] { MatterClusters.OnOff }, "wifi", 0x01)));

        AwaitAssert(() => Assert.Equal("wifi",
            gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result[0].Transport));
    }

    [Fact]
    public void NodeRemoved_drops_every_endpoint_of_the_node()
    {
        var fake = new FakeMatterController();
        var mqtt = new InMemoryMqttPublisher();
        var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, mqtt, new MqttTopics("matterhorn")));

        fake.Emit(new NodeAdded(Light(7)));
        fake.Emit(new NodeAdded(new EndpointInfo(7, 2, "Nanoleaf", "Sensor", 4442, 3, "OccupancySensor", true,
            new[] { MatterClusters.OccupancySensing })));
        AwaitAssert(() => Assert.Equal(2, gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result.Count));

        fake.Emit(new NodeRemoved(7));

        AwaitAssert(() => Assert.Empty(gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result));
    }

    [Fact]
    public void MqttConnected_reannounces_bridge_state_and_devices()
    {
        var fake = new FakeMatterController();
        var mqtt = new InMemoryMqttPublisher();
        var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, mqtt, new MqttTopics("matterhorn")));
        fake.Emit(new NodeAdded(Light(3)));
        AwaitAssert(() => Assert.NotEmpty(gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result));

        gw.Tell(new MqttConnected());

        AwaitAssert(() =>
        {
            Assert.Contains(mqtt.Messages, m => m.Topic == "matterhorn/bridge/state" && m.Payload.Contains("online"));
            Assert.Contains(mqtt.Messages, m => m.Topic == "matterhorn/bridge/devices");
        });
    }

    [Fact]
    public void Rename_rekeys_the_device_and_republishes_bridge_devices()
    {
        var fake = new FakeMatterController();
        var mqtt = new InMemoryMqttPublisher();
        var store = new InMemoryNameStore();
        var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, mqtt, new MqttTopics("matterhorn"), store));
        fake.Emit(new NodeAdded(Light(5)));
        AwaitAssert(() => Assert.Single(gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result));

        var result = gw.Ask<RenameResult>(new RenameRequest("bulb_5_1", "Living Room Lamp!", "tx1")).Result;

        Assert.True(result.Ok);
        Assert.Equal("living_room_lamp", result.NewName); // slugified
        var devices = gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result;
        Assert.Equal("living_room_lamp", Assert.Single(devices).FriendlyName);
        Assert.Equal("living_room_lamp", store.Names[(5UL, 1)]); // persisted
        AwaitAssert(() => Assert.Contains(mqtt.Messages,
            m => m.Topic == "matterhorn/bridge/response/rename" && m.Payload.Contains("\"tx1\"") && m.Payload.Contains("ok")));
    }

    [Fact]
    public void Rename_unknown_device_returns_not_found()
    {
        var fake = new FakeMatterController();
        var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, new InMemoryMqttPublisher(), new MqttTopics("matterhorn")));

        var result = gw.Ask<RenameResult>(new RenameRequest("ghost", "whatever", "tx1")).Result;

        Assert.False(result.Ok);
        Assert.Equal("not_found", result.Error);
    }

    [Fact]
    public void Rename_to_an_existing_name_is_rejected()
    {
        var fake = new FakeMatterController();
        var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, new InMemoryMqttPublisher(), new MqttTopics("matterhorn")));
        fake.Emit(new NodeAdded(Light(5)));
        fake.Emit(new NodeAdded(Light(6)));
        AwaitAssert(() => Assert.Equal(2, gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result.Count));

        var result = gw.Ask<RenameResult>(new RenameRequest("bulb_5_1", "bulb_6_1", "tx1")).Result;

        Assert.False(result.Ok);
        Assert.Equal("name_taken", result.Error);
    }

    [Fact]
    public void Rename_with_an_empty_slug_is_rejected()
    {
        var fake = new FakeMatterController();
        var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, new InMemoryMqttPublisher(), new MqttTopics("matterhorn")));
        fake.Emit(new NodeAdded(Light(5)));
        AwaitAssert(() => Assert.Single(gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result));

        var result = gw.Ask<RenameResult>(new RenameRequest("bulb_5_1", "!!!", "tx1")).Result;

        Assert.False(result.Ok);
        Assert.Equal("invalid_name", result.Error);
    }

    [Fact]
    public void Stored_override_is_applied_when_the_device_joins()
    {
        var fake = new FakeMatterController();
        var store = new InMemoryNameStore();
        store.Names[(5UL, 1)] = "living_room_lamp";
        var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, new InMemoryMqttPublisher(), new MqttTopics("matterhorn"), store));

        fake.Emit(new NodeAdded(Light(5)));

        AwaitAssert(() => Assert.Equal("living_room_lamp",
            Assert.Single(gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result).FriendlyName));
    }

    [Fact]
    public void Set_after_rename_still_reaches_the_device()
    {
        var fake = new FakeMatterController();
        var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, new InMemoryMqttPublisher(), new MqttTopics("matterhorn"), new InMemoryNameStore()));
        fake.Emit(new NodeAdded(Light(5)));
        AwaitAssert(() => Assert.Single(gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result));
        Assert.True(gw.Ask<RenameResult>(new RenameRequest("bulb_5_1", "lamp", "tx1")).Result.Ok);

        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"state":"ON"}""")!;
        gw.Tell(new SetDevice("lamp", payload));

        AwaitAssert(() => Assert.Contains(fake.Invocations, i => i.NodeId == 5 && i.Cmd.CommandName == "On"));
    }

    [Fact]
    public void Rename_to_the_same_slug_is_a_no_op_success()
    {
        var fake = new FakeMatterController();
        var mqtt = new InMemoryMqttPublisher();
        var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, mqtt, new MqttTopics("matterhorn"), new InMemoryNameStore()));
        fake.Emit(new NodeAdded(Light(5)));
        AwaitAssert(() => Assert.Single(gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result));

        // "Bulb_5_1" slugs back to the existing "bulb_5_1" — nothing actually changes.
        var result = gw.Ask<RenameResult>(new RenameRequest("bulb_5_1", "Bulb_5_1", "tx1")).Result;

        Assert.True(result.Ok);
        Assert.Equal("bulb_5_1", gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result[0].FriendlyName);
        Assert.DoesNotContain(mqtt.Messages, m => m.Topic == "matterhorn/bridge/event" && m.Payload.Contains("device_renamed"));
    }

    [Fact]
    public void Attribute_routing_still_works_after_a_rename()
    {
        // Regression: attribute routing keys on (nodeId, endpoint) via _byKey, which a rename must
        // not disturb — an attribute after a rename must publish under the NEW name topic.
        var fake = new FakeMatterController();
        var mqtt = new InMemoryMqttPublisher();
        var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, mqtt, new MqttTopics("matterhorn"), new InMemoryNameStore()));
        fake.Emit(new NodeAdded(Light(5)));
        AwaitAssert(() => Assert.Single(gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result));
        Assert.True(gw.Ask<RenameResult>(new RenameRequest("bulb_5_1", "lamp", "tx1")).Result.Ok);

        fake.Emit(new AttributeChanged(new AttributeReading(5, 1, MatterClusters.OnOff, 0,
            JsonDocument.Parse("true").RootElement)));

        AwaitAssert(() => Assert.Contains(mqtt.Messages,
            m => m.Topic == "matterhorn/lamp" && m.Payload.Contains("\"state\":\"ON\"")));
    }

    [Fact]
    public void Rename_succeeds_even_when_persistence_fails()
    {
        // A read-only data dir (Save throws) must degrade to an in-memory rename, not break it.
        var fake = new FakeMatterController();
        var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, new InMemoryMqttPublisher(), new MqttTopics("matterhorn"), new ThrowingNameStore()));
        fake.Emit(new NodeAdded(Light(5)));
        AwaitAssert(() => Assert.Single(gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result));

        var result = gw.Ask<RenameResult>(new RenameRequest("bulb_5_1", "lamp", "tx1")).Result;

        Assert.True(result.Ok);
        Assert.Equal("lamp", gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result[0].FriendlyName);
    }

    [Fact]
    public void Removing_a_device_prunes_its_persisted_override()
    {
        var fake = new FakeMatterController();
        var store = new InMemoryNameStore();
        var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, new InMemoryMqttPublisher(), new MqttTopics("matterhorn"), store));
        fake.Emit(new NodeAdded(Light(5)));
        AwaitAssert(() => Assert.Single(gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result));
        Assert.True(gw.Ask<RenameResult>(new RenameRequest("bulb_5_1", "lamp", "tx1")).Result.Ok);
        Assert.True(store.Names.ContainsKey((5UL, 1)));

        fake.Emit(new NodeRemoved(5));

        AwaitAssert(() => Assert.Empty(gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result));
        Assert.False(store.Names.ContainsKey((5UL, 1))); // override pruned from the store
    }

    [Fact]
    public void RemoveRequest_for_a_known_device_replies_found_and_calls_RemoveNode()
    {
        var fake = new FakeMatterController();
        var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, new InMemoryMqttPublisher(), new MqttTopics("matterhorn")));
        fake.Emit(new NodeAdded(Light(5)));
        AwaitAssert(() => Assert.Single(gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result));

        var result = gw.Ask<RemoveAccepted>(new RemoveRequest("bulb_5_1", "tx1")).Result;

        Assert.True(result.Found);
        AwaitAssert(() => Assert.Contains(5UL, fake.Removed));
    }

    [Fact]
    public void RemoveRequest_for_an_unknown_device_replies_not_found_and_skips_RemoveNode()
    {
        var fake = new FakeMatterController();
        var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, new InMemoryMqttPublisher(), new MqttTopics("matterhorn")));

        var result = gw.Ask<RemoveAccepted>(new RemoveRequest("ghost", "tx1")).Result;

        Assert.False(result.Found);
        Assert.Empty(fake.Removed);
    }
}
