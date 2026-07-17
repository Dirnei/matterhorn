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
    private readonly IThreadDatasetSource _thread;
    private readonly Dictionary<(ulong, ushort), string> _overrides;
    // Captured on the actor thread at construction: Akka's Context is thread-static and throws when
    // touched from the off-actor tasks below, whereas publishing to the EventStream is thread-safe.
    private readonly Akka.Event.EventStream _events;
    private ControllerInfo? _controllerInfo;

    private sealed record Registered(string FriendlyName, EndpointInfo Info, IActorRef Actor, DeviceDescriptor Descriptor);
    // Self-addressed results of the off-actor controller calls, so the actor never blocks on them.
    private sealed record CommissionDone(string Transaction, ulong? NodeId, string? Error);
    private sealed record RemoveDone(string Transaction, string? Error);
    private readonly Dictionary<(ulong, ushort), Registered> _byKey = new();
    private readonly Dictionary<string, Registered> _byName = new();
    private ISourceQueueWithComplete<MatterEvent>? _queue;
    private IActorRef? _groups;
    private readonly HashSet<string> _groupNames = new();

    public static Props Props(IMatterController controller, IMqttPublisher mqtt, MqttTopics topics,
        INameStore? names = null, IThreadDatasetSource? thread = null) =>
        Akka.Actor.Props.Create(() => new MatterGatewayActor(controller, mqtt, topics, names, thread));

    public MatterGatewayActor(IMatterController controller, IMqttPublisher mqtt, MqttTopics topics,
        INameStore? names = null, IThreadDatasetSource? thread = null)
    {
        _controller = controller; _mqtt = mqtt; _topics = topics;
        _names = names ?? NullNameStore.Instance;
        _thread = thread ?? new FixedThreadDatasetSource(ThreadCredentials.None);
        _events = Context.System.EventStream;
        _overrides = new Dictionary<(ulong, ushort), string>(_names.Load());
        Context.System.EventStream.Subscribe(Self, typeof(Matterhorn.Groups.GroupNamesChanged));

        Receive<NodeAdded>(OnNodeAdded);
        Receive<NodeRemoved>(OnNodeRemoved);
        Receive<AttributeChanged>(ac =>
        {
            var r = ac.Reading;
            Log(LogCategory.Raw, "attribute_updated",
                $"{r.NodeId}/{r.Endpoint}/{r.ClusterId}/{r.AttributeId} = {r.Value.GetRawText()}");
            if (_byKey.TryGetValue((r.NodeId, r.Endpoint), out var reg))
                reg.Actor.Tell(new ApplyAttribute(r));
        });
        Receive<ReachabilityChanged>(OnReachabilityChanged);
        Receive<SetDevice>(s =>
        {
            if (_byName.TryGetValue(s.FriendlyName, out var reg)) reg.Actor.Tell(new ApplySet(s.Payload));
            else if (_groupNames.Contains(s.FriendlyName)) _groups?.Tell(new Matterhorn.Groups.GroupSet(s.FriendlyName, s.Payload));
        });
        Receive<RouteSet>(r =>
        {
            if (_byKey.TryGetValue(r.Key, out var reg)) reg.Actor.Tell(new ApplySet(r.Payload));
        });
        Receive<RouteGetState>(r =>
        {
            if (_byKey.TryGetValue(r.Key, out var reg)) reg.Actor.Forward(new GetState());
            else Sender.Tell(new DeviceStateSnapshot(false, null));
        });
        Receive<RegisterGroups>(r => _groups = r.Groups);
        Receive<RegisterScenes>(_ => { });
        Receive<Matterhorn.Groups.GroupNamesChanged>(g => { _groupNames.Clear(); _groupNames.UnionWith(g.Names); });
        Receive<MqttConnected>(_ => AnnounceAll());
        Receive<GetDevices>(_ => Sender.Tell((IReadOnlyList<DeviceDescriptor>)_byName.Values.Select(r => r.Descriptor).ToList()));
        Receive<GetDeviceState>(g =>
        {
            if (_byName.TryGetValue(g.FriendlyName, out var reg)) reg.Actor.Forward(new GetState());
            else Sender.Tell(new DeviceStateSnapshot(false, null));
        });
        Receive<ControllerInfo>(ci =>
        {
            _controllerInfo = ci;
            Log(LogCategory.Raw, "server_info",
                $"matter-server {ci.SdkVersion} · schema {ci.SchemaVersion} · bluetooth {(ci.BluetoothEnabled ? "enabled" : "disabled")}");
        });
        Receive<GetThreadStatus>(_ =>
        {
            // Capture actor state here, resolve off the actor: a border router we can't reach must
            // not stall the gateway just because someone opened the dashboard.
            var info = _controllerInfo;
            _thread.Resolve(CancellationToken.None)
                .ContinueWith(t => ThreadStatusOf(t.IsFaulted ? ThreadCredentials.None : t.Result, info))
                .PipeTo(Sender);
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
        Log(LogCategory.Raw, "node_added", $"node {info.NodeId} · {info.ProductName}", level: LogLevel.Ok);
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
        Context.System.EventStream.Publish(new DeviceRegistered((info.NodeId, info.Endpoint), name));
        PublishDevices();
        PublishEvent("device_joined", new { friendly_name = name });
        Log(LogCategory.Activity, "joined", $"{name} joined · node {info.NodeId}", device: name, level: LogLevel.Ok);
    }

    private void OnNodeRemoved(NodeRemoved msg)
    {
        Log(LogCategory.Raw, "node_removed", $"node {msg.NodeId}");
        // node_removed carries only the node id, so drop every endpoint registered under that node.
        var keys = _byKey.Keys.Where(k => k.Item1 == msg.NodeId).ToList();
        if (keys.Count == 0) return;
        var prunedOverride = false;
        foreach (var key in keys)
        {
            if (!_byKey.Remove(key, out var reg)) continue;
            _byName.Remove(reg.FriendlyName);
            Context.System.EventStream.Publish(new DeviceRemoved(key));
            Context.Stop(reg.Actor);
            // Drop any persisted name override for the departed endpoint so names.json doesn't grow forever.
            prunedOverride |= _overrides.Remove(key);
            PublishEvent("device_leave", new { friendly_name = reg.FriendlyName });
            Log(LogCategory.Activity, "removed", $"{reg.FriendlyName} removed", device: reg.FriendlyName);
        }
        if (prunedOverride) SaveOverrides();
        PublishDevices();
    }

    // Commissioning can take 30-60s. Run it off the actor and pipe the outcome back as CommissionDone
    // so the gateway keeps serving device queries and attribute routing while it's in flight.
    private void OnCommission(CommissionRequest req)
    {
        Log(LogCategory.Activity, "commission", "setup code accepted");
        CommissionFlow(req.Code).ContinueWith(t => t.IsFaulted
            ? new CommissionDone(req.Transaction, null, t.Exception!.GetBaseException().Message)
            : new CommissionDone(req.Transaction, t.Result, null)).PipeTo(Self);
    }

    /// <summary>
    /// Resolves Thread credentials and commissions. Both run off the actor.
    /// <para>
    /// The dataset is resolved here rather than on connect because the controller connection is an
    /// event stream with no "connected" signal to hook; doing it lazily also means a slow border
    /// router delays an operation that already takes 30-60s instead of blocking startup, and picks up
    /// a border router that came up after Matterhorn did.
    /// </para>
    /// </summary>
    private async Task<ulong> CommissionFlow(string code)
    {
        var thread = await _thread.Resolve(CancellationToken.None);
        if (thread.Dataset is not null)
        {
            // Sent before every commission rather than once per connection: it is idempotent, costs
            // one message against a 30-60s operation, and stays correct when the controller
            // reconnects (which drops the credentials it was holding).
            await _controller.SetThreadDataset(thread.Dataset, CancellationToken.None);
            Log(LogCategory.Raw, "set_thread_dataset", $"→ sent ({thread.Source})");
        }

        // Without credentials we can only reach a device that is already on IP — which is every
        // device Matterhorn could commission before Thread onboarding existed.
        var networkOnly = !thread.Available;
        Log(LogCategory.Raw, "commission_with_code", $"→ sent (network_only={networkOnly.ToString().ToLowerInvariant()})");
        return await _controller.Commission(code, networkOnly, CancellationToken.None);
    }

    private void OnCommissionDone(CommissionDone done)
    {
        if (done.Error is not null)
            Log(LogCategory.Activity, "commission_failed", $"commissioning failed: {done.Error}", level: LogLevel.Warn);
        _mqtt.Publish(_topics.Base + "/bridge/response/commission", JsonSerializer.Serialize(new
        {
            transaction = done.Transaction,
            status = done.Error is null ? "ok" : "error",
            node_id = done.NodeId?.ToString(),
            error = done.Error,
        }));
    }

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
        if (_groupNames.Contains(slug)) return new RenameResult(false, "name_taken");

        var updated = reg with { FriendlyName = slug, Descriptor = reg.Descriptor with { FriendlyName = slug } };
        _byName.Remove(from);
        _byName[slug] = updated;
        _byKey[(reg.Info.NodeId, reg.Info.Endpoint)] = updated;
        Context.System.EventStream.Publish(new DeviceRegistered((reg.Info.NodeId, reg.Info.Endpoint), slug));
        reg.Actor.Tell(new Rename(slug));

        _overrides[(reg.Info.NodeId, reg.Info.Endpoint)] = slug;
        SaveOverrides();

        PublishDevices();
        PublishEvent("device_renamed", new { from, to = slug });
        Log(LogCategory.Activity, "renamed", $"{from} → {slug}", device: slug);
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
        Log(LogCategory.Raw, "node_updated", $"node {r.NodeId} reachable={r.Reachable}");
        if (!_byKey.TryGetValue((r.NodeId, r.Endpoint), out var reg)) return;
        reg.Actor.Tell(new SetReachable(r.Reachable));
        if (reg.Descriptor.Reachable == r.Reachable) return;
        Log(LogCategory.Activity, r.Reachable ? "online" : "offline",
            $"{reg.FriendlyName} {(r.Reachable ? "online" : "offline")}",
            device: reg.FriendlyName, level: r.Reachable ? LogLevel.Ok : LogLevel.Warn);
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

    /// <summary>
    /// Can we onboard a brand-new Thread device right now? That needs both halves: credentials to
    /// hand over, and a Bluetooth radio to hand them over on. Pure, so it can run off the actor.
    /// </summary>
    private static ThreadStatus ThreadStatusOf(ThreadCredentials creds, ControllerInfo? controller)
    {
        if (!creds.Available)
            return new ThreadStatus(false, creds.Source, creds.BorderRouter, creds.Reason);

        // Only claim Bluetooth is the problem when the controller actually told us so — before it
        // connects we have no idea, and guessing would be worse than saying nothing.
        if (controller is { BluetoothEnabled: false })
            return new ThreadStatus(false, creds.Source, creds.BorderRouter,
                "the Matter controller has Bluetooth turned off, and a brand-new Thread device can only be reached over Bluetooth");

        return new ThreadStatus(true, creds.Source, creds.BorderRouter, null);
    }

    // Safe to call from the off-actor commission/remove tasks — see _events.
    private void Log(LogCategory category, string kind, string message,
                     string? device = null, LogLevel level = LogLevel.Info) =>
        _events.Publish(new LogEntry(DateTimeOffset.Now, category, kind, message, device, level));
}
