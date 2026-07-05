using System.Text.Json;
using Akka.Actor;
using Akka.TestKit.Xunit2;
using Matterhorn.Bridge;
using Matterhorn.Devices;
using Matterhorn.Groups;
using Matterhorn.Mqtt;
using Matterhorn.Test.Mqtt;

namespace Matterhorn.Test.Groups;

public class GroupActorTests : TestKit
{
    private static Dictionary<string, JsonElement> Payload(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void ApplyGroupSet_fans_out_RouteSet_to_every_member()
    {
        var gateway = CreateTestProbe();
        var actor = Sys.ActorOf(GroupActor.Props("living_room",
            new (ulong, ushort)[] { (5, 1), (9, 1) }, gateway.Ref, new InMemoryMqttPublisher(), new MqttTopics("matterhorn")));

        actor.Tell(new ApplyGroupSet(Payload("""{"state":"OFF"}""")));

        var a = gateway.ExpectMsg<RouteSet>();
        var b = gateway.ExpectMsg<RouteSet>();
        Assert.Equal(new[] { (5UL, (ushort)1), (9UL, (ushort)1) }.ToHashSet(), new[] { a.Key, b.Key }.ToHashSet());
        Assert.Equal("OFF", a.Payload["state"].GetString());
    }

    [Fact]
    public void ApplyGroupSet_echoes_requested_payload_onto_retained_group_topic()
    {
        var mqtt = new InMemoryMqttPublisher();
        var actor = Sys.ActorOf(GroupActor.Props("living_room",
            new (ulong, ushort)[] { (5, 1) }, CreateTestProbe().Ref, mqtt, new MqttTopics("matterhorn")));

        actor.Tell(new ApplyGroupSet(Payload("""{"state":"ON","brightness":128}""")));

        AwaitAssert(() =>
        {
            var echo = Assert.Single(mqtt.Messages, m => m.Topic == "matterhorn/living_room" && m.Retained);
            Assert.Contains("\"state\":\"ON\"", echo.Payload);
            Assert.Contains("\"brightness\":128", echo.Payload);
        });
    }

    [Fact]
    public void ApplyGroupSet_publishes_a_state_event_for_the_group()
    {
        Sys.EventStream.Subscribe(TestActor, typeof(DeviceStateChanged));
        var actor = Sys.ActorOf(GroupActor.Props("living_room",
            new (ulong, ushort)[] { (5, 1) }, CreateTestProbe().Ref, new InMemoryMqttPublisher(), new MqttTopics("matterhorn")));

        actor.Tell(new ApplyGroupSet(Payload("""{"state":"ON"}""")));

        ExpectMsg<DeviceStateChanged>(e => e.FriendlyName == "living_room" && e.StateJson.Contains("\"state\":\"ON\""));
    }
}
