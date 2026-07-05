using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using Matterhorn.Matter;
using Matterhorn.Mqtt;

namespace Matterhorn.Devices;

/// <summary>
/// One actor per logical device (node endpoint). Holds retained state, maps attribute
/// changes to Z2M properties, and translates <c>/set</c> into controller commands.
/// </summary>
public sealed class MatterEndpointActor : ReceiveActor
{
    private readonly ILoggingAdapter _log = Context.GetLogger();
    private string _name;
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
        Receive<Rename>(msg =>
        {
            // Drop the old retained state + availability so the broker stops serving the old name.
            _mqtt.PublishRetained(_topics.Device(_name), "");
            _mqtt.PublishRetained(_topics.Availability(_name), "");
            _name = msg.NewName;
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
        SyncNestedColor();
        var json = JsonSerializer.Serialize(_state);
        _mqtt.PublishRetained(_topics.Device(_name), json);
        Context.System.EventStream.Publish(new DeviceStateChanged(_name, json));
    }

    // HA's json-schema light reads color nested ({"color":{"h":0-360,"s":0-100}}); keep that
    // object in sync with the flat Matter-range hue/saturation properties.
    private void SyncNestedColor()
    {
        var hasHue = _state.TryGetValue("hue", out var hue) && hue is int;
        var hasSat = _state.TryGetValue("saturation", out var sat) && sat is int;
        if (!hasHue && !hasSat) return;
        _state["color"] = new Dictionary<string, object?>
        {
            ["h"] = hasHue ? (int)Math.Round((int)hue! * 360.0 / 254) : 0,
            ["s"] = hasSat ? (int)Math.Round((int)sat! * 100.0 / 254) : 0,
        };
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
