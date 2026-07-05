using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using Matterhorn.Bridge;
using Matterhorn.Configuration;
using Matterhorn.Mqtt;
using Matterhorn.Persistence;

namespace Matterhorn.Groups;

/// <summary>Owns group persistence + a device name↔key read-model (from DeviceRegistered/DeviceRemoved),
/// spawns one <see cref="GroupActor"/> per group, and is the name-addressed front door for group ops.</summary>
public sealed class GroupsSupervisor : ReceiveActor
{
    private readonly ILoggingAdapter _log = Context.GetLogger();
    private readonly IGroupStore _store;
    private readonly IActorRef _gateway;
    private readonly IMqttPublisher _mqtt;
    private readonly MqttTopics _topics;

    private readonly Dictionary<string, List<(ulong, ushort)>> _defs = new();       // group -> member keys
    private readonly Dictionary<string, IActorRef> _actors = new();                  // group -> entity
    private readonly Dictionary<(ulong, ushort), string> _deviceNames = new();       // key -> friendly name

    public static Props Props(IGroupStore store, IActorRef gateway, IMqttPublisher mqtt, MqttTopics topics) =>
        Akka.Actor.Props.Create(() => new GroupsSupervisor(store, gateway, mqtt, topics));

    public GroupsSupervisor(IGroupStore store, IActorRef gateway, IMqttPublisher mqtt, MqttTopics topics)
    {
        _store = store; _gateway = gateway; _mqtt = mqtt; _topics = topics;

        Receive<DeviceRegistered>(d => _deviceNames[d.Key] = d.FriendlyName);
        Receive<DeviceRemoved>(OnDeviceRemoved);
        Receive<CreateGroup>(OnCreate);
        Receive<DeleteGroup>(OnDelete);
        Receive<RenameGroup>(OnRename);
        Receive<AddGroupMember>(m => OnMember(m.Group, m.Device, m.Transaction, add: true));
        Receive<RemoveGroupMember>(m => OnMember(m.Group, m.Device, m.Transaction, add: false));
        Receive<GroupSet>(s => { if (_actors.TryGetValue(s.Name, out var a)) a.Tell(new ApplyGroupSet(s.Payload)); });
        Receive<GetGroups>(_ => Sender.Tell(Views()));
    }

    protected override void PreStart()
    {
        Context.System.EventStream.Subscribe(Self, typeof(DeviceRegistered));
        Context.System.EventStream.Subscribe(Self, typeof(DeviceRemoved));
        foreach (var (name, members) in _store.Load())
        {
            _defs[name] = members.ToList();
            _actors[name] = SpawnEntity(name, _defs[name]);
        }
        Announce();
    }

    private IActorRef SpawnEntity(string name, IReadOnlyList<(ulong, ushort)> members) =>
        Context.ActorOf(GroupActor.Props(name, members, _gateway, _mqtt, _topics), $"group-{name}");

    private void OnCreate(CreateGroup req)
    {
        var slug = FriendlyName.Slug(req.Name);
        if (slug.Length == 0) { Reply(new GroupOpResult(false, "invalid_name"), "create", req.Transaction, req.Name); return; }
        if (IsDeviceName(slug)) { Reply(new GroupOpResult(false, "collides_with_device"), "create", req.Transaction, req.Name); return; }

        var members = ResolveDevices(req.MemberDevices);
        _defs[slug] = members;
        if (_actors.TryGetValue(slug, out var existing)) existing.Tell(new UpdateGroupMembers(members)); // replace
        else _actors[slug] = SpawnEntity(slug, members);
        Persist();
        Reply(new GroupOpResult(true, null, slug), "create", req.Transaction, req.Name);
    }

    private void OnDelete(DeleteGroup req)
    {
        if (!_actors.TryGetValue(req.Name, out var actor)) { Reply(new GroupOpResult(false, "not_found"), "remove", req.Transaction, req.Name); return; }
        _mqtt.PublishRetained(_topics.Device(req.Name), "");   // clear retained group state
        Context.Stop(actor);
        _actors.Remove(req.Name); _defs.Remove(req.Name);
        Persist();
        Reply(new GroupOpResult(true, null, req.Name), "remove", req.Transaction, req.Name);
    }

    private void OnRename(RenameGroup req)
    {
        var slug = FriendlyName.Slug(req.To);
        if (slug.Length == 0) { Reply(new GroupOpResult(false, "invalid_name"), "rename", req.Transaction, req.From); return; }
        if (!_defs.TryGetValue(req.From, out var members)) { Reply(new GroupOpResult(false, "not_found"), "rename", req.Transaction, req.From); return; }
        if (slug == req.From) { Reply(new GroupOpResult(true, null, slug), "rename", req.Transaction, req.From); return; }
        if (_defs.ContainsKey(slug) || IsDeviceName(slug))
        { Reply(new GroupOpResult(false, _defs.ContainsKey(slug) ? "name_taken" : "collides_with_device"), "rename", req.Transaction, req.From); return; }

        var actor = _actors[req.From];
        _actors.Remove(req.From); _defs.Remove(req.From);
        _actors[slug] = actor; _defs[slug] = members;
        actor.Tell(new RenameGroupEntity(slug));
        Persist();
        Reply(new GroupOpResult(true, null, slug), "rename", req.Transaction, req.From);
    }

    private void OnMember(string group, string device, string tx, bool add)
    {
        if (!_defs.TryGetValue(group, out var members)) { Reply(new GroupOpResult(false, "not_found"), add ? "members/add" : "members/remove", tx, group); return; }
        var key = ResolveDevices(new[] { device }).FirstOrDefault();
        if (key == default && add) { Reply(new GroupOpResult(false, "not_found"), "members/add", tx, group); return; }
        if (add && !members.Contains(key)) members.Add(key);
        else if (!add) members.Remove(key);
        _actors[group].Tell(new UpdateGroupMembers(members));
        Persist();
        Reply(new GroupOpResult(true, null, group), add ? "members/add" : "members/remove", tx, group);
    }

    private void OnDeviceRemoved(DeviceRemoved d)
    {
        _deviceNames.Remove(d.Key);
        var touched = false;
        foreach (var (group, members) in _defs)
            if (members.Remove(d.Key)) { _actors[group].Tell(new UpdateGroupMembers(members)); touched = true; }
        if (touched) Persist();
    }

    private bool IsDeviceName(string name) => _deviceNames.Values.Contains(name);

    private List<(ulong, ushort)> ResolveDevices(IReadOnlyList<string> devices)
    {
        var keys = new List<(ulong, ushort)>();
        foreach (var name in devices)
        {
            var match = _deviceNames.FirstOrDefault(kv => kv.Value == name);
            if (!match.Equals(default(KeyValuePair<(ulong, ushort), string>))) keys.Add(match.Key);
        }
        return keys;
    }

    private IReadOnlyList<GroupView> Views() =>
        _defs.Select(kv => new GroupView(kv.Key,
            kv.Value.Select(k => _deviceNames.TryGetValue(k, out var n) ? n : DeviceKeys.Format(k)).ToList())).ToList();

    private void Persist()
    {
        try { _store.Save(_defs.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<(ulong, ushort)>)kv.Value)); }
        catch (Exception ex) { _log.Warning("Failed to persist groups: {Error}", ex.Message); }
        Announce();
    }

    private void Announce()
    {
        _mqtt.PublishRetained(_topics.BridgeGroups(), JsonSerializer.Serialize(Views(), JsonDefaults.SnakeCase));
        Context.System.EventStream.Publish(new GroupNamesChanged(_defs.Keys.ToHashSet()));
        Context.System.EventStream.Publish(new GroupListChanged());
    }

    private void Reply(GroupOpResult result, string action, string tx, string from)
    {
        Sender.Tell(result);   // REST Ask path; harmless when Told with NoSender (MQTT).
        _mqtt.Publish($"{_topics.Base}/bridge/response/group/{action}", JsonSerializer.Serialize(new
        {
            transaction = tx, status = result.Ok ? "ok" : "error", from, to = result.Name, error = result.Error,
        }));
    }
}
