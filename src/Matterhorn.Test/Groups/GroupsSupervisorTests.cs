using System.Text.Json;
using Akka.Actor;
using Akka.TestKit;
using Akka.TestKit.Xunit2;
using Matterhorn.Bridge;
using Matterhorn.Groups;
using Matterhorn.Mqtt;
using Matterhorn.Persistence;
using Matterhorn.Test.Mqtt;

namespace Matterhorn.Test.Groups;

public class GroupsSupervisorTests : TestKit
{
    private sealed class MemGroupStore : IGroupStore
    {
        public Dictionary<string, IReadOnlyList<(ulong, ushort)>> Groups { get; } = new();
        public IReadOnlyDictionary<string, IReadOnlyList<(ulong, ushort)>> Load() => Groups;
        public void Save(IReadOnlyDictionary<string, IReadOnlyList<(ulong, ushort)>> g)
        { Groups.Clear(); foreach (var (k, v) in g) Groups[k] = v; }
    }

    // A supervisor pre-seeded with two known devices (via DeviceRegistered) it can resolve by name.
    private (IActorRef sup, TestProbe gateway, InMemoryMqttPublisher mqtt, MemGroupStore store) NewSupervisor()
    {
        var gateway = CreateTestProbe();
        var mqtt = new InMemoryMqttPublisher();
        var store = new MemGroupStore();
        var sup = Sys.ActorOf(GroupsSupervisor.Props(store, gateway.Ref, mqtt, new MqttTopics("matterhorn")));
        // Wait for the actor to finish starting (PreStart subscribes to the EventStream) before publishing —
        // otherwise DeviceRegistered can be published before the subscription exists and is silently dropped
        // (EventStream does not queue for late subscribers).
        AwaitAssert(() => { sup.Tell(new GetGroups()); ExpectMsg<IReadOnlyList<GroupView>>(); });
        Sys.EventStream.Publish(new DeviceRegistered((5UL, 1), "lamp"));
        Sys.EventStream.Publish(new DeviceRegistered((9UL, 1), "strip"));
        // let the read-model settle
        AwaitAssert(() => { sup.Tell(new GetGroups()); ExpectMsg<IReadOnlyList<GroupView>>(); });
        return (sup, gateway, mqtt, store);
    }

    [Fact]
    public void CreateGroup_persists_members_and_publishes_bridge_groups()
    {
        var (sup, _, mqtt, store) = NewSupervisor();
        var r = sup.Ask<GroupOpResult>(new CreateGroup("living_room", new[] { "lamp", "strip" }, "t1")).Result;

        Assert.True(r.Ok);
        Assert.Equal(new (ulong, ushort)[] { (5, 1), (9, 1) }, store.Groups["living_room"]);
        AwaitAssert(() => Assert.Contains(mqtt.Messages,
            m => m.Topic == "matterhorn/bridge/groups" && m.Payload.Contains("living_room") && m.Payload.Contains("lamp")));
    }

    [Fact]
    public void CreateGroup_rejects_a_name_that_is_a_device()
    {
        var (sup, _, _, _) = NewSupervisor();
        var r = sup.Ask<GroupOpResult>(new CreateGroup("lamp", Array.Empty<string>(), "t1")).Result;
        Assert.False(r.Ok);
        Assert.Equal("collides_with_device", r.Error);
    }

    [Fact]
    public void GroupSet_routes_ApplyGroupSet_fanout_via_gateway()
    {
        var (sup, gateway, _, _) = NewSupervisor();
        sup.Ask<GroupOpResult>(new CreateGroup("living_room", new[] { "lamp", "strip" }, "t1")).Wait();

        sup.Tell(new GroupSet("living_room",
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"state":"OFF"}""")!));

        gateway.ExpectMsg<RouteSet>(m => m.Key == (5UL, (ushort)1));
        gateway.ExpectMsg<RouteSet>(m => m.Key == (9UL, (ushort)1));
    }

    [Fact]
    public void DeviceRemoved_prunes_the_member_from_every_group()
    {
        var (sup, _, _, store) = NewSupervisor();
        sup.Ask<GroupOpResult>(new CreateGroup("living_room", new[] { "lamp", "strip" }, "t1")).Wait();

        Sys.EventStream.Publish(new DeviceRemoved((5UL, 1)));

        AwaitAssert(() => Assert.Equal(new (ulong, ushort)[] { (9, 1) }, store.Groups["living_room"]));
    }

    [Fact]
    public void RenameGroup_to_an_existing_group_is_rejected()
    {
        var (sup, _, _, _) = NewSupervisor();
        sup.Ask<GroupOpResult>(new CreateGroup("a", Array.Empty<string>(), "t1")).Wait();
        sup.Ask<GroupOpResult>(new CreateGroup("b", Array.Empty<string>(), "t2")).Wait();

        var r = sup.Ask<GroupOpResult>(new RenameGroup("a", "b", "t3")).Result;
        Assert.False(r.Ok);
        Assert.Equal("name_taken", r.Error);
    }

    [Fact]
    public void CreateGroup_publishes_GroupNamesChanged()
    {
        Sys.EventStream.Subscribe(TestActor, typeof(GroupNamesChanged));
        var (sup, _, _, _) = NewSupervisor();
        sup.Ask<GroupOpResult>(new CreateGroup("living_room", Array.Empty<string>(), "t1")).Wait();
        AwaitAssert(() =>
        {
            var seen = false;
            while (TryReceiveOne(out var m, TimeSpan.Zero))
                if (m.Message is GroupNamesChanged g && g.Names.Contains("living_room")) seen = true;
            Assert.True(seen);
        });
    }
}
