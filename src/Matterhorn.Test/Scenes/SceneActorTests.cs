using System.Text.Json;
using Akka.Actor;
using Akka.TestKit.Xunit2;
using Matterhorn.Bridge;
using Matterhorn.Scenes;

namespace Matterhorn.Test.Scenes;

public class SceneActorTests : TestKit
{
    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement.Clone();

    [Fact]
    public void RecallScene_fans_out_stored_values_via_RouteSet()
    {
        var gateway = CreateTestProbe();
        var values = new Dictionary<(ulong, ushort), IReadOnlyDictionary<string, JsonElement>>
        {
            [(5, 1)] = new Dictionary<string, JsonElement> { ["state"] = J("\"ON\""), ["brightness"] = J("40") },
            [(9, 1)] = new Dictionary<string, JsonElement> { ["state"] = J("\"OFF\"") },
        };
        var actor = Sys.ActorOf(SceneActor.Props("movie", values, gateway.Ref));

        actor.Tell(new RecallScene());

        var a = gateway.ExpectMsg<RouteSet>();
        var b = gateway.ExpectMsg<RouteSet>();
        var byKey = new[] { a, b }.ToDictionary(x => x.Key);
        Assert.Equal("ON", byKey[(5UL, (ushort)1)].Payload["state"].GetString());
        Assert.Equal(40, byKey[(5UL, (ushort)1)].Payload["brightness"].GetInt32());
        Assert.Equal("OFF", byKey[(9UL, (ushort)1)].Payload["state"].GetString());
    }
}
