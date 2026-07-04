using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using Matterhorn.Matter;
using Matterhorn.Mqtt;

namespace Matterhorn.Devices;

/// <summary>
/// One actor per logical device (node endpoint). Holds retained state, maps attribute
/// changes to Z2M properties, and translates <c>/set</c> into controller commands (spec §9).
/// </summary>
public sealed class MatterEndpointActor : ReceiveActor
{
    private readonly ILoggingAdapter _log = Context.GetLogger();
    private readonly string _name;
    private readonly ulong _nodeId;
    private readonly ushort _endpoint;
    private readonly IMatterController _controller;
    private readonly IMqttPublisher _mqtt;
    private readonly MqttTopics _topics;
    private readonly Dictionary<string, object?> _state = new();

    public static Props Props(string friendlyName, ulong nodeId, ushort endpoint,
        IMatterController controller, IMqttPublisher mqtt, MqttTopics topics) =>
        Akka.Actor.Props.Create(() => new MatterEndpointActor(friendlyName, nodeId, endpoint, controller, mqtt, topics));

    public MatterEndpointActor(string friendlyName, ulong nodeId, ushort endpoint,
        IMatterController controller, IMqttPublisher mqtt, MqttTopics topics)
    {
        _name = friendlyName; _nodeId = nodeId; _endpoint = endpoint;
        _controller = controller; _mqtt = mqtt; _topics = topics;

        Receive<ApplyAttribute>(OnAttribute);
        ReceiveAsync<ApplySet>(OnSet);
        Receive<SetReachable>(r => _mqtt.Publish(_topics.Availability(_name), r.Reachable ? "online" : "offline"));
        Receive<Republish>(_ =>
        {
            if (_state.Count > 0)
                _mqtt.PublishRetained(_topics.Device(_name), JsonSerializer.Serialize(_state));
            _mqtt.Publish(_topics.Availability(_name), "online");
        });
        Receive<GetState>(_ => Sender.Tell(new DeviceStateSnapshot(true, new Dictionary<string, object?>(_state))));
    }

    private void OnAttribute(ApplyAttribute msg)
    {
        foreach (var (k, v) in PropertyMapping.Map(new[] { msg.Reading }))
            _state[k] = v;
        var json = JsonSerializer.Serialize(_state);
        _mqtt.PublishRetained(_topics.Device(_name), json);
        Context.System.EventStream.Publish(new DeviceStateChanged(_name, json));
    }

    private async Task OnSet(ApplySet msg)
    {
        foreach (var cmd in CommandMapping.Map(msg.Payload))
        {
            try { await _controller.InvokeCommand(_nodeId, _endpoint, cmd, CancellationToken.None); }
            catch (Exception ex) { _log.Error(ex, "InvokeCommand failed for {Name}", _name); }
        }
    }
}
