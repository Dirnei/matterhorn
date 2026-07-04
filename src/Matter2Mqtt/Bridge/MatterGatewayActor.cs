using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using Akka.Streams;
using Matter2Mqtt.Devices;
using Matter2Mqtt.Matter;
using Matter2Mqtt.Mqtt;

namespace Matter2Mqtt.Bridge;

/// <summary>
/// Owns the controller connection, device lifecycle, and the <c>bridge/*</c> control-plane
/// (spec §9). Creates one <see cref="MatterEndpointActor"/> per logical device and answers
/// REST queries against the live model.
/// </summary>
public sealed class MatterGatewayActor : ReceiveActor
{
    private readonly ILoggingAdapter _log = Context.GetLogger();
    private readonly IMatterController _controller;
    private readonly IMqttPublisher _mqtt;
    private readonly MqttTopics _topics;

    private sealed record Registered(string FriendlyName, EndpointInfo Info, IActorRef Actor, DeviceDescriptor Descriptor);
    private readonly Dictionary<(ulong, ushort), Registered> _byKey = new();
    private readonly Dictionary<string, Registered> _byName = new();
    private ISourceQueueWithComplete<MatterEvent>? _queue;

    public static Props Props(IMatterController controller, IMqttPublisher mqtt, MqttTopics topics) =>
        Akka.Actor.Props.Create(() => new MatterGatewayActor(controller, mqtt, topics));

    public MatterGatewayActor(IMatterController controller, IMqttPublisher mqtt, MqttTopics topics)
    {
        _controller = controller; _mqtt = mqtt; _topics = topics;

        Receive<NodeAdded>(OnNodeAdded);
        Receive<NodeRemoved>(OnNodeRemoved);
        Receive<ReachabilityChanged>(r =>
        {
            if (_byKey.TryGetValue((r.NodeId, r.Endpoint), out var reg))
                reg.Actor.Tell(new SetReachable(r.Reachable));
        });
        Receive<SetDevice>(s =>
        {
            if (_byName.TryGetValue(s.FriendlyName, out var reg)) reg.Actor.Tell(new ApplySet(s.Payload));
        });
        Receive<GetDevices>(_ => Sender.Tell((IReadOnlyList<DeviceDescriptor>)_byName.Values.Select(r => r.Descriptor).ToList()));
        Receive<GetDeviceState>(g => Sender.Tell(new DeviceStateReply(_byName.ContainsKey(g.FriendlyName), null)));
        ReceiveAsync<CommissionRequest>(OnCommission);
        ReceiveAsync<RemoveRequest>(OnRemove);
    }

    protected override void PreStart()
    {
        _mqtt.PublishRetained(_topics.BridgeState(), """{"state":"online"}""");
        var (queue, _) = IngestionPipeline.Run(Context.System,
            (n, e) => _byKey.TryGetValue((n, e), out var r) ? r.Actor : null, Self);
        _queue = queue;
        // Pump the controller's event stream into the pipeline queue.
        _ = Task.Run(async () =>
        {
            await foreach (var evt in _controller.ConnectAndListen(CancellationToken.None))
                await queue.OfferAsync(evt);
        });
    }

    private void OnNodeAdded(NodeAdded msg)
    {
        var info = msg.Endpoint;
        var name = FriendlyName.Default(info.ProductName, info.NodeId, info.Endpoint);
        if (_byName.ContainsKey(name)) return;

        var actor = Context.ActorOf(MatterEndpointActor.Props(name, info.NodeId, info.Endpoint, _controller, _mqtt, _topics),
            $"ep-{info.NodeId}-{info.Endpoint}");
        var descriptor = new DeviceDescriptor(name, info.NodeId.ToString(), info.Endpoint,
            info.VendorName, info.ProductName, info.VendorId, info.ProductId, info.DeviceType, info.Reachable,
            ExposesBuilder.Build(info.ClusterIds));
        var reg = new Registered(name, info, actor, descriptor);
        _byKey[(info.NodeId, info.Endpoint)] = reg;
        _byName[name] = reg;
        PublishDevices();
        PublishEvent("device_joined", new { friendly_name = name });
    }

    private void OnNodeRemoved(NodeRemoved msg)
    {
        if (!_byKey.Remove((msg.NodeId, msg.Endpoint), out var reg)) return;
        _byName.Remove(reg.FriendlyName);
        Context.Stop(reg.Actor);
        PublishDevices();
        PublishEvent("device_leave", new { friendly_name = reg.FriendlyName });
    }

    private async Task OnCommission(CommissionRequest req)
    {
        try
        {
            var nodeId = await _controller.Commission(req.Code, CancellationToken.None);
            await _mqtt.Publish(_topics.Base + "/bridge/response/commission",
                JsonSerializer.Serialize(new { transaction = req.Transaction, status = "ok", node_id = nodeId.ToString(), error = (string?)null }));
        }
        catch (Exception ex)
        {
            await _mqtt.Publish(_topics.Base + "/bridge/response/commission",
                JsonSerializer.Serialize(new { transaction = req.Transaction, status = "error", node_id = (string?)null, error = ex.Message }));
        }
    }

    private async Task OnRemove(RemoveRequest req)
    {
        if (_byName.TryGetValue(req.FriendlyName, out var reg))
            await _controller.RemoveNode(reg.Info.NodeId, CancellationToken.None);
        await _mqtt.Publish(_topics.Base + "/bridge/response/remove",
            JsonSerializer.Serialize(new { transaction = req.Transaction, status = "ok" }));
    }

    private void PublishDevices() =>
        _mqtt.PublishRetained(_topics.BridgeDevices(), JsonSerializer.Serialize(_byName.Values.Select(r => r.Descriptor)));

    private void PublishEvent(string type, object data) =>
        _mqtt.Publish(_topics.BridgeEvent(), JsonSerializer.Serialize(new { type, data }));
}
