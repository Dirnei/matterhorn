using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using Akka.Streams;
using Matterhorn.Configuration;
using Matterhorn.Devices;
using Matterhorn.Matter;
using Matterhorn.Mqtt;
using Matterhorn.Persistence;

namespace Matterhorn.Bridge;

/// <summary>
/// Owns the controller connection, device lifecycle, and the <c>bridge/*</c> control-plane.
/// Creates one <see cref="MatterEndpointActor"/> per logical device and answers
/// REST queries against the live model.
/// </summary>
public sealed class MatterGatewayActor : ReceiveActor
{
    private readonly ILoggingAdapter _log = Context.GetLogger();
    private readonly IMatterController _controller;
    private readonly IMqttPublisher _mqtt;
    private readonly MqttTopics _topics;
    private readonly INameStore _names;
    private readonly Dictionary<(ulong, ushort), string> _overrides;

    private sealed record Registered(string FriendlyName, EndpointInfo Info, IActorRef Actor, DeviceDescriptor Descriptor);
    // Self-addressed results of the off-actor controller calls, so the actor never blocks on them.
    private sealed record CommissionDone(string Transaction, ulong? NodeId, string? Error);
    private sealed record RemoveDone(string Transaction, string? Error);
    private readonly Dictionary<(ulong, ushort), Registered> _byKey = new();
    private readonly Dictionary<string, Registered> _byName = new();
    private ISourceQueueWithComplete<MatterEvent>? _queue;

    public static Props Props(IMatterController controller, IMqttPublisher mqtt, MqttTopics topics, INameStore? names = null) =>
        Akka.Actor.Props.Create(() => new MatterGatewayActor(controller, mqtt, topics, names));

    public MatterGatewayActor(IMatterController controller, IMqttPublisher mqtt, MqttTopics topics, INameStore? names = null)
    {
        _controller = controller; _mqtt = mqtt; _topics = topics;
        _names = names ?? NullNameStore.Instance;
        _overrides = new Dictionary<(ulong, ushort), string>(_names.Load());

        Receive<NodeAdded>(OnNodeAdded);
        Receive<NodeRemoved>(OnNodeRemoved);
        Receive<AttributeChanged>(ac =>
        {
            if (_byKey.TryGetValue((ac.Reading.NodeId, ac.Reading.Endpoint), out var reg))
                reg.Actor.Tell(new ApplyAttribute(ac.Reading));
        });
        Receive<ReachabilityChanged>(OnReachabilityChanged);
        Receive<SetDevice>(s =>
        {
            if (_byName.TryGetValue(s.FriendlyName, out var reg)) reg.Actor.Tell(new ApplySet(s.Payload));
        });
        Receive<MqttConnected>(_ => AnnounceAll());
        Receive<GetDevices>(_ => Sender.Tell((IReadOnlyList<DeviceDescriptor>)_byName.Values.Select(r => r.Descriptor).ToList()));
        Receive<GetDeviceState>(g =>
        {
            if (_byName.TryGetValue(g.FriendlyName, out var reg)) reg.Actor.Forward(new GetState());
            else Sender.Tell(new DeviceStateSnapshot(false, null));
        });
        Receive<CommissionRequest>(OnCommission);
        Receive<CommissionDone>(OnCommissionDone);
        Receive<RemoveRequest>(OnRemove);
        Receive<RemoveDone>(OnRemoveDone);
        Receive<RenameRequest>(OnRename);
    }

    protected override void PreStart()
    {
        // bridge/state online + retained state are (re)published on MqttConnected, so they land
        // reliably once the broker is actually connected.
        var (queue, _) = IngestionPipeline.Run(Context.System, Self);
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
        var name = _overrides.TryGetValue((info.NodeId, info.Endpoint), out var custom)
            ? custom
            : FriendlyName.Default(info.ProductName, info.NodeId, info.Endpoint);
        if (_byName.ContainsKey(name)) return;

        var actor = Context.ActorOf(MatterEndpointActor.Props(name, info.NodeId, info.Endpoint, _controller, _mqtt, _topics),
            $"ep-{info.NodeId}-{info.Endpoint}");
        var descriptor = new DeviceDescriptor(name, info.NodeId.ToString(), info.Endpoint,
            info.VendorName, info.ProductName, info.VendorId, info.ProductId, info.DeviceType, info.Reachable,
            ExposesBuilder.Build(info.ClusterIds, info.ColorFeatures), info.Transport);
        var reg = new Registered(name, info, actor, descriptor);
        _byKey[(info.NodeId, info.Endpoint)] = reg;
        _byName[name] = reg;
        PublishDevices();
        PublishEvent("device_joined", new { friendly_name = name });
    }

    private void OnNodeRemoved(NodeRemoved msg)
    {
        // node_removed carries only the node id, so drop every endpoint registered under that node.
        var keys = _byKey.Keys.Where(k => k.Item1 == msg.NodeId).ToList();
        if (keys.Count == 0) return;
        var prunedOverride = false;
        foreach (var key in keys)
        {
            if (!_byKey.Remove(key, out var reg)) continue;
            _byName.Remove(reg.FriendlyName);
            Context.Stop(reg.Actor);
            // Drop any persisted name override for the departed endpoint so names.json doesn't grow forever.
            prunedOverride |= _overrides.Remove(key);
            PublishEvent("device_leave", new { friendly_name = reg.FriendlyName });
        }
        if (prunedOverride) SaveOverrides();
        PublishDevices();
    }

    // Commissioning can take 30-60s. Run it off the actor and pipe the outcome back as CommissionDone
    // so the gateway keeps serving device queries and attribute routing while it's in flight.
    private void OnCommission(CommissionRequest req) =>
        _controller.Commission(req.Code, CancellationToken.None).ContinueWith(t => t.IsFaulted
            ? new CommissionDone(req.Transaction, null, t.Exception!.GetBaseException().Message)
            : new CommissionDone(req.Transaction, t.Result, null)).PipeTo(Self);

    private void OnCommissionDone(CommissionDone done) =>
        _mqtt.Publish(_topics.Base + "/bridge/response/commission", JsonSerializer.Serialize(new
        {
            transaction = done.Transaction,
            status = done.Error is null ? "ok" : "error",
            node_id = done.NodeId?.ToString(),
            error = done.Error,
        }));

    private void OnRemove(RemoveRequest req)
    {
        var found = _byName.TryGetValue(req.FriendlyName, out var reg);
        Sender.Tell(new RemoveAccepted(found)); // REST Ask path; harmless when Told with NoSender (MQTT).
        if (found)
            _controller.RemoveNode(reg!.Info.NodeId, CancellationToken.None).ContinueWith(t =>
                new RemoveDone(req.Transaction, t.IsFaulted ? t.Exception!.GetBaseException().Message : null)).PipeTo(Self);
        else
            Self.Tell(new RemoveDone(req.Transaction, null));
    }

    private void OnRemoveDone(RemoveDone done) =>
        _mqtt.Publish(_topics.Base + "/bridge/response/remove", JsonSerializer.Serialize(new
        {
            transaction = done.Transaction,
            status = done.Error is null ? "ok" : "error",
            error = done.Error,
        }));

    private void OnRename(RenameRequest req)
    {
        var result = DoRename(req.FromName, req.ToName);
        Sender.Tell(result); // REST Ask path; harmless when Told with NoSender (MQTT).
        _mqtt.Publish(_topics.Base + "/bridge/response/rename", JsonSerializer.Serialize(new
        {
            transaction = req.Transaction,
            status = result.Ok ? "ok" : "error",
            from = req.FromName,
            to = result.NewName,
            error = result.Error,
        }));
    }

    private RenameResult DoRename(string from, string to)
    {
        var slug = FriendlyName.Slug(to);
        if (slug.Length == 0) return new RenameResult(false, "invalid_name");
        if (!_byName.TryGetValue(from, out var reg)) return new RenameResult(false, "not_found");
        if (slug == from) return new RenameResult(true, null, slug); // no-op
        if (_byName.ContainsKey(slug)) return new RenameResult(false, "name_taken");

        var updated = reg with { FriendlyName = slug, Descriptor = reg.Descriptor with { FriendlyName = slug } };
        _byName.Remove(from);
        _byName[slug] = updated;
        _byKey[(reg.Info.NodeId, reg.Info.Endpoint)] = updated;
        reg.Actor.Tell(new Rename(slug));

        _overrides[(reg.Info.NodeId, reg.Info.Endpoint)] = slug;
        SaveOverrides();

        PublishDevices();
        PublishEvent("device_renamed", new { from, to = slug });
        return new RenameResult(true, null, slug);
    }

    // Persist the override map; a write failure is logged, never fatal (a read-only data dir
    // degrades to in-memory-only renames rather than breaking the operation).
    private void SaveOverrides()
    {
        try { _names.Save(_overrides); }
        catch (Exception ex) { _log.Warning("Failed to persist name overrides: {Error}", ex.Message); }
    }

    private void OnReachabilityChanged(ReachabilityChanged r)
    {
        if (!_byKey.TryGetValue((r.NodeId, r.Endpoint), out var reg)) return;
        reg.Actor.Tell(new SetReachable(r.Reachable));
        if (reg.Descriptor.Reachable == r.Reachable) return;
        // Reflect it in the discovery model so REST/dashboard and bridge/devices show the live state.
        var updated = reg with { Descriptor = reg.Descriptor with { Reachable = r.Reachable } };
        _byKey[(r.NodeId, r.Endpoint)] = updated;
        _byName[reg.FriendlyName] = updated;
        PublishDevices();
    }

    private void AnnounceAll()
    {
        _mqtt.PublishRetained(_topics.BridgeState(), """{"state":"online"}""");
        PublishDevices();
        foreach (var reg in _byName.Values) reg.Actor.Tell(new Republish());
    }

    private void PublishDevices()
    {
        _mqtt.PublishRetained(_topics.BridgeDevices(),
            JsonSerializer.Serialize(_byName.Values.Select(r => r.Descriptor), JsonDefaults.SnakeCase));
        Context.System.EventStream.Publish(new DeviceListChanged());
    }

    private void PublishEvent(string type, object data) =>
        _mqtt.Publish(_topics.BridgeEvent(), JsonSerializer.Serialize(new { type, data }));
}
