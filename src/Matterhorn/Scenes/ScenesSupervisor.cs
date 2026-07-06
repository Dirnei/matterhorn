using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using Matterhorn.Bridge;
using Matterhorn.Configuration;
using Matterhorn.Devices;
using Matterhorn.Mqtt;
using Matterhorn.Persistence;

namespace Matterhorn.Scenes;

/// <summary>Owns scene persistence + a device name↔key read-model, spawns one <see cref="SceneActor"/>
/// per scene, and captures snapshots off-actor via the gateway's RouteGetState.</summary>
public sealed class ScenesSupervisor : ReceiveActor
{
    private static readonly HashSet<string> Settable = new() { "state", "brightness", "color_temp", "hue", "saturation" };

    private readonly ILoggingAdapter _log = Context.GetLogger();
    private readonly ISceneStore _store;
    private readonly IActorRef _gateway;
    private readonly IMqttPublisher _mqtt;
    private readonly MqttTopics _topics;

    private readonly Dictionary<string, Dictionary<(ulong, ushort), IReadOnlyDictionary<string, JsonElement>>> _scenes = new();
    private readonly Dictionary<string, IActorRef> _actors = new();
    private readonly Dictionary<(ulong, ushort), string> _deviceNames = new();
    private int _seq;   // monotonic id for child actor names, decoupled from the (mutable) friendly name

    // Off-actor snapshot result piped back to Self.
    private sealed record SnapshotCaptured(string Name, string Transaction,
        Dictionary<(ulong, ushort), IReadOnlyDictionary<string, JsonElement>> Values, IActorRef ReplyTo);

    public static Props Props(ISceneStore store, IActorRef gateway, IMqttPublisher mqtt, MqttTopics topics) =>
        Akka.Actor.Props.Create(() => new ScenesSupervisor(store, gateway, mqtt, topics));

    public ScenesSupervisor(ISceneStore store, IActorRef gateway, IMqttPublisher mqtt, MqttTopics topics)
    {
        _store = store; _gateway = gateway; _mqtt = mqtt; _topics = topics;

        Receive<DeviceRegistered>(d => _deviceNames[d.Key] = d.FriendlyName);
        Receive<DeviceRemoved>(OnDeviceRemoved);
        Receive<StoreScene>(OnStore);
        Receive<SnapshotCaptured>(OnSnapshotCaptured);
        Receive<DeleteScene>(OnDelete);
        Receive<RenameScene>(OnRename);
        Receive<RecallSceneByName>(OnRecall);
        Receive<GetScenes>(_ => Sender.Tell(Views()));
    }

    protected override void PreStart()
    {
        Context.System.EventStream.Subscribe(Self, typeof(DeviceRegistered));
        Context.System.EventStream.Subscribe(Self, typeof(DeviceRemoved));
        foreach (var (name, members) in _store.Load())
        {
            _scenes[name] = members.ToDictionary(kv => kv.Key, kv => kv.Value);
            _actors[name] = SpawnEntity(name, _scenes[name]);
        }
        Announce();
    }

    private IActorRef SpawnEntity(string name, IReadOnlyDictionary<(ulong, ushort), IReadOnlyDictionary<string, JsonElement>> values) =>
        Context.ActorOf(SceneActor.Props(name, CopyValues(values), _gateway), $"scene-{_seq++}");

    private static Dictionary<(ulong, ushort), IReadOnlyDictionary<string, JsonElement>> CopyValues(
        IReadOnlyDictionary<(ulong, ushort), IReadOnlyDictionary<string, JsonElement>> values) =>
        new(values);

    private void OnStore(StoreScene req)
    {
        var slug = FriendlyName.Slug(req.Name);
        if (slug.Length == 0) { Reply(new SceneOpResult(false, "invalid_name"), "store", req.Transaction, req.Name, null); return; }

        if (req.ExplicitState is not null)
        {
            var values = new Dictionary<(ulong, ushort), IReadOnlyDictionary<string, JsonElement>>();
            foreach (var (device, props) in req.ExplicitState)
                if (ResolveDevice(device) is { } key)
                    values[key] = props.Where(p => Settable.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
            Commit(slug, values, req.Transaction, Sender);
            return;
        }

        // Snapshot: Ask each member's current state off-actor, then pipe the assembled map back.
        var keys = req.Devices.Select(ResolveDevice).Where(k => k is not null).Select(k => k!.Value).ToList();
        var replyTo = Sender;
        var tasks = keys.ToDictionary(k => k, k => _gateway.Ask<DeviceStateSnapshot>(new RouteGetState(k), TimeSpan.FromSeconds(5)));
        Task.WhenAll(tasks.Values).ContinueWith(_ =>
        {
            var values = new Dictionary<(ulong, ushort), IReadOnlyDictionary<string, JsonElement>>();
            foreach (var (key, task) in tasks)
            {
                if (!task.IsCompletedSuccessfully || task.Result is not { Found: true, State: { } state }) continue;
                var props = new Dictionary<string, JsonElement>();
                foreach (var (k, v) in state)
                    if (Settable.Contains(k)) props[k] = JsonSerializer.SerializeToElement(v);
                values[key] = props;
            }
            return new SnapshotCaptured(slug, req.Transaction, values, replyTo);
        }).PipeTo(Self);
    }

    private void OnSnapshotCaptured(SnapshotCaptured s) => Commit(s.Name, s.Values, s.Transaction, s.ReplyTo);

    private void Commit(string slug, Dictionary<(ulong, ushort), IReadOnlyDictionary<string, JsonElement>> values, string tx, IActorRef replyTo)
    {
        _scenes[slug] = values;
        if (_actors.TryGetValue(slug, out var existing)) existing.Tell(new UpdateSceneValues(CopyValues(values)));
        else _actors[slug] = SpawnEntity(slug, values);
        Persist();
        var result = new SceneOpResult(true, null, slug);
        replyTo.Tell(result);
        PublishResponse(result, "store", tx, slug, slug);
    }

    private void OnDelete(DeleteScene req)
    {
        if (!_actors.TryGetValue(req.Name, out var actor)) { Reply(new SceneOpResult(false, "not_found"), "remove", req.Transaction, req.Name, null); return; }
        Context.Stop(actor);
        _actors.Remove(req.Name); _scenes.Remove(req.Name);
        Persist();
        Reply(new SceneOpResult(true, null, req.Name), "remove", req.Transaction, req.Name, req.Name);
    }

    private void OnRename(RenameScene req)
    {
        var slug = FriendlyName.Slug(req.To);
        if (slug.Length == 0) { Reply(new SceneOpResult(false, "invalid_name"), "rename", req.Transaction, req.From, null); return; }
        if (!_scenes.TryGetValue(req.From, out var values)) { Reply(new SceneOpResult(false, "not_found"), "rename", req.Transaction, req.From, null); return; }
        if (slug == req.From) { Reply(new SceneOpResult(true, null, slug), "rename", req.Transaction, req.From, slug); return; }
        if (_scenes.ContainsKey(slug)) { Reply(new SceneOpResult(false, "name_taken"), "rename", req.Transaction, req.From, null); return; }

        var actor = _actors[req.From];
        _actors.Remove(req.From); _scenes.Remove(req.From);
        _actors[slug] = actor; _scenes[slug] = values;
        Persist();
        Reply(new SceneOpResult(true, null, slug), "rename", req.Transaction, req.From, slug);
    }

    private void OnRecall(RecallSceneByName req)
    {
        if (!_actors.TryGetValue(req.Name, out var actor)) { Reply(new SceneOpResult(false, "not_found"), "recall", req.Transaction, req.Name, null); return; }
        actor.Tell(new RecallScene());
        Reply(new SceneOpResult(true, null, req.Name), "recall", req.Transaction, req.Name, req.Name);
    }

    private void OnDeviceRemoved(DeviceRemoved d)
    {
        _deviceNames.Remove(d.Key);
        var touched = false;
        foreach (var (scene, values) in _scenes)
            if (values.Remove(d.Key)) { _actors[scene].Tell(new UpdateSceneValues(CopyValues(values))); touched = true; }
        if (touched) Persist();
    }

    private (ulong, ushort)? ResolveDevice(string name)
    {
        foreach (var (key, n) in _deviceNames) if (n == name) return key;
        return null;
    }

    private IReadOnlyList<SceneView> Views() =>
        _scenes.Select(kv => new SceneView(kv.Key,
            kv.Value.Keys.Select(k => _deviceNames.TryGetValue(k, out var n) ? n : DeviceKeys.Format(k)).ToList())).ToList();

    private void Persist()
    {
        try
        {
            _store.Save(_scenes.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyDictionary<(ulong, ushort), IReadOnlyDictionary<string, JsonElement>>)kv.Value));
        }
        catch (Exception ex) { _log.Warning("Failed to persist scenes: {Error}", ex.Message); }
        Announce();
    }

    private void Announce()
    {
        _mqtt.PublishRetained(_topics.BridgeScenes(), JsonSerializer.Serialize(Views(), JsonDefaults.SnakeCase));
        Context.System.EventStream.Publish(new SceneListChanged());
    }

    private void Reply(SceneOpResult result, string action, string tx, string from, string? to)
    {
        Sender.Tell(result);
        PublishResponse(result, action, tx, from, to);
    }

    private void PublishResponse(SceneOpResult result, string action, string tx, string from, string? to) =>
        _mqtt.Publish($"{_topics.Base}/bridge/response/scene/{action}", JsonSerializer.Serialize(new
        {
            transaction = tx, status = result.Ok ? "ok" : "error", from, to = result.Name, error = result.Error,
        }));
}
