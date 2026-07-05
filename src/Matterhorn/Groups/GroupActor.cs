using System.Text.Json;
using Akka.Actor;
using Matterhorn.Bridge;
using Matterhorn.Devices;
using Matterhorn.Mqtt;

namespace Matterhorn.Groups;

/// <summary>One actor per group. Fans a set out to its members via the gateway's RouteSet and
/// publishes a Z2M-style optimistic echo of the requested payload onto the retained group topic.</summary>
public sealed class GroupActor : ReceiveActor
{
    private string _name;
    private IReadOnlyList<(ulong NodeId, ushort Endpoint)> _members;
    private readonly IActorRef _gateway;
    private readonly IMqttPublisher _mqtt;
    private readonly MqttTopics _topics;
    private readonly Dictionary<string, JsonElement> _echo = new();

    public static Props Props(string name, IReadOnlyList<(ulong, ushort)> members,
        IActorRef gateway, IMqttPublisher mqtt, MqttTopics topics) =>
        Akka.Actor.Props.Create(() => new GroupActor(name, members, gateway, mqtt, topics));

    public GroupActor(string name, IReadOnlyList<(ulong, ushort)> members,
        IActorRef gateway, IMqttPublisher mqtt, MqttTopics topics)
    {
        _name = name; _members = members; _gateway = gateway; _mqtt = mqtt; _topics = topics;

        Receive<ApplyGroupSet>(OnSet);
        Receive<UpdateGroupMembers>(m => _members = m.Members);
        Receive<RenameGroupEntity>(r =>
        {
            _mqtt.PublishRetained(_topics.Device(_name), "");   // drop old retained group state
            _name = r.NewName;
        });
    }

    private void OnSet(ApplyGroupSet msg)
    {
        foreach (var key in _members) _gateway.Tell(new RouteSet(key, msg.Payload));
        foreach (var (k, v) in msg.Payload) _echo[k] = v;       // merge into optimistic echo
        var json = JsonSerializer.Serialize(_echo);
        _mqtt.PublishRetained(_topics.Device(_name), json);
        Context.System.EventStream.Publish(new DeviceStateChanged(_name, json));
    }
}
