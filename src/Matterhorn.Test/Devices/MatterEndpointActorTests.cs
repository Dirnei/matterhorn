using System.Text.Json;
using Akka.Actor;
using Akka.TestKit.Xunit2;
using Matterhorn.Devices;
using Matterhorn.Matter;
using Matterhorn.Mqtt;
using Matterhorn.Test.Mqtt;

namespace Matterhorn.Test.Devices;

public class MatterEndpointActorTests : TestKit
{
    private readonly MqttTopics _topics = new("matterhorn");

    [Fact]
    public void Publishes_retained_state_on_attribute_change()
    {
        var mqtt = new InMemoryMqttPublisher();
        var actor = Sys.ActorOf(MatterEndpointActor.Props("lamp", 1, 1, new FakeMatterController(), mqtt, _topics));

        actor.Tell(new ApplyAttribute(new AttributeReading(1, 1, MatterClusters.OnOff, 0,
            JsonDocument.Parse("true").RootElement)));

        AwaitAssert(() =>
        {
            var msg = Assert.Single(mqtt.Messages, m => m.Topic == "matterhorn/lamp");
            Assert.True(msg.Retained);
            Assert.Contains("\"state\":\"ON\"", msg.Payload);
        });
    }

    [Fact]
    public void Merges_multiple_attributes_into_one_state()
    {
        var mqtt = new InMemoryMqttPublisher();
        var actor = Sys.ActorOf(MatterEndpointActor.Props("lamp", 1, 1, new FakeMatterController(), mqtt, _topics));

        actor.Tell(new ApplyAttribute(new AttributeReading(1, 1, MatterClusters.OnOff, 0, JsonDocument.Parse("true").RootElement)));
        actor.Tell(new ApplyAttribute(new AttributeReading(1, 1, MatterClusters.LevelControl, 0, JsonDocument.Parse("128").RootElement)));

        AwaitAssert(() =>
        {
            var last = mqtt.Messages.Where(m => m.Topic == "matterhorn/lamp").Last();
            Assert.Contains("\"state\":\"ON\"", last.Payload);
            Assert.Contains("\"brightness\":128", last.Payload);
        });
    }

    [Fact]
    public void Set_invokes_controller_commands()
    {
        var fake = new FakeMatterController();
        var actor = Sys.ActorOf(MatterEndpointActor.Props("lamp", 7, 1, fake, new InMemoryMqttPublisher(), _topics));

        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"state":"ON","brightness":200}""")!;
        actor.Tell(new ApplySet(payload));

        AwaitAssert(() =>
        {
            Assert.Equal(2, fake.Invocations.Count);
            Assert.Equal(7ul, fake.Invocations[0].NodeId);
            Assert.Equal("On", fake.Invocations[0].Cmd.CommandName);
        });
    }

    [Fact]
    public void Rename_clears_old_retained_topics_and_republishes_under_new_name()
    {
        var mqtt = new InMemoryMqttPublisher();
        var actor = Sys.ActorOf(MatterEndpointActor.Props("lamp", 5, 1,
            new FakeMatterController(), mqtt, _topics));

        // Seed some state so there is a retained payload to migrate.
        actor.Tell(new ApplyAttribute(new AttributeReading(5, 1, MatterClusters.OnOff, 0,
            JsonDocument.Parse("true").RootElement)));
        AwaitAssert(() => Assert.Contains(mqtt.Messages, m => m.Topic == "matterhorn/lamp"));

        actor.Tell(new Rename("desk_bulb"));

        AwaitAssert(() =>
        {
            // Old retained state + availability cleared with an empty retained payload.
            Assert.Contains(mqtt.Messages, m => m.Topic == "matterhorn/lamp" && m.Retained && m.Payload == "");
            Assert.Contains(mqtt.Messages, m => m.Topic == "matterhorn/lamp/availability" && m.Retained && m.Payload == "");
            // State republished under the new name.
            Assert.Contains(mqtt.Messages, m => m.Topic == "matterhorn/desk_bulb" && m.Payload.Contains("\"state\":\"ON\""));
            Assert.Contains(mqtt.Messages, m => m.Topic == "matterhorn/desk_bulb/availability" && m.Payload == "online");
        });
    }
}
