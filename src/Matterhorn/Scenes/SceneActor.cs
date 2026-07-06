using System.Text.Json;
using Akka.Actor;
using Matterhorn.Bridge;

namespace Matterhorn.Scenes;

/// <summary>One actor per scene. Recall fans the stored per-device values out via the gateway's RouteSet.</summary>
public sealed class SceneActor : ReceiveActor
{
    private IReadOnlyDictionary<(ulong, ushort), IReadOnlyDictionary<string, JsonElement>> _values;
    private readonly IActorRef _gateway;

    public static Props Props(string name,
        IReadOnlyDictionary<(ulong, ushort), IReadOnlyDictionary<string, JsonElement>> values, IActorRef gateway) =>
        Akka.Actor.Props.Create(() => new SceneActor(name, values, gateway));

    public SceneActor(string name,
        IReadOnlyDictionary<(ulong, ushort), IReadOnlyDictionary<string, JsonElement>> values, IActorRef gateway)
    {
        _values = values; _gateway = gateway;
        Receive<RecallScene>(_ =>
        {
            foreach (var (key, payload) in _values) _gateway.Tell(new RouteSet(key, payload));
        });
        Receive<UpdateSceneValues>(u => _values = u.Values);
    }
}
