using System.Text.Json;
using Akka.Actor;
using Akka.TestKit.Xunit2;
using Matterhorn.Bridge;
using Matterhorn.Devices;
using Matterhorn.Mqtt;
using Matterhorn.Persistence;
using Matterhorn.Scenes;
using Matterhorn.Test.Mqtt;

namespace Matterhorn.Test.Scenes;

public class ScenesSupervisorTests : TestKit
{
    private sealed class MemSceneStore : ISceneStore
    {
        public Dictionary<string, IReadOnlyDictionary<(ulong, ushort), IReadOnlyDictionary<string, JsonElement>>> Scenes { get; } = new();
        public IReadOnlyDictionary<string, IReadOnlyDictionary<(ulong, ushort), IReadOnlyDictionary<string, JsonElement>>> Load() => Scenes;
        public void Save(IReadOnlyDictionary<string, IReadOnlyDictionary<(ulong, ushort), IReadOnlyDictionary<string, JsonElement>>> s)
        { Scenes.Clear(); foreach (var (k, v) in s) Scenes[k] = v; }
    }

    // A gateway stand-in that answers RouteGetState with a canned snapshot (settable + a read-only prop).
    private sealed class FakeGateway : ReceiveActor
    {
        public FakeGateway()
        {
            Receive<RouteGetState>(r => Sender.Tell(new DeviceStateSnapshot(true, new Dictionary<string, object?>
            {
                ["state"] = "ON", ["brightness"] = 40, ["temperature"] = 21.5,   // temperature must be filtered out
            })));
        }
    }

    private (IActorRef sup, IActorRef gw, InMemoryMqttPublisher mqtt, MemSceneStore store) NewSupervisor()
    {
        var gw = Sys.ActorOf(Props.Create(() => new FakeGateway()));
        var mqtt = new InMemoryMqttPublisher();
        var store = new MemSceneStore();
        var sup = Sys.ActorOf(ScenesSupervisor.Props(store, gw, mqtt, new MqttTopics("matterhorn")));
        sup.Tell(new GetScenes());
        ExpectMsg<IReadOnlyList<SceneView>>();
        Sys.EventStream.Publish(new DeviceRegistered((5UL, 1), "lamp"));
        AwaitAssert(() =>
        {
            sup.Tell(new GetScenes());
            var views = ExpectMsg<IReadOnlyList<SceneView>>();
            _ = views; // registration just needs to have been absorbed before proceeding
        });
        return (sup, gw, mqtt, store);
    }

    [Fact]
    public void StoreScene_snapshot_keeps_only_settable_properties()
    {
        var (sup, _, _, store) = NewSupervisor();
        var r = sup.Ask<SceneOpResult>(new StoreScene("movie", new[] { "lamp" }, null, "t1")).Result;

        Assert.True(r.Ok);
        var props = store.Scenes["movie"][(5, 1)];
        Assert.Equal("ON", props["state"].GetString());
        Assert.Equal(40, props["brightness"].GetInt32());
        Assert.False(props.ContainsKey("temperature"));   // read-only filtered out
    }

    [Fact]
    public void StoreScene_with_explicit_state_stores_it_without_snapshot()
    {
        var (sup, _, _, store) = NewSupervisor();
        var explicitState = new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>
        {
            ["lamp"] = new Dictionary<string, JsonElement> { ["state"] = JsonDocument.Parse("\"OFF\"").RootElement.Clone() },
        };
        sup.Ask<SceneOpResult>(new StoreScene("night", new[] { "lamp" }, explicitState, "t1")).Wait();

        Assert.Equal("OFF", store.Scenes["night"][(5, 1)]["state"].GetString());
    }

    [Fact]
    public void RecallSceneByName_recalls_the_entity()
    {
        var (sup, _, _, _) = NewSupervisor();
        sup.Ask<SceneOpResult>(new StoreScene("movie", new[] { "lamp" }, null, "t1")).Wait();
        var r = sup.Ask<SceneOpResult>(new RecallSceneByName("movie", "t2")).Result;
        Assert.True(r.Ok);
    }

    [Fact]
    public void RecallSceneByName_unknown_scene_is_not_found()
    {
        var (sup, _, _, _) = NewSupervisor();
        var r = sup.Ask<SceneOpResult>(new RecallSceneByName("ghost", "t1")).Result;
        Assert.False(r.Ok);
        Assert.Equal("not_found", r.Error);
    }

    [Fact]
    public void DeviceRemoved_prunes_the_member_from_every_scene()
    {
        var (sup, _, _, store) = NewSupervisor();
        sup.Ask<SceneOpResult>(new StoreScene("movie", new[] { "lamp" }, null, "t1")).Wait();

        Sys.EventStream.Publish(new DeviceRemoved((5UL, 1)));

        AwaitAssert(() => Assert.False(store.Scenes["movie"].ContainsKey((5, 1))));
    }
}
