# Software Scenes & Groups Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add controller-side ("software") groups and scenes to Matterhorn — a named set of devices with a fan-out `/set` + optimistic echo (groups), and a named snapshot of device state that recall re-applies (scenes) — projected identically on MQTT, REST, and the dashboard.

**Architecture:** Child-per-entity Akka actors. `GroupsSupervisor`/`ScenesSupervisor` are top-level manager actors that own persistence (JSON stores mirroring `JsonNameStore`) and a name↔key read-model built from gateway `DeviceRegistered`/`DeviceRemoved` EventStream events; each spawns one `GroupActor`/`SceneActor` per entity. Entities fan out by telling the gateway `RouteSet(key, payload)` (the gateway is the device router and owns `_byKey`), keeping membership keyed by stable `(nodeId, endpoint)` so renames don't orphan it and removals prune cleanly.

**Tech Stack:** .NET 10, Akka.NET + Akka.Hosting, Akka.TestKit.Xunit2, HiveMQtt, NSwag (contract-first REST from `contracts/matterhorn.openapi.yaml`), vanilla-JS dashboard (`wwwroot/index.html`).

## Global Constraints

- **.NET 10**; solution `Matterhorn.slnx`; app `src/Matterhorn`, tests `src/Matterhorn.Test`.
- **Vertical slices by concern**, ASP.NET-style namespaces (`Matterhorn.Groups`, `Matterhorn.Scenes`, `Matterhorn.Persistence`, …). No layered folders.
- **Contract-first REST:** edit `contracts/matterhorn.openapi.yaml` first, then `dotnet build` regenerates `obj/generated/ApiContract.g.cs`; implement the generated abstract method in `Api/MatterhornController`. Never hand-edit generated code.
- **MQTT and REST are two projections of one model** — every operation exists on both.
- **Wire surface speaks friendly names**; persistence and internal membership use stable `(nodeId, endpoint)` keys serialized as `"{node}_{endpoint}"` (the `JsonNameStore` convention).
- **DTO wire names are snake_case** via `[JsonPropertyName]` in generated DTOs; JSON omits null fields (`DefaultIgnoreCondition = WhenWritingNull`, already configured in `Program.cs`).
- **Persistence degrades, never fatal:** a store `Save` failure is logged and the operation continues in memory (as `JsonNameStore`/`SaveOverrides` already do).
- **Commit after every task.** No `Co-Authored-By` trailer. No `git push`. Conventional-commit messages (`feat:`, `test:`, `refactor:`).
- **Run one test:** `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~<TypeOrMethod>"`. Full suite: `dotnet test src/Matterhorn.Test`.

---

## Phase 0 — Shared gateway seams

Groups (Part A) and scenes (Part B) both need these. Part A depends on Tasks 1–2; Part B additionally on Task 3.

### Task 1: `RouteSet` — fan-out a set to a device by stable key

The gateway owns `_byKey` (the authoritative device→endpoint-actor map). Give entity actors a way to command a device by stable key without knowing endpoint actor paths.

**Files:**
- Modify: `src/Matterhorn/Bridge/BridgeMessages.cs`
- Modify: `src/Matterhorn/Bridge/MatterGatewayActor.cs` (constructor `Receive` wiring ~line 55)
- Test: `src/Matterhorn.Test/Bridge/MatterGatewayActorTests.cs`

**Interfaces:**
- Produces: `record RouteSet((ulong NodeId, ushort Endpoint) Key, IReadOnlyDictionary<string, JsonElement> Payload)` — telling the gateway this routes `ApplySet(Payload)` to the endpoint actor registered under `Key`, or drops it if none.

- [ ] **Step 1: Write the failing test**

Add to `MatterGatewayActorTests`:

```csharp
[Fact]
public void RouteSet_by_key_reaches_the_device()
{
    var fake = new FakeMatterController();
    var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, new InMemoryMqttPublisher(), new MqttTopics("matterhorn")));
    fake.Emit(new NodeAdded(Light(9)));
    AwaitAssert(() => Assert.NotEmpty(gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result));

    var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"state":"ON"}""")!;
    gw.Tell(new RouteSet((9UL, (ushort)1), payload));

    AwaitAssert(() => Assert.Contains(fake.Invocations, i => i.NodeId == 9 && i.Cmd.CommandName == "On"));
}

[Fact]
public void RouteSet_for_unknown_key_is_a_harmless_noop()
{
    var fake = new FakeMatterController();
    var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, new InMemoryMqttPublisher(), new MqttTopics("matterhorn")));
    gw.Tell(new RouteSet((404UL, (ushort)1),
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"state":"ON"}""")!));
    ExpectNoMsg(TimeSpan.FromMilliseconds(200));
    Assert.Empty(fake.Invocations);
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~RouteSet"`
Expected: FAIL — `RouteSet` does not exist (compile error).

- [ ] **Step 3: Add the message**

Append to `src/Matterhorn/Bridge/BridgeMessages.cs`:

```csharp
/// <summary>Route a partial state change to a device by its stable key (group/scene fan-out).
/// No-op if no endpoint is registered under the key.</summary>
public record RouteSet((ulong NodeId, ushort Endpoint) Key, IReadOnlyDictionary<string, JsonElement> Payload);
```

- [ ] **Step 4: Handle it in the gateway**

In `MatterGatewayActor`'s constructor, after the existing `Receive<SetDevice>(...)` block (~line 58), add:

```csharp
Receive<RouteSet>(r =>
{
    if (_byKey.TryGetValue(r.Key, out var reg)) reg.Actor.Tell(new ApplySet(r.Payload));
});
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~RouteSet"`
Expected: PASS (both).

- [ ] **Step 6: Commit**

```bash
git add src/Matterhorn/Bridge/BridgeMessages.cs src/Matterhorn/Bridge/MatterGatewayActor.cs src/Matterhorn.Test/Bridge/MatterGatewayActorTests.cs
git commit -m "feat: gateway RouteSet to command a device by stable key"
```

---

### Task 2: Device lifecycle events on the EventStream

Supervisors need a name↔key read-model and removal notifications. Emit `DeviceRegistered`/`DeviceRemoved` from the gateway at the points it already mutates `_byKey`/`_byName`.

**Files:**
- Modify: `src/Matterhorn/Bridge/BridgeMessages.cs`
- Modify: `src/Matterhorn/Bridge/MatterGatewayActor.cs` (`OnNodeAdded` ~line 104, `DoRename` ~line 204, `OnNodeRemoved` ~line 123)
- Test: `src/Matterhorn.Test/Bridge/MatterGatewayActorTests.cs`

**Interfaces:**
- Produces:
  - `record DeviceRegistered((ulong NodeId, ushort Endpoint) Key, string FriendlyName)` — published on `Context.System.EventStream` when a device joins **and** when it is renamed (upsert of the key→name mapping).
  - `record DeviceRemoved((ulong NodeId, ushort Endpoint) Key)` — published per endpoint when a node is removed.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void NodeAdded_and_NodeRemoved_publish_device_lifecycle_events()
{
    Sys.EventStream.Subscribe(TestActor, typeof(DeviceRegistered));
    Sys.EventStream.Subscribe(TestActor, typeof(DeviceRemoved));
    var gw = Sys.ActorOf(MatterGatewayActor.Props(
        new FakeMatterController(), new InMemoryMqttPublisher(), new MqttTopics("matterhorn")));

    gw.Tell(new NodeAdded(Light(5)));
    ExpectMsg<DeviceRegistered>(m => m.Key == (5UL, (ushort)1) && m.FriendlyName == "bulb_5_1");

    gw.Tell(new NodeRemoved(5));
    ExpectMsg<DeviceRemoved>(m => m.Key == (5UL, (ushort)1));
}

[Fact]
public void Rename_republishes_DeviceRegistered_with_the_new_name()
{
    Sys.EventStream.Subscribe(TestActor, typeof(DeviceRegistered));
    var gw = Sys.ActorOf(MatterGatewayActor.Props(
        new FakeMatterController(), new InMemoryMqttPublisher(), new MqttTopics("matterhorn"), new InMemoryNameStore()));
    gw.Tell(new NodeAdded(Light(5)));
    ExpectMsg<DeviceRegistered>(m => m.FriendlyName == "bulb_5_1");

    gw.Ask<RenameResult>(new RenameRequest("bulb_5_1", "lamp", "tx1")).Wait();
    ExpectMsg<DeviceRegistered>(m => m.Key == (5UL, (ushort)1) && m.FriendlyName == "lamp");
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~lifecycle|FullyQualifiedName~republishes_DeviceRegistered"`
Expected: FAIL — types don't exist.

- [ ] **Step 3: Add the messages**

Append to `BridgeMessages.cs`:

```csharp
/// <summary>Published on the EventStream when a device joins or is renamed (upsert key→name).
/// Consumed by the group/scene supervisors' name↔key read-model.</summary>
public record DeviceRegistered((ulong NodeId, ushort Endpoint) Key, string FriendlyName);

/// <summary>Published on the EventStream per endpoint when a node is removed — drives group/scene pruning.</summary>
public record DeviceRemoved((ulong NodeId, ushort Endpoint) Key);
```

- [ ] **Step 4: Emit them**

In `OnNodeAdded`, immediately after `_byName[name] = reg;` (~line 103):

```csharp
Context.System.EventStream.Publish(new DeviceRegistered((info.NodeId, info.Endpoint), name));
```

In `DoRename`, after `_byKey[(reg.Info.NodeId, reg.Info.Endpoint)] = updated;` (~line 198):

```csharp
Context.System.EventStream.Publish(new DeviceRegistered((reg.Info.NodeId, reg.Info.Endpoint), slug));
```

In `OnNodeRemoved`, inside the `foreach (var key in keys)` loop after `_byName.Remove(reg.FriendlyName);` (~line 119):

```csharp
Context.System.EventStream.Publish(new DeviceRemoved(key));
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~lifecycle|FullyQualifiedName~republishes_DeviceRegistered"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Matterhorn/Bridge/BridgeMessages.cs src/Matterhorn/Bridge/MatterGatewayActor.cs src/Matterhorn.Test/Bridge/MatterGatewayActorTests.cs
git commit -m "feat: emit DeviceRegistered/DeviceRemoved lifecycle events"
```

---

### Task 3: `DeviceKeys` — shared stable-key string format (needed by Part B; harmless in Part A)

Groups and scenes persist `(nodeId, endpoint)` as `"{node}_{endpoint}"`. Extract one parser/formatter both stores use.

**Files:**
- Create: `src/Matterhorn/Persistence/DeviceKeys.cs`
- Test: `src/Matterhorn.Test/Persistence/DeviceKeysTests.cs`

**Interfaces:**
- Produces: `static class DeviceKeys` with
  - `string Format((ulong NodeId, ushort Endpoint) key)` → `"{node}_{endpoint}"`
  - `bool TryParse(string s, out (ulong NodeId, ushort Endpoint) key)`

- [ ] **Step 1: Write the failing test**

Create `src/Matterhorn.Test/Persistence/DeviceKeysTests.cs`:

```csharp
using Matterhorn.Persistence;

namespace Matterhorn.Test.Persistence;

public class DeviceKeysTests
{
    [Fact]
    public void Format_joins_node_and_endpoint_with_underscore()
        => Assert.Equal("5_1", DeviceKeys.Format((5UL, 1)));

    [Fact]
    public void TryParse_round_trips_a_formatted_key()
    {
        Assert.True(DeviceKeys.TryParse("5_1", out var key));
        Assert.Equal((5UL, (ushort)1), key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("5")]
    [InlineData("a_b")]
    [InlineData("5_1_2")]
    public void TryParse_rejects_malformed(string s) => Assert.False(DeviceKeys.TryParse(s, out _));
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~DeviceKeysTests"`
Expected: FAIL — `DeviceKeys` not defined.

- [ ] **Step 3: Implement**

Create `src/Matterhorn/Persistence/DeviceKeys.cs`:

```csharp
namespace Matterhorn.Persistence;

/// <summary>Stable device-key ⇄ string codec ("{node}_{endpoint}") shared by the JSON stores.</summary>
public static class DeviceKeys
{
    public static string Format((ulong NodeId, ushort Endpoint) key) => $"{key.NodeId}_{key.Endpoint}";

    public static bool TryParse(string s, out (ulong NodeId, ushort Endpoint) key)
    {
        key = default;
        var parts = s.Split('_');
        if (parts.Length != 2) return false;
        if (!ulong.TryParse(parts[0], out var node) || !ushort.TryParse(parts[1], out var ep)) return false;
        key = (node, ep);
        return true;
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~DeviceKeysTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Matterhorn/Persistence/DeviceKeys.cs src/Matterhorn.Test/Persistence/DeviceKeysTests.cs
git commit -m "feat: shared DeviceKeys stable-key codec"
```

---

## Part A — Groups (end-to-end: MQTT + REST + dashboard)

### Task 4: `IGroupStore` + `JsonGroupStore`

**Files:**
- Create: `src/Matterhorn/Persistence/IGroupStore.cs`
- Create: `src/Matterhorn/Persistence/JsonGroupStore.cs`
- Test: `src/Matterhorn.Test/Persistence/JsonGroupStoreTests.cs`

**Interfaces:**
- Produces:
  - `interface IGroupStore { IReadOnlyDictionary<string, IReadOnlyList<(ulong NodeId, ushort Endpoint)>> Load(); void Save(IReadOnlyDictionary<string, IReadOnlyList<(ulong NodeId, ushort Endpoint)>> groups); }`
  - `sealed class JsonGroupStore(string path) : IGroupStore` — persists `{ "<group>": { "members": ["5_1", ...] } }`.

- [ ] **Step 1: Write the failing test**

Create `src/Matterhorn.Test/Persistence/JsonGroupStoreTests.cs`:

```csharp
using Matterhorn.Persistence;

namespace Matterhorn.Test.Persistence;

public class JsonGroupStoreTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"groups-{Guid.NewGuid():N}.json");

    [Fact]
    public void Load_missing_file_returns_empty() => Assert.Empty(new JsonGroupStore(TempFile()).Load());

    [Fact]
    public void Save_then_load_round_trips_members()
    {
        var path = TempFile();
        try
        {
            new JsonGroupStore(path).Save(new Dictionary<string, IReadOnlyList<(ulong, ushort)>>
            {
                ["living_room"] = new (ulong, ushort)[] { (5, 1), (9, 1) },
            });

            var loaded = new JsonGroupStore(path).Load();
            Assert.Equal(new (ulong, ushort)[] { (5, 1), (9, 1) }, loaded["living_room"]);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_corrupt_file_returns_empty()
    {
        var path = TempFile();
        try { File.WriteAllText(path, "{ not json"); Assert.Empty(new JsonGroupStore(path).Load()); }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~JsonGroupStoreTests"`
Expected: FAIL — types missing.

- [ ] **Step 3: Implement the interface + store**

Create `src/Matterhorn/Persistence/IGroupStore.cs`:

```csharp
namespace Matterhorn.Persistence;

/// <summary>Persists group membership, keyed by group name → stable device keys.</summary>
public interface IGroupStore
{
    IReadOnlyDictionary<string, IReadOnlyList<(ulong NodeId, ushort Endpoint)>> Load();
    void Save(IReadOnlyDictionary<string, IReadOnlyList<(ulong NodeId, ushort Endpoint)>> groups);
}
```

Create `src/Matterhorn/Persistence/JsonGroupStore.cs`:

```csharp
using System.Text.Json;

namespace Matterhorn.Persistence;

/// <summary>Stores groups as { "<group>": { "members": ["<node>_<endpoint>", ...] } }.
/// Missing/corrupt file loads empty; writes swap atomically via a temp file (never a torn write).</summary>
public sealed class JsonGroupStore(string path) : IGroupStore
{
    private sealed record Entry(List<string> members);

    public IReadOnlyDictionary<string, IReadOnlyList<(ulong, ushort)>> Load()
    {
        var result = new Dictionary<string, IReadOnlyList<(ulong, ushort)>>();
        if (!File.Exists(path)) return result;
        Dictionary<string, Entry>? raw;
        try { raw = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path)); }
        catch (JsonException) { return result; }
        if (raw is null) return result;
        foreach (var (name, entry) in raw)
        {
            var keys = new List<(ulong, ushort)>();
            foreach (var m in entry.members ?? new())
                if (DeviceKeys.TryParse(m, out var key)) keys.Add(key);
            result[name] = keys;
        }
        return result;
    }

    public void Save(IReadOnlyDictionary<string, IReadOnlyList<(ulong, ushort)>> groups)
    {
        var raw = groups.ToDictionary(
            kv => kv.Key,
            kv => new Entry(kv.Value.Select(DeviceKeys.Format).ToList()));
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(raw, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~JsonGroupStoreTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Matterhorn/Persistence/IGroupStore.cs src/Matterhorn/Persistence/JsonGroupStore.cs src/Matterhorn.Test/Persistence/JsonGroupStoreTests.cs
git commit -m "feat: JsonGroupStore group-membership persistence"
```

---

### Task 5: Group messages + `GroupActor`

The per-group entity: holds member keys + optimistic echo, fans out via `RouteSet`, publishes the retained group state topic + an SSE `state` frame.

**Files:**
- Create: `src/Matterhorn/Groups/GroupMessages.cs`
- Create: `src/Matterhorn/Groups/GroupActor.cs`
- Test: `src/Matterhorn.Test/Groups/GroupActorTests.cs`

**Interfaces:**
- Consumes: `RouteSet` (Task 1), `DeviceStateChanged` (`Matterhorn.Devices`), `IMqttPublisher`, `MqttTopics`.
- Produces:
  - `record ApplyGroupSet(IReadOnlyDictionary<string, JsonElement> Payload)` — tell the entity to fan out + echo.
  - `record UpdateGroupMembers(IReadOnlyList<(ulong NodeId, ushort Endpoint)> Members)` — supervisor pushes a new member set.
  - `record RenameGroupEntity(string NewName)` — supervisor pushes a new name (clears old retained topic).
  - `GroupActor.Props(string name, IReadOnlyList<(ulong,ushort)> members, IActorRef gateway, IMqttPublisher mqtt, MqttTopics topics)`.

- [ ] **Step 1: Write the failing test**

Create `src/Matterhorn.Test/Groups/GroupActorTests.cs`:

```csharp
using System.Text.Json;
using Akka.TestKit.Xunit2;
using Matterhorn.Bridge;
using Matterhorn.Devices;
using Matterhorn.Groups;
using Matterhorn.Mqtt;
using Matterhorn.Test.Mqtt;

namespace Matterhorn.Test.Groups;

public class GroupActorTests : TestKit
{
    private static Dictionary<string, JsonElement> Payload(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void ApplyGroupSet_fans_out_RouteSet_to_every_member()
    {
        var gateway = CreateTestProbe();
        var actor = Sys.ActorOf(GroupActor.Props("living_room",
            new (ulong, ushort)[] { (5, 1), (9, 1) }, gateway.Ref, new InMemoryMqttPublisher(), new MqttTopics("matterhorn")));

        actor.Tell(new ApplyGroupSet(Payload("""{"state":"OFF"}""")));

        var a = gateway.ExpectMsg<RouteSet>();
        var b = gateway.ExpectMsg<RouteSet>();
        Assert.Equal(new[] { (5UL, (ushort)1), (9UL, (ushort)1) }.ToHashSet(), new[] { a.Key, b.Key }.ToHashSet());
        Assert.Equal("OFF", a.Payload["state"].GetString());
    }

    [Fact]
    public void ApplyGroupSet_echoes_requested_payload_onto_retained_group_topic()
    {
        var mqtt = new InMemoryMqttPublisher();
        var actor = Sys.ActorOf(GroupActor.Props("living_room",
            new (ulong, ushort)[] { (5, 1) }, CreateTestProbe().Ref, mqtt, new MqttTopics("matterhorn")));

        actor.Tell(new ApplyGroupSet(Payload("""{"state":"ON","brightness":128}""")));

        AwaitAssert(() =>
        {
            var echo = Assert.Single(mqtt.Messages, m => m.Topic == "matterhorn/living_room" && m.Retained);
            Assert.Contains("\"state\":\"ON\"", echo.Payload);
            Assert.Contains("\"brightness\":128", echo.Payload);
        });
    }

    [Fact]
    public void ApplyGroupSet_publishes_a_state_event_for_the_group()
    {
        Sys.EventStream.Subscribe(TestActor, typeof(DeviceStateChanged));
        var actor = Sys.ActorOf(GroupActor.Props("living_room",
            new (ulong, ushort)[] { (5, 1) }, CreateTestProbe().Ref, new InMemoryMqttPublisher(), new MqttTopics("matterhorn")));

        actor.Tell(new ApplyGroupSet(Payload("""{"state":"ON"}""")));

        ExpectMsg<DeviceStateChanged>(e => e.FriendlyName == "living_room" && e.StateJson.Contains("\"state\":\"ON\""));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~GroupActorTests"`
Expected: FAIL — `GroupActor` missing.

- [ ] **Step 3: Add the messages**

Create `src/Matterhorn/Groups/GroupMessages.cs`:

```csharp
using System.Text.Json;

namespace Matterhorn.Groups;

/// <summary>Tell a <see cref="GroupActor"/> to fan out a set to its members and echo it.</summary>
public record ApplyGroupSet(IReadOnlyDictionary<string, JsonElement> Payload);

/// <summary>Supervisor pushes a group's new member set to its entity.</summary>
public record UpdateGroupMembers(IReadOnlyList<(ulong NodeId, ushort Endpoint)> Members);

/// <summary>Supervisor pushes a group's new name; the entity clears the old retained topic.</summary>
public record RenameGroupEntity(string NewName);
```

- [ ] **Step 4: Implement `GroupActor`**

Create `src/Matterhorn/Groups/GroupActor.cs`:

```csharp
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
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~GroupActorTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Matterhorn/Groups/ src/Matterhorn.Test/Groups/GroupActorTests.cs
git commit -m "feat: GroupActor fan-out with optimistic echo"
```

---

### Task 6: `GroupsSupervisor`

The manager: owns the store + name↔key read-model, spawns/stops `GroupActor`s, handles create/delete/rename/member CRUD/set/query, prunes on `DeviceRemoved`, and publishes `bridge/groups` + `GroupNamesChanged` (for the gateway's rename-collision check) + `GroupListChanged` (for SSE) + `bridge/response/group/*`.

**Files:**
- Modify: `src/Matterhorn/Groups/GroupMessages.cs`
- Create: `src/Matterhorn/Groups/GroupsSupervisor.cs`
- Test: `src/Matterhorn.Test/Groups/GroupsSupervisorTests.cs`

**Interfaces:**
- Consumes: `IGroupStore`, `IMqttPublisher`, `MqttTopics`, gateway `IActorRef`, `DeviceRegistered`/`DeviceRemoved` (Task 2), `DeviceKeys` (Task 3).
- Produces (public command/reply/query messages + events):
  - `record CreateGroup(string Name, IReadOnlyList<string> MemberDevices, string Transaction)`
  - `record DeleteGroup(string Name, string Transaction)`
  - `record RenameGroup(string From, string To, string Transaction)`
  - `record AddGroupMember(string Group, string Device, string Transaction)`
  - `record RemoveGroupMember(string Group, string Device, string Transaction)`
  - `record GroupSet(string Name, IReadOnlyDictionary<string, JsonElement> Payload)` — routed set (from REST/MQTT).
  - `record GroupOpResult(bool Ok, string? Error, string? Name = null)` — reply (Error ∈ `invalid_name|not_found|name_taken|collides_with_device`).
  - `record GetGroups` → reply `IReadOnlyList<GroupView>`; `record GroupView(string FriendlyName, IReadOnlyList<string> Members)`.
  - `record GroupNamesChanged(IReadOnlySet<string> Names)` — EventStream (gateway consumes).
  - `record GroupListChanged` — EventStream (SSE consumes).
  - `GroupsSupervisor.Props(IGroupStore store, IActorRef gateway, IMqttPublisher mqtt, MqttTopics topics)`.

- [ ] **Step 1: Add the message types**

Append to `src/Matterhorn/Groups/GroupMessages.cs`:

```csharp
/// <summary>Create-or-replace a group with an initial member set (device friendly names).</summary>
public record CreateGroup(string Name, IReadOnlyList<string> MemberDevices, string Transaction);
public record DeleteGroup(string Name, string Transaction);
public record RenameGroup(string From, string To, string Transaction);
public record AddGroupMember(string Group, string Device, string Transaction);
public record RemoveGroupMember(string Group, string Device, string Transaction);

/// <summary>A set addressed to a group (from REST PATCH or MQTT &lt;group&gt;/set).</summary>
public record GroupSet(string Name, IReadOnlyDictionary<string, System.Text.Json.JsonElement> Payload);

/// <summary>Reply. Error ∈ invalid_name | not_found | name_taken | collides_with_device.</summary>
public record GroupOpResult(bool Ok, string? Error, string? Name = null);

public record GetGroups;
public record GroupView(string FriendlyName, IReadOnlyList<string> Members);

/// <summary>EventStream: the set of group names changed (gateway consumes for rename-collision checks).</summary>
public record GroupNamesChanged(IReadOnlySet<string> Names);

/// <summary>EventStream: the group list changed (SSE consumes).</summary>
public record GroupListChanged;
```

- [ ] **Step 2: Write the failing tests**

Create `src/Matterhorn.Test/Groups/GroupsSupervisorTests.cs`:

```csharp
using System.Text.Json;
using Akka.Actor;
using Akka.TestKit.Xunit2;
using Matterhorn.Bridge;
using Matterhorn.Groups;
using Matterhorn.Mqtt;
using Matterhorn.Persistence;
using Matterhorn.Test.Mqtt;

namespace Matterhorn.Test.Groups;

public class GroupsSupervisorTests : TestKit
{
    private sealed class MemGroupStore : IGroupStore
    {
        public Dictionary<string, IReadOnlyList<(ulong, ushort)>> Groups { get; } = new();
        public IReadOnlyDictionary<string, IReadOnlyList<(ulong, ushort)>> Load() => Groups;
        public void Save(IReadOnlyDictionary<string, IReadOnlyList<(ulong, ushort)>> g)
        { Groups.Clear(); foreach (var (k, v) in g) Groups[k] = v; }
    }

    // A supervisor pre-seeded with two known devices (via DeviceRegistered) it can resolve by name.
    private (IActorRef sup, TestProbe gateway, InMemoryMqttPublisher mqtt, MemGroupStore store) NewSupervisor()
    {
        var gateway = CreateTestProbe();
        var mqtt = new InMemoryMqttPublisher();
        var store = new MemGroupStore();
        var sup = Sys.ActorOf(GroupsSupervisor.Props(store, gateway.Ref, mqtt, new MqttTopics("matterhorn")));
        Sys.EventStream.Publish(new DeviceRegistered((5UL, 1), "lamp"));
        Sys.EventStream.Publish(new DeviceRegistered((9UL, 1), "strip"));
        // let the read-model settle
        AwaitAssert(() => { sup.Tell(new GetGroups()); ExpectMsg<IReadOnlyList<GroupView>>(); });
        return (sup, gateway, mqtt, store);
    }

    [Fact]
    public void CreateGroup_persists_members_and_publishes_bridge_groups()
    {
        var (sup, _, mqtt, store) = NewSupervisor();
        var r = sup.Ask<GroupOpResult>(new CreateGroup("living_room", new[] { "lamp", "strip" }, "t1")).Result;

        Assert.True(r.Ok);
        Assert.Equal(new (ulong, ushort)[] { (5, 1), (9, 1) }, store.Groups["living_room"]);
        AwaitAssert(() => Assert.Contains(mqtt.Messages,
            m => m.Topic == "matterhorn/bridge/groups" && m.Payload.Contains("living_room") && m.Payload.Contains("lamp")));
    }

    [Fact]
    public void CreateGroup_rejects_a_name_that_is_a_device()
    {
        var (sup, _, _, _) = NewSupervisor();
        var r = sup.Ask<GroupOpResult>(new CreateGroup("lamp", Array.Empty<string>(), "t1")).Result;
        Assert.False(r.Ok);
        Assert.Equal("collides_with_device", r.Error);
    }

    [Fact]
    public void GroupSet_routes_ApplyGroupSet_fanout_via_gateway()
    {
        var (sup, gateway, _, _) = NewSupervisor();
        sup.Ask<GroupOpResult>(new CreateGroup("living_room", new[] { "lamp", "strip" }, "t1")).Wait();

        sup.Tell(new GroupSet("living_room",
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"state":"OFF"}""")!));

        gateway.ExpectMsg<RouteSet>(m => m.Key == (5UL, (ushort)1));
        gateway.ExpectMsg<RouteSet>(m => m.Key == (9UL, (ushort)1));
    }

    [Fact]
    public void DeviceRemoved_prunes_the_member_from_every_group()
    {
        var (sup, _, _, store) = NewSupervisor();
        sup.Ask<GroupOpResult>(new CreateGroup("living_room", new[] { "lamp", "strip" }, "t1")).Wait();

        Sys.EventStream.Publish(new DeviceRemoved((5UL, 1)));

        AwaitAssert(() => Assert.Equal(new (ulong, ushort)[] { (9, 1) }, store.Groups["living_room"]));
    }

    [Fact]
    public void RenameGroup_to_an_existing_group_is_rejected()
    {
        var (sup, _, _, _) = NewSupervisor();
        sup.Ask<GroupOpResult>(new CreateGroup("a", Array.Empty<string>(), "t1")).Wait();
        sup.Ask<GroupOpResult>(new CreateGroup("b", Array.Empty<string>(), "t2")).Wait();

        var r = sup.Ask<GroupOpResult>(new RenameGroup("a", "b", "t3")).Result;
        Assert.False(r.Ok);
        Assert.Equal("name_taken", r.Error);
    }

    [Fact]
    public void CreateGroup_publishes_GroupNamesChanged()
    {
        Sys.EventStream.Subscribe(TestActor, typeof(GroupNamesChanged));
        var (sup, _, _, _) = NewSupervisor();
        sup.Ask<GroupOpResult>(new CreateGroup("living_room", Array.Empty<string>(), "t1")).Wait();
        AwaitAssert(() =>
        {
            var seen = false;
            while (TryReceiveOne(out var m, TimeSpan.Zero))
                if (m.Message is GroupNamesChanged g && g.Names.Contains("living_room")) seen = true;
            Assert.True(seen);
        });
    }
}
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~GroupsSupervisorTests"`
Expected: FAIL — `GroupsSupervisor` missing.

- [ ] **Step 4: Implement `GroupsSupervisor`**

Create `src/Matterhorn/Groups/GroupsSupervisor.cs`:

```csharp
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
        if (slug.Length == 0) { Reply(new GroupOpResult(false, "invalid_name"), "create", req.Transaction, req.Name, null); return; }
        if (IsDeviceName(slug)) { Reply(new GroupOpResult(false, "collides_with_device"), "create", req.Transaction, req.Name, slug); return; }

        var members = ResolveDevices(req.MemberDevices);
        _defs[slug] = members;
        if (_actors.TryGetValue(slug, out var existing)) existing.Tell(new UpdateGroupMembers(members)); // replace
        else _actors[slug] = SpawnEntity(slug, members);
        Persist();
        Reply(new GroupOpResult(true, null, slug), "create", req.Transaction, req.Name, slug);
    }

    private void OnDelete(DeleteGroup req)
    {
        if (!_actors.TryGetValue(req.Name, out var actor)) { Reply(new GroupOpResult(false, "not_found"), "remove", req.Transaction, req.Name, null); return; }
        _mqtt.PublishRetained(_topics.Device(req.Name), "");   // clear retained group state
        Context.Stop(actor);
        _actors.Remove(req.Name); _defs.Remove(req.Name);
        Persist();
        Reply(new GroupOpResult(true, null, req.Name), "remove", req.Transaction, req.Name, req.Name);
    }

    private void OnRename(RenameGroup req)
    {
        var slug = FriendlyName.Slug(req.To);
        if (slug.Length == 0) { Reply(new GroupOpResult(false, "invalid_name"), "rename", req.Transaction, req.From, null); return; }
        if (!_defs.TryGetValue(req.From, out var members)) { Reply(new GroupOpResult(false, "not_found"), "rename", req.Transaction, req.From, null); return; }
        if (slug == req.From) { Reply(new GroupOpResult(true, null, slug), "rename", req.Transaction, req.From, slug); return; }
        if (_defs.ContainsKey(slug) || IsDeviceName(slug))
        { Reply(new GroupOpResult(false, _defs.ContainsKey(slug) ? "name_taken" : "collides_with_device"), "rename", req.Transaction, req.From, null); return; }

        var actor = _actors[req.From];
        _actors.Remove(req.From); _defs.Remove(req.From);
        _actors[slug] = actor; _defs[slug] = members;
        actor.Tell(new RenameGroupEntity(slug));
        Persist();
        Reply(new GroupOpResult(true, null, slug), "rename", req.Transaction, req.From, slug);
    }

    private void OnMember(string group, string device, string tx, bool add)
    {
        if (!_defs.TryGetValue(group, out var members)) { Reply(new GroupOpResult(false, "not_found"), add ? "members/add" : "members/remove", tx, group, null); return; }
        var key = ResolveDevices(new[] { device }).FirstOrDefault();
        if (key == default && add) { Reply(new GroupOpResult(false, "not_found"), "members/add", tx, group, null); return; }
        if (add && !members.Contains(key)) members.Add(key);
        else if (!add) members.Remove(key);
        _actors[group].Tell(new UpdateGroupMembers(members));
        Persist();
        Reply(new GroupOpResult(true, null, group), add ? "members/add" : "members/remove", tx, group, group);
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

    private void Reply(GroupOpResult result, string action, string tx, string from, string? to)
    {
        Sender.Tell(result);   // REST Ask path; harmless when Told with NoSender (MQTT).
        _mqtt.Publish($"{_topics.Base}/bridge/response/group/{action}", JsonSerializer.Serialize(new
        {
            transaction = tx, status = result.Ok ? "ok" : "error", from, to = result.Name, error = result.Error,
        }));
    }
}
```

> Note: `FriendlyName.Slug` and `JsonDefaults.SnakeCase` already exist (`Matterhorn.Bridge` / `Matterhorn.Configuration`). `_topics.BridgeGroups()` is added in Task 7.

- [ ] **Step 5: Add `BridgeGroups()` to `MqttTopics`** (needed to compile)

In `src/Matterhorn/Mqtt/MqttTopics.cs`, after `BridgeDevices()` (~line 9):

```csharp
public string BridgeGroups() => $"{Base}/bridge/groups";
```

- [ ] **Step 6: Run to verify it passes**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~GroupsSupervisorTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Matterhorn/Groups/ src/Matterhorn/Mqtt/MqttTopics.cs src/Matterhorn.Test/Groups/GroupsSupervisorTests.cs
git commit -m "feat: GroupsSupervisor manager with persistence, read-model, pruning"
```

---

### Task 7: MQTT routing for groups (multi-segment actions + `<group>/set`)

Route `bridge/request/group/*` to the supervisor and `<group>/set` to the group path. Groups share the device `<name>/set` topic, so the gateway (which knows group names via `GroupNamesChanged`) disambiguates: a set for a group name is forwarded to the supervisor as `GroupSet`.

**Files:**
- Modify: `src/Matterhorn/Mqtt/MqttTopics.cs` (`TryParseRequest`, `RequestSubscription`)
- Modify: `src/Matterhorn/Mqtt/MqttCommandRouter.cs` (add `CommandTargets`, group actions)
- Modify: `src/Matterhorn/Bridge/MatterGatewayActor.cs` (`SetDevice` → forward group sets; consume `GroupNamesChanged`)
- Modify: `src/Matterhorn/Mqtt/MqttBridgeService.cs` (build `CommandTargets`; subscribe `bridge/request/#`)
- Test: `src/Matterhorn.Test/Mqtt/MqttTopicsTests.cs`, `src/Matterhorn.Test/Mqtt/MqttCommandRouterTests.cs`, `src/Matterhorn.Test/Bridge/MatterGatewayActorTests.cs`

**Interfaces:**
- Consumes: `GroupsSupervisor` messages (Task 6), `GroupNamesChanged` (Task 6).
- Produces:
  - `MqttTopics.TryParseRequest` now returns multi-segment actions (e.g. `"group/members/add"`).
  - `MqttTopics.RequestSubscription()` → `"{Base}/bridge/request/#"`.
  - `record CommandTargets(ICanTell Gateway, ICanTell Groups, ICanTell Scenes)`.
  - `MqttCommandRouter.Route(MqttTopics topics, string topic, string payload, CommandTargets targets)`.

- [ ] **Step 1: Write the failing `MqttTopics` test**

Add to `src/Matterhorn.Test/Mqtt/MqttTopicsTests.cs`:

```csharp
[Fact]
public void TryParseRequest_accepts_multi_segment_actions()
{
    var t = new MqttTopics("matterhorn");
    Assert.True(t.TryParseRequest("matterhorn/bridge/request/group/members/add", out var action));
    Assert.Equal("group/members/add", action);
}

[Fact]
public void TryParseRequest_still_accepts_single_segment_actions()
{
    var t = new MqttTopics("matterhorn");
    Assert.True(t.TryParseRequest("matterhorn/bridge/request/commission", out var action));
    Assert.Equal("commission", action);
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~TryParseRequest"`
Expected: FAIL — multi-segment currently rejected.

- [ ] **Step 3: Update `MqttTopics`**

In `src/Matterhorn/Mqtt/MqttTopics.cs`, change `RequestSubscription`:

```csharp
public string RequestSubscription() => $"{Base}/bridge/request/#";
```

and `TryParseRequest`'s final line — replace `return action.Length > 0 && !action.Contains('/');` with:

```csharp
        return action.Length > 0;
```

- [ ] **Step 4: Run to verify `MqttTopics` passes**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~TryParseRequest"`
Expected: PASS.

- [ ] **Step 5: Write the failing router test**

Replace the `gw.Ref` call sites in `MqttCommandRouterTests` with a `CommandTargets` and add group tests. Add a helper + tests:

```csharp
private CommandTargets Targets(out TestProbe gw, out TestProbe groups, out TestProbe scenes)
{
    gw = CreateTestProbe(); groups = CreateTestProbe(); scenes = CreateTestProbe();
    return new CommandTargets(gw.Ref, groups.Ref, scenes.Ref);
}

[Fact]
public void Group_add_request_forwards_CreateGroup()
{
    var targets = Targets(out _, out var groups, out _);
    MqttCommandRouter.Route(_topics, "matterhorn/bridge/request/group/add",
        """{"friendly_name":"living_room","transaction":"t1"}""", targets);

    var msg = groups.ExpectMsg<Matterhorn.Groups.CreateGroup>();
    Assert.Equal("living_room", msg.Name);
    Assert.Equal("t1", msg.Transaction);
}

[Fact]
public void Group_members_add_request_forwards_AddGroupMember()
{
    var targets = Targets(out _, out var groups, out _);
    MqttCommandRouter.Route(_topics, "matterhorn/bridge/request/group/members/add",
        """{"group":"living_room","device":"lamp","transaction":"t2"}""", targets);

    var msg = groups.ExpectMsg<Matterhorn.Groups.AddGroupMember>();
    Assert.Equal("living_room", msg.Group);
    Assert.Equal("lamp", msg.Device);
}
```

Update the existing tests in this file to pass a `CommandTargets` instead of `gw.Ref` (wrap the existing `gw` probe: `new CommandTargets(gw.Ref, CreateTestProbe(), CreateTestProbe())`).

- [ ] **Step 6: Run to verify it fails**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~MqttCommandRouterTests"`
Expected: FAIL — `CommandTargets` missing / signature changed.

- [ ] **Step 7: Update the router**

Rewrite `src/Matterhorn/Mqtt/MqttCommandRouter.cs`'s signature and add group routing:

```csharp
/// <summary>Recipients an inbound MQTT command can be routed to.</summary>
public record CommandTargets(ICanTell Gateway, ICanTell Groups, ICanTell Scenes);
```

Change `Route` to take `CommandTargets targets`; where it currently `Tell`s `gateway`, use `targets.Gateway`. In `RouteRequest`, add group cases before the existing `switch` default (dispatching to `targets.Groups`):

```csharp
if (action.StartsWith("group/"))
{
    RouteGroup(action["group/".Length..], root, targets.Groups);
    return;
}
```

and add:

```csharp
private static void RouteGroup(string sub, JsonElement root, ICanTell groups)
{
    string Tx() => root.TryGetProperty("transaction", out var t) ? t.GetString() ?? "" : "";
    string? Str(string p) => root.TryGetProperty(p, out var v) ? v.GetString() : null;
    switch (sub)
    {
        case "add" when Str("friendly_name") is { } n:
            groups.Tell(new Groups.CreateGroup(n, Array.Empty<string>(), Tx()), ActorRefs.NoSender); break;
        case "remove" when (Str("id") ?? Str("friendly_name")) is { } n:
            groups.Tell(new Groups.DeleteGroup(n, Tx()), ActorRefs.NoSender); break;
        case "rename" when Str("from") is { } f && Str("to") is { } t:
            groups.Tell(new Groups.RenameGroup(f, t, Tx()), ActorRefs.NoSender); break;
        case "members/add" when Str("group") is { } g && Str("device") is { } d:
            groups.Tell(new Groups.AddGroupMember(g, d, Tx()), ActorRefs.NoSender); break;
        case "members/remove" when Str("group") is { } g && Str("device") is { } d:
            groups.Tell(new Groups.RemoveGroupMember(g, d, Tx()), ActorRefs.NoSender); break;
    }
}
```

Add `using Matterhorn.Groups;` is unnecessary (fully-qualified above); keep existing usings.

- [ ] **Step 8: Run to verify router passes**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~MqttCommandRouterTests"`
Expected: PASS.

- [ ] **Step 9: Gateway disambiguates `<group>/set`**

Write the failing gateway test in `MatterGatewayActorTests`:

```csharp
[Fact]
public void SetDevice_for_a_group_name_forwards_GroupSet_to_the_groups_supervisor()
{
    var groups = CreateTestProbe();
    var gw = Sys.ActorOf(MatterGatewayActor.Props(
        new FakeMatterController(), new InMemoryMqttPublisher(), new MqttTopics("matterhorn")));
    gw.Tell(new RegisterGroups(groups.Ref));
    Sys.EventStream.Publish(new Matterhorn.Groups.GroupNamesChanged(new HashSet<string> { "living_room" }));

    var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"state":"OFF"}""")!;
    gw.Tell(new SetDevice("living_room", payload));

    groups.ExpectMsg<Matterhorn.Groups.GroupSet>(m => m.Name == "living_room" && m.Payload["state"].GetString() == "OFF");
}
```

Add the `RegisterGroups` message to `BridgeMessages.cs`:

```csharp
/// <summary>Startup wiring: hand the gateway the groups supervisor so it can forward group sets.</summary>
public record RegisterGroups(Akka.Actor.IActorRef Groups);
```

In `MatterGatewayActor`, add fields + handlers. Near the other fields (~line 33):

```csharp
private IActorRef? _groups;
private readonly HashSet<string> _groupNames = new();
```

In the constructor, subscribe + register + change `SetDevice`:

```csharp
Context.System.EventStream.Subscribe(Self, typeof(Matterhorn.Groups.GroupNamesChanged));
Receive<RegisterGroups>(r => _groups = r.Groups);
Receive<Matterhorn.Groups.GroupNamesChanged>(g => { _groupNames.Clear(); _groupNames.UnionWith(g.Names); });
```

Replace the existing `Receive<SetDevice>` body with:

```csharp
Receive<SetDevice>(s =>
{
    if (_byName.TryGetValue(s.FriendlyName, out var reg)) reg.Actor.Tell(new ApplySet(s.Payload));
    else if (_groupNames.Contains(s.FriendlyName)) _groups?.Tell(new Matterhorn.Groups.GroupSet(s.FriendlyName, s.Payload));
});
```

- [ ] **Step 10: Run to verify the gateway test passes**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~SetDevice_for_a_group_name"`
Expected: PASS.

- [ ] **Step 11: Wire `MqttBridgeService`**

In `src/Matterhorn/Mqtt/MqttBridgeService.cs`, resolve the supervisors and build `CommandTargets`. Change the constructor to also take nothing new (uses `registry`); in `ExecuteAsync` after `var gateway = ...`:

```csharp
var groups = await registry.GetAsync<Matterhorn.Groups.GroupsSupervisor>(ct);
var scenes = await registry.GetAsync<Matterhorn.Scenes.ScenesSupervisor>(ct); // added in Part B; register a placeholder now (see Task 8)
var targets = new CommandTargets(gateway, groups, scenes);
```

and change the `OnMessageReceived` route call to `MqttCommandRouter.Route(topics, ..., ..., targets)`.

> Part B registers `ScenesSupervisor`. To keep Part A building before Part B, Task 8 registers **both** supervisors in `Program.cs` (scenes uses the Part B type). If executing Part A strictly before Part B, temporarily pass `gateway` for the scenes slot and revisit in Task 8 — but the recommended order registers both supervisors together in Task 8.

- [ ] **Step 12: Commit**

```bash
git add src/Matterhorn/Mqtt/ src/Matterhorn/Bridge/ src/Matterhorn.Test/Mqtt/ src/Matterhorn.Test/Bridge/MatterGatewayActorTests.cs
git commit -m "feat: MQTT routing for groups + gateway group-set disambiguation"
```

---

### Task 8: Gateway rename-collision + Program.cs wiring + config

Complete the bidirectional name-collision guard (device rename must reject a group name) and wire the supervisors, stores, config, and MQTT subscriptions.

**Files:**
- Modify: `src/Matterhorn/Bridge/MatterGatewayActor.cs` (`DoRename`)
- Modify: `src/Matterhorn/Configuration/MatterhornConfig.cs`
- Modify: `src/Matterhorn/Program.cs`
- Test: `src/Matterhorn.Test/Bridge/MatterGatewayActorTests.cs`

**Interfaces:**
- Consumes: `GroupNamesChanged` (already subscribed in Task 7), `GroupsSupervisor.Props` (Task 6).
- Produces: `MatterhornConfig.GroupsFile` (default `data/groups.json`), `MatterhornConfig.ScenesFile` (default `data/scenes.json`, used in Part B).

- [ ] **Step 1: Write the failing rename-collision test**

```csharp
[Fact]
public void Rename_to_a_group_name_is_rejected_as_name_taken()
{
    var fake = new FakeMatterController();
    var gw = Sys.ActorOf(MatterGatewayActor.Props(
        fake, new InMemoryMqttPublisher(), new MqttTopics("matterhorn"), new InMemoryNameStore()));
    // The gateway learns group names from the supervisor's GroupNamesChanged broadcast.
    gw.Tell(new Matterhorn.Groups.GroupNamesChanged(new HashSet<string> { "living_room" }));
    fake.Emit(new NodeAdded(Light(5)));
    AwaitAssert(() => Assert.Single(gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result));

    var r = gw.Ask<RenameResult>(new RenameRequest("bulb_5_1", "living_room", "tx1")).Result;

    Assert.False(r.Ok);
    Assert.Equal("name_taken", r.Error);
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~Rename_to_a_group_name"`
Expected: FAIL — rename currently allows it.

- [ ] **Step 3: Guard `DoRename`**

In `MatterGatewayActor.DoRename`, after the existing `if (_byName.ContainsKey(slug)) return new RenameResult(false, "name_taken");` (~line 193):

```csharp
        if (_groupNames.Contains(slug)) return new RenameResult(false, "name_taken");
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~Rename_to_a_group_name"`
Expected: PASS.

- [ ] **Step 5: Add config**

In `MatterhornConfig.cs`, add `string GroupsFile, string ScenesFile` to the record and `FromConfiguration`:

```csharp
    GroupsFile: c["Storage:GroupsFile"] ?? "data/groups.json",
    ScenesFile: c["Storage:ScenesFile"] ?? "data/scenes.json",
```

(Add the two params to the record's parameter list after `NamesFile`.)

- [ ] **Step 6: Wire `Program.cs`**

Add store registrations after the `INameStore` line (~line 35):

```csharp
builder.Services.AddSingleton<IGroupStore>(new JsonGroupStore(cfg.GroupsFile));
builder.Services.AddSingleton<ISceneStore>(new JsonSceneStore(cfg.ScenesFile)); // Part B
```

In the `WithActors` block (~line 47), after registering the gateway + logbuffer, add:

```csharp
var groups = system.ActorOf(GroupsSupervisor.Props(
    sp.GetRequiredService<IGroupStore>(), gw, publisher, topics), "groups");
registry.Register<GroupsSupervisor>(groups);
var scenes = system.ActorOf(ScenesSupervisor.Props(              // Part B
    sp.GetRequiredService<ISceneStore>(), gw, publisher, topics), "scenes");
registry.Register<ScenesSupervisor>(scenes);
gw.Tell(new RegisterGroups(groups));
gw.Tell(new RegisterScenes(scenes));                            // Part B
```

Add `using Matterhorn.Groups;` and `using Matterhorn.Scenes;` to `Program.cs`. Add `GroupsRef`/`ScenesRef` DI wrappers mirroring `GatewayRef` for the REST controller (Task 9 / Part B Task 15):

```csharp
builder.Services.AddSingleton(sp => new GroupsRef(sp.GetRequiredService<ActorRegistry>().Get<GroupsSupervisor>()));
builder.Services.AddSingleton(sp => new ScenesRef(sp.GetRequiredService<ActorRegistry>().Get<ScenesSupervisor>()));
```

Create `src/Matterhorn/Groups/GroupsRef.cs`:

```csharp
using Akka.Actor;
namespace Matterhorn.Groups;
/// <summary>DI handle to the groups supervisor for the REST facade.</summary>
public sealed record GroupsRef(IActorRef Ref);
```

> `ScenesRef`, `RegisterScenes`, `ScenesSupervisor`, `ISceneStore`, `JsonSceneStore` come from Part B. If you are executing Part A strictly alone, stub them out or defer Steps that reference scenes until Part B; the recommended execution registers both together here.

- [ ] **Step 7: Build + full suite**

Run: `dotnet build` then `dotnet test src/Matterhorn.Test`
Expected: build OK, all green.

- [ ] **Step 8: Commit**

```bash
git add src/Matterhorn/ src/Matterhorn.Test/Bridge/MatterGatewayActorTests.cs
git commit -m "feat: wire groups supervisor + bidirectional name-collision guard + config"
```

---

### Task 9: REST — group endpoints (contract-first)

**Files:**
- Modify: `contracts/matterhorn.openapi.yaml`
- Modify: `src/Matterhorn/Api/MatterhornController.cs`
- Test: `src/Matterhorn.Test/Api/MatterhornControllerTests.cs`

**Interfaces:**
- Consumes: `GroupsRef` (Task 8), `GroupsSupervisor` messages (Task 6).
- Produces REST: `GET /api/groups`, `PUT /api/groups/{name}`, `DELETE /api/groups/{name}`, `PATCH /api/groups/{name}`, `PUT/DELETE /api/groups/{name}/members/{device}`, `POST /api/groups/{name}/rename`.

- [ ] **Step 1: Add paths + schemas to the OpenAPI contract**

In `contracts/matterhorn.openapi.yaml`, add under `paths:`:

```yaml
  /api/groups:
    get:
      operationId: listGroups
      summary: List all groups.
      tags: [groups]
      responses:
        '200':
          description: Every group and its member friendly names.
          content: { application/json: { schema: { type: array, items: { $ref: '#/components/schemas/Group' } } } }
  /api/groups/{name}:
    put:
      operationId: putGroup
      summary: Create or replace a group.
      tags: [groups]
      parameters: [ { $ref: '#/components/parameters/FriendlyName' } ]
      requestBody:
        required: false
        content: { application/json: { schema: { $ref: '#/components/schemas/GroupMembers' } } }
      responses:
        '200': { description: Replaced. }
        '201': { description: Created. }
        '400': { description: The requested name is not usable. }
        '409': { description: The name collides with an existing device. }
    patch:
      operationId: setGroupState
      summary: Fan out a partial state change to a group's members.
      tags: [groups]
      parameters: [ { $ref: '#/components/parameters/FriendlyName' } ]
      requestBody:
        required: true
        content: { application/json: { schema: { $ref: '#/components/schemas/SetRequest' } } }
      responses:
        '202': { description: Accepted; applied asynchronously. }
    delete:
      operationId: deleteGroup
      summary: Delete a group.
      tags: [groups]
      parameters: [ { $ref: '#/components/parameters/FriendlyName' } ]
      responses:
        '202': { description: Deleted. }
        '404': { description: No group with that name. }
  /api/groups/{name}/members/{device}:
    put:
      operationId: addGroupMember
      summary: Add a device to a group.
      tags: [groups]
      parameters:
        - { $ref: '#/components/parameters/FriendlyName' }
        - { name: device, in: path, required: true, schema: { type: string } }
      responses:
        '200': { description: Added. }
        '404': { description: No such group or device. }
    delete:
      operationId: removeGroupMember
      summary: Remove a device from a group.
      tags: [groups]
      parameters:
        - { $ref: '#/components/parameters/FriendlyName' }
        - { name: device, in: path, required: true, schema: { type: string } }
      responses:
        '200': { description: Removed. }
        '404': { description: No group with that name. }
  /api/groups/{name}/rename:
    post:
      operationId: renameGroup
      summary: Rename a group.
      tags: [groups]
      parameters: [ { $ref: '#/components/parameters/FriendlyName' } ]
      requestBody:
        required: true
        content: { application/json: { schema: { $ref: '#/components/schemas/RenameRequest' } } }
      responses:
        '200': { description: Renamed. }
        '400': { description: Not usable. }
        '404': { description: No group with that name. }
        '409': { description: Target name taken. }
```

and under `components: schemas:`:

```yaml
    Group:
      type: object
      required: [friendly_name, members]
      properties:
        friendly_name: { type: string }
        members: { type: array, items: { type: string } }
    GroupMembers:
      type: object
      properties:
        members: { type: array, items: { type: string }, description: 'Initial member friendly names.' }
```

- [ ] **Step 2: Build to regenerate the contract base**

Run: `dotnet build`
Expected: build succeeds; new abstract methods (`ListGroups`, `PutGroup`, `SetGroupState`, `DeleteGroup`, `AddGroupMember`, `RemoveGroupMember`, `RenameGroup`) now exist on `Gen.MatterhornControllerBase` — the app **won't run** until implemented, but it compiles the generated partial.

> If the build reports the controller is abstract/unimplemented, that's expected until Step 4. It still generates `ApiContract.g.cs`.

- [ ] **Step 3: Write the failing controller test**

Add to `MatterhornControllerTests` (follow the file's existing harness — it constructs the controller against a probe/registry; mirror the device tests there). Example for list + create:

```csharp
[Fact]
public async Task PutGroup_creates_and_returns_201()
{
    // Arrange: controller wired to a probe standing in for GroupsSupervisor that replies GroupOpResult.
    // (Mirror the existing device-controller test setup in this file.)
    // Act
    var result = await Controller.PutGroup("living_room", new Gen.GroupMembers { Members = new List<string> { "lamp" } });
    // Assert
    Assert.IsType<CreatedResult>(result);
}
```

> Match whatever probe/DI pattern `MatterhornControllerTests` already uses for `GatewayRef`; add a `GroupsRef` pointing at a `TestProbe` that auto-replies `new GroupOpResult(true, null, "living_room")`.

- [ ] **Step 4: Implement the abstract methods**

In `MatterhornController.cs`, add a `GroupsRef` constructor param and implement. Constructor:

```csharp
public sealed class MatterhornController(GatewayRef gateway, Matterhorn.Groups.GroupsRef groups)
    : Gen.MatterhornControllerBase
{
    private IActorRef Grp => groups.Ref;
```

Methods:

```csharp
public override async Task<ActionResult<ICollection<Gen.Group>>> ListGroups()
{
    var views = await Grp.Ask<IReadOnlyList<Matterhorn.Groups.GroupView>>(new Matterhorn.Groups.GetGroups(), Timeout);
    return views.Select(v => new Gen.Group { Friendly_name = v.FriendlyName, Members = v.Members.ToList() }).ToList();
}

public override async Task<IActionResult> PutGroup(string name, Gen.GroupMembers body)
{
    var tx = Guid.NewGuid().ToString("N");
    var r = await Grp.Ask<Matterhorn.Groups.GroupOpResult>(
        new Matterhorn.Groups.CreateGroup(name, body?.Members?.ToList() ?? new(), tx), Timeout);
    return r switch
    {
        { Ok: true } => Created($"/api/groups/{r.Name}", null),
        { Error: "invalid_name" } => BadRequest(),
        { Error: "collides_with_device" } => Conflict(),
        _ => BadRequest(),
    };
}

public override Task<IActionResult> SetGroupState(string name, Gen.SetRequest body)
{
    var payload = SetRequestToPayload(body);   // extract the existing PATCH-device mapping into this helper
    if (payload.Count > 0) Grp.Tell(new Matterhorn.Groups.GroupSet(name, payload));
    return Task.FromResult<IActionResult>(Accepted());
}

public override async Task<IActionResult> DeleteGroup(string name)
{
    var r = await Grp.Ask<Matterhorn.Groups.GroupOpResult>(new Matterhorn.Groups.DeleteGroup(name, Guid.NewGuid().ToString("N")), Timeout);
    return r.Ok ? Accepted() : NotFound();
}

public override async Task<IActionResult> AddGroupMember(string name, string device)
{
    var r = await Grp.Ask<Matterhorn.Groups.GroupOpResult>(new Matterhorn.Groups.AddGroupMember(name, device, Guid.NewGuid().ToString("N")), Timeout);
    return r.Ok ? Ok() : NotFound();
}

public override async Task<IActionResult> RemoveGroupMember(string name, string device)
{
    var r = await Grp.Ask<Matterhorn.Groups.GroupOpResult>(new Matterhorn.Groups.RemoveGroupMember(name, device, Guid.NewGuid().ToString("N")), Timeout);
    return r.Ok ? Ok() : NotFound();
}

public override async Task<IActionResult> RenameGroup(string name, Gen.RenameRequest body)
{
    var r = await Grp.Ask<Matterhorn.Groups.GroupOpResult>(new Matterhorn.Groups.RenameGroup(name, body.To, Guid.NewGuid().ToString("N")), Timeout);
    return r switch
    {
        { Ok: true } => Ok(),
        { Error: "not_found" } => NotFound(),
        { Error: "name_taken" } or { Error: "collides_with_device" } => Conflict(),
        _ => BadRequest(),
    };
}
```

Refactor the existing `SetDeviceState` body into a reusable `private static Dictionary<string, JsonElement> SetRequestToPayload(Gen.SetRequest body)` and call it from both `SetDeviceState` and `SetGroupState` (DRY).

- [ ] **Step 5: Run tests + build**

Run: `dotnet build` then `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~MatterhornControllerTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add contracts/matterhorn.openapi.yaml src/Matterhorn/Api/MatterhornController.cs src/Matterhorn.Test/Api/MatterhornControllerTests.cs
git commit -m "feat: REST group endpoints (contract-first)"
```

> After editing `contracts/matterhorn.openapi.yaml`, also copy/verify the served copy at `src/Matterhorn/openapi/matterhorn.yaml` if it is a separate file (see `Program.cs` line ~74). If the build already sources the served file from `contracts/`, skip.

---

### Task 10: Dashboard — groups UI

Add a Groups section to `wwwroot/index.html`: create a group, list groups as cards with a master on/off that `PATCH`es, member add/remove, kebab rename/delete. Frontend is verified manually against the fake controller.

**Files:**
- Modify: `src/Matterhorn/wwwroot/index.html`
- Modify: `src/Matterhorn/Api/ServerSentEvents.cs` (subscribe `GroupListChanged`; forward a `groups` frame)

**Interfaces:**
- Consumes: REST `/api/groups*` (Task 9), SSE frames `state` (group echo reuses `DeviceStateChanged`) + new `groups`.
- Produces: SSE `{"type":"groups"}` frame.

- [ ] **Step 1: Add the SSE `groups` frame**

In `ServerSentEvents.cs`, subscribe the bridge to `GroupListChanged` (add near line 29):

```csharp
system.EventStream.Subscribe(bridge, typeof(Matterhorn.Groups.GroupListChanged));
```

In `SseBridgeActor`'s constructor, add:

```csharp
Receive<Matterhorn.Groups.GroupListChanged>(_ => writer.TryWrite("{\"type\":\"groups\"}"));
```

- [ ] **Step 2: Manual test scaffold — run the stack**

Run: `docker compose up --build` (fake controller + demo devices), open `http://localhost:16090`.
Expected: dashboard loads with demo device stations (baseline before UI changes).

- [ ] **Step 3: Add the Groups UI**

In `index.html`, add a `<section id="groups">` region above `<main id="grid">` (or a dedicated panel), plus JS:
- `loadGroups()` → `GET /api/groups`, render one card per group with: name, member chips, a master switch (`PATCH /api/groups/<name>` `{state:ON|OFF}`), an "＋ add device" control (`PUT /api/groups/<name>/members/<device>`), a member remove ✕ (`DELETE …/members/<device>`), and a kebab (rename via `POST …/rename`, delete via `DELETE /api/groups/<name>`).
- A "New group" form → `PUT /api/groups/<name>` with `{members:[]}`.
- In the SSE `onmessage` handler, add `else if (m.type==='groups'){ loadGroups(); }` and let the existing `m.type==='state'` path update a group card when `m.device` is a known group name.
- Call `loadGroups()` from `loadAll()` and on connect.

Reuse existing helpers (`api`, `patch`, `toast`, `esc`, `cssId`, kebab menu/modal patterns). Match the "station" card styling.

- [ ] **Step 4: Manual verification**

With `docker compose up` running:
1. Create a group `living_room`; confirm it appears as a card and `matterhorn/bridge/groups` is retained (check EMQX dashboard `http://localhost:16083`, or `mqtt` topic).
2. Add two demo devices as members; toggle the group master switch OFF; confirm both devices turn off (their station switches flip) and `matterhorn/living_room` shows `{"state":"OFF"}` retained.
3. Rename and delete the group; confirm the card and retained topics update.
4. Reload the page; confirm the group persists (loaded from `groups.json`).

- [ ] **Step 5: Commit**

```bash
git add src/Matterhorn/wwwroot/index.html src/Matterhorn/Api/ServerSentEvents.cs
git commit -m "feat: dashboard groups UI + SSE groups frame"
```

**Part A is now shippable: groups work over MQTT, REST, and the dashboard.**

---

## Part B — Scenes (end-to-end: MQTT + REST + dashboard)

Depends on Phase 0 (Tasks 1–3) and reuses the supervisor/read-model pattern from Part A.

### Task 11: `RouteGetState` — read a device's current state by key

Scene snapshot needs each member's current state. Add the by-key read to the gateway (mirrors Task 1).

**Files:**
- Modify: `src/Matterhorn/Bridge/BridgeMessages.cs`
- Modify: `src/Matterhorn/Bridge/MatterGatewayActor.cs`
- Test: `src/Matterhorn.Test/Bridge/MatterGatewayActorTests.cs`

**Interfaces:**
- Produces: `record RouteGetState((ulong NodeId, ushort Endpoint) Key)` — `Ask`ing the gateway forwards `GetState` to the endpoint; the reply is a `DeviceStateSnapshot` (`Found:false` if the key is unknown).

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task RouteGetState_returns_the_device_snapshot()
{
    var fake = new FakeMatterController();
    var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, new InMemoryMqttPublisher(), new MqttTopics("matterhorn")));
    fake.Emit(new NodeAdded(Light(9)));
    fake.Emit(new AttributeChanged(new AttributeReading(9, 1, MatterClusters.OnOff, 0, JsonDocument.Parse("true").RootElement)));
    AwaitAssert(() => Assert.NotEmpty(gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result));

    var snap = await gw.Ask<DeviceStateSnapshot>(new RouteGetState((9UL, (ushort)1)), TimeSpan.FromSeconds(2));

    Assert.True(snap.Found);
    Assert.Equal("ON", snap.State!["state"]!.ToString());
}

[Fact]
public async Task RouteGetState_for_unknown_key_returns_not_found()
{
    var gw = Sys.ActorOf(MatterGatewayActor.Props(new FakeMatterController(), new InMemoryMqttPublisher(), new MqttTopics("matterhorn")));
    var snap = await gw.Ask<DeviceStateSnapshot>(new RouteGetState((404UL, (ushort)1)), TimeSpan.FromSeconds(2));
    Assert.False(snap.Found);
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~RouteGetState"`
Expected: FAIL.

- [ ] **Step 3: Add the message + handler**

Append to `BridgeMessages.cs`:

```csharp
/// <summary>Read a device's current state by stable key; replied to with <see cref="Matterhorn.Devices.DeviceStateSnapshot"/>
/// (Found:false if no endpoint is registered under the key). Used by scene snapshot capture.</summary>
public record RouteGetState((ulong NodeId, ushort Endpoint) Key);
```

In `MatterGatewayActor`'s constructor, after `Receive<RouteSet>`:

```csharp
Receive<RouteGetState>(r =>
{
    if (_byKey.TryGetValue(r.Key, out var reg)) reg.Actor.Forward(new GetState());
    else Sender.Tell(new DeviceStateSnapshot(false, null));
});
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~RouteGetState"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Matterhorn/Bridge/ src/Matterhorn.Test/Bridge/MatterGatewayActorTests.cs
git commit -m "feat: gateway RouteGetState to read device state by key"
```

---

### Task 12: `ISceneStore` + `JsonSceneStore`

**Files:**
- Create: `src/Matterhorn/Persistence/ISceneStore.cs`
- Create: `src/Matterhorn/Persistence/JsonSceneStore.cs`
- Test: `src/Matterhorn.Test/Persistence/JsonSceneStoreTests.cs`

**Interfaces:**
- Produces:
  - `interface ISceneStore { IReadOnlyDictionary<string, IReadOnlyDictionary<(ulong,ushort), IReadOnlyDictionary<string, JsonElement>>> Load(); void Save(...same shape...); }`
  - `sealed class JsonSceneStore(string path) : ISceneStore` — persists `{ "<scene>": { "5_1": { "state": "ON", ... } } }`.

- [ ] **Step 1: Write the failing test**

Create `src/Matterhorn.Test/Persistence/JsonSceneStoreTests.cs`:

```csharp
using System.Text.Json;
using Matterhorn.Persistence;

namespace Matterhorn.Test.Persistence;

public class JsonSceneStoreTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"scenes-{Guid.NewGuid():N}.json");
    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement.Clone();

    [Fact]
    public void Load_missing_file_returns_empty() => Assert.Empty(new JsonSceneStore(TempFile()).Load());

    [Fact]
    public void Save_then_load_round_trips_values()
    {
        var path = TempFile();
        try
        {
            new JsonSceneStore(path).Save(new Dictionary<string, IReadOnlyDictionary<(ulong, ushort), IReadOnlyDictionary<string, JsonElement>>>
            {
                ["movie"] = new Dictionary<(ulong, ushort), IReadOnlyDictionary<string, JsonElement>>
                {
                    [(5, 1)] = new Dictionary<string, JsonElement> { ["state"] = J("\"ON\""), ["brightness"] = J("40") },
                },
            });

            var loaded = new JsonSceneStore(path).Load();
            Assert.Equal("ON", loaded["movie"][(5, 1)]["state"].GetString());
            Assert.Equal(40, loaded["movie"][(5, 1)]["brightness"].GetInt32());
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_corrupt_file_returns_empty()
    {
        var path = TempFile();
        try { File.WriteAllText(path, "nonsense"); Assert.Empty(new JsonSceneStore(path).Load()); }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~JsonSceneStoreTests"`
Expected: FAIL.

- [ ] **Step 3: Implement**

Create `src/Matterhorn/Persistence/ISceneStore.cs`:

```csharp
using System.Text.Json;

namespace Matterhorn.Persistence;

/// <summary>Persists scenes: scene name → (stable device key → target property payload).</summary>
public interface ISceneStore
{
    IReadOnlyDictionary<string, IReadOnlyDictionary<(ulong NodeId, ushort Endpoint), IReadOnlyDictionary<string, JsonElement>>> Load();
    void Save(IReadOnlyDictionary<string, IReadOnlyDictionary<(ulong NodeId, ushort Endpoint), IReadOnlyDictionary<string, JsonElement>>> scenes);
}
```

Create `src/Matterhorn/Persistence/JsonSceneStore.cs`:

```csharp
using System.Text.Json;

namespace Matterhorn.Persistence;

/// <summary>Stores scenes as { "<scene>": { "<node>_<endpoint>": { "<prop>": value, ... } } }.
/// Missing/corrupt file loads empty; writes swap atomically via a temp file.</summary>
public sealed class JsonSceneStore(string path) : ISceneStore
{
    public IReadOnlyDictionary<string, IReadOnlyDictionary<(ulong, ushort), IReadOnlyDictionary<string, JsonElement>>> Load()
    {
        var result = new Dictionary<string, IReadOnlyDictionary<(ulong, ushort), IReadOnlyDictionary<string, JsonElement>>>();
        if (!File.Exists(path)) return result;
        Dictionary<string, Dictionary<string, Dictionary<string, JsonElement>>>? raw;
        try { raw = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Dictionary<string, JsonElement>>>>(File.ReadAllText(path)); }
        catch (JsonException) { return result; }
        if (raw is null) return result;
        foreach (var (scene, members) in raw)
        {
            var map = new Dictionary<(ulong, ushort), IReadOnlyDictionary<string, JsonElement>>();
            foreach (var (keyStr, props) in members)
                if (DeviceKeys.TryParse(keyStr, out var key)) map[key] = props;
            result[scene] = map;
        }
        return result;
    }

    public void Save(IReadOnlyDictionary<string, IReadOnlyDictionary<(ulong, ushort), IReadOnlyDictionary<string, JsonElement>>> scenes)
    {
        var raw = scenes.ToDictionary(
            s => s.Key,
            s => s.Value.ToDictionary(m => DeviceKeys.Format(m.Key), m => m.Value));
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(raw, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~JsonSceneStoreTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Matterhorn/Persistence/ISceneStore.cs src/Matterhorn/Persistence/JsonSceneStore.cs src/Matterhorn.Test/Persistence/JsonSceneStoreTests.cs
git commit -m "feat: JsonSceneStore scene persistence"
```

---

### Task 13: Scene messages + `SceneActor` (recall + explicit store)

**Files:**
- Create: `src/Matterhorn/Scenes/SceneMessages.cs`
- Create: `src/Matterhorn/Scenes/SceneActor.cs`
- Test: `src/Matterhorn.Test/Scenes/SceneActorTests.cs`

**Interfaces:**
- Consumes: `RouteSet` (Task 1), `RouteGetState` (Task 11).
- Produces:
  - `record RecallScene` — tell the entity to fan out its stored map.
  - `record UpdateSceneValues(IReadOnlyDictionary<(ulong NodeId, ushort Endpoint), IReadOnlyDictionary<string, JsonElement>> Values)` — supervisor pushes a new stored snapshot.
  - `SceneActor.Props(string name, IReadOnlyDictionary<(ulong,ushort), IReadOnlyDictionary<string,JsonElement>> values, IActorRef gateway)`.

- [ ] **Step 1: Write the failing test**

Create `src/Matterhorn.Test/Scenes/SceneActorTests.cs`:

```csharp
using System.Text.Json;
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
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~SceneActorTests"`
Expected: FAIL — `SceneActor` missing.

- [ ] **Step 3: Add messages**

Create `src/Matterhorn/Scenes/SceneMessages.cs`:

```csharp
using System.Text.Json;

namespace Matterhorn.Scenes;

/// <summary>Tell a <see cref="SceneActor"/> to fan out its stored values.</summary>
public record RecallScene;

/// <summary>Supervisor pushes a scene's new stored snapshot to its entity.</summary>
public record UpdateSceneValues(
    IReadOnlyDictionary<(ulong NodeId, ushort Endpoint), IReadOnlyDictionary<string, JsonElement>> Values);
```

- [ ] **Step 4: Implement `SceneActor`**

Create `src/Matterhorn/Scenes/SceneActor.cs`:

```csharp
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
```

> `name` is carried for symmetry/logging even though recall keys on the stored map; keep the parameter.

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~SceneActorTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Matterhorn/Scenes/ src/Matterhorn.Test/Scenes/SceneActorTests.cs
git commit -m "feat: SceneActor recall fan-out"
```

---

### Task 14: `ScenesSupervisor` (store, explicit store, snapshot capture, recall, prune)

**Files:**
- Modify: `src/Matterhorn/Scenes/SceneMessages.cs`
- Create: `src/Matterhorn/Scenes/ScenesSupervisor.cs`
- Create: `src/Matterhorn/Scenes/ScenesRef.cs`
- Modify: `src/Matterhorn/Mqtt/MqttTopics.cs` (`BridgeScenes()`)
- Test: `src/Matterhorn.Test/Scenes/ScenesSupervisorTests.cs`

**Interfaces:**
- Consumes: `ISceneStore`, `RouteGetState` (Task 11), `DeviceRegistered`/`DeviceRemoved` (Task 2), gateway `IActorRef`, `IMqttPublisher`, `MqttTopics`.
- Produces:
  - `record StoreScene(string Name, IReadOnlyList<string> Devices, IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>>? ExplicitState, string Transaction)` — snapshot when `ExplicitState` is null, else use it.
  - `record DeleteScene(string Name, string Transaction)`, `record RenameScene(string From, string To, string Transaction)`, `record RecallSceneByName(string Name, string Transaction)`.
  - `record SceneOpResult(bool Ok, string? Error, string? Name = null)` (Error ∈ `invalid_name|not_found|name_taken`).
  - `record GetScenes` → `IReadOnlyList<SceneView>`; `record SceneView(string FriendlyName, IReadOnlyList<string> Members)`.
  - `record SceneListChanged` — EventStream (SSE).
  - `record RegisterScenes(Akka.Actor.IActorRef Scenes)` — added to `BridgeMessages.cs` (gateway holds it; not used for routing but kept symmetric / future use).
  - `ScenesSupervisor.Props(ISceneStore store, IActorRef gateway, IMqttPublisher mqtt, MqttTopics topics)`.
  - Settable-property filter: `state, brightness, color_temp, hue, saturation` (the dashboard `SETTABLE` set + color).

- [ ] **Step 1: Add message types + `BridgeScenes()` + `RegisterScenes`**

Append to `src/Matterhorn/Scenes/SceneMessages.cs`:

```csharp
public record StoreScene(string Name, IReadOnlyList<string> Devices,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>>? ExplicitState, string Transaction);
public record DeleteScene(string Name, string Transaction);
public record RenameScene(string From, string To, string Transaction);
public record RecallSceneByName(string Name, string Transaction);
public record SceneOpResult(bool Ok, string? Error, string? Name = null);
public record GetScenes;
public record SceneView(string FriendlyName, IReadOnlyList<string> Members);
public record SceneListChanged;
```

In `MqttTopics.cs`, after `BridgeGroups()`:

```csharp
public string BridgeScenes() => $"{Base}/bridge/scenes";
```

In `BridgeMessages.cs`:

```csharp
/// <summary>Startup wiring: hand the gateway the scenes supervisor (symmetry with RegisterGroups).</summary>
public record RegisterScenes(Akka.Actor.IActorRef Scenes);
```

Add a no-op handler in the gateway constructor so the message doesn't dead-letter: `Receive<RegisterScenes>(_ => { });`

- [ ] **Step 2: Write the failing tests**

Create `src/Matterhorn.Test/Scenes/ScenesSupervisorTests.cs`:

```csharp
using System.Text.Json;
using Akka.Actor;
using Akka.TestKit.Xunit2;
using Matterhorn.Bridge;
using Matterhorn.Devices;
using Matterhorn.Mqtt;
using Matterhorn.Persistence;
using Matterhorn.Scenes;
using Matterhorn.Test.Mqtt;

namespace Matterhorn.Test.Scenes;

public class ScenesSupervisorTests : TestKit
{
    private sealed class MemSceneStore : ISceneStore
    {
        public Dictionary<string, IReadOnlyDictionary<(ulong, ushort), IReadOnlyDictionary<string, JsonElement>>> Scenes { get; } = new();
        public IReadOnlyDictionary<string, IReadOnlyDictionary<(ulong, ushort), IReadOnlyDictionary<string, JsonElement>>> Load() => Scenes;
        public void Save(IReadOnlyDictionary<string, IReadOnlyDictionary<(ulong, ushort), IReadOnlyDictionary<string, JsonElement>>> s)
        { Scenes.Clear(); foreach (var (k, v) in s) Scenes[k] = v; }
    }

    // A gateway stand-in that answers RouteGetState with a canned snapshot (settable + a read-only prop).
    private sealed class FakeGateway : ReceiveActor
    {
        public FakeGateway()
        {
            Receive<RouteGetState>(r => Sender.Tell(new DeviceStateSnapshot(true, new Dictionary<string, object?>
            {
                ["state"] = "ON", ["brightness"] = 40, ["temperature"] = 21.5,   // temperature must be filtered out
            })));
        }
    }

    private (IActorRef sup, IActorRef gw, InMemoryMqttPublisher mqtt, MemSceneStore store) NewSupervisor()
    {
        var gw = Sys.ActorOf(Props.Create(() => new FakeGateway()));
        var mqtt = new InMemoryMqttPublisher();
        var store = new MemSceneStore();
        var sup = Sys.ActorOf(ScenesSupervisor.Props(store, gw, mqtt, new MqttTopics("matterhorn")));
        Sys.EventStream.Publish(new DeviceRegistered((5UL, 1), "lamp"));
        AwaitAssert(() => { sup.Tell(new GetScenes()); ExpectMsg<IReadOnlyList<SceneView>>(); });
        return (sup, gw, mqtt, store);
    }

    [Fact]
    public void StoreScene_snapshot_keeps_only_settable_properties()
    {
        var (sup, _, _, store) = NewSupervisor();
        var r = sup.Ask<SceneOpResult>(new StoreScene("movie", new[] { "lamp" }, null, "t1")).Result;

        Assert.True(r.Ok);
        var props = store.Scenes["movie"][(5, 1)];
        Assert.Equal("ON", props["state"].GetString());
        Assert.Equal(40, props["brightness"].GetInt32());
        Assert.False(props.ContainsKey("temperature"));   // read-only filtered out
    }

    [Fact]
    public void StoreScene_with_explicit_state_stores_it_without_snapshot()
    {
        var (sup, _, _, store) = NewSupervisor();
        var explicitState = new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>
        {
            ["lamp"] = new Dictionary<string, JsonElement> { ["state"] = JsonDocument.Parse("\"OFF\"").RootElement.Clone() },
        };
        sup.Ask<SceneOpResult>(new StoreScene("night", new[] { "lamp" }, explicitState, "t1")).Wait();

        Assert.Equal("OFF", store.Scenes["night"][(5, 1)]["state"].GetString());
    }

    [Fact]
    public void RecallSceneByName_recalls_the_entity()
    {
        var (sup, _, _, _) = NewSupervisor();
        sup.Ask<SceneOpResult>(new StoreScene("movie", new[] { "lamp" }, null, "t1")).Wait();
        var r = sup.Ask<SceneOpResult>(new RecallSceneByName("movie", "t2")).Result;
        Assert.True(r.Ok);
    }

    [Fact]
    public void RecallSceneByName_unknown_scene_is_not_found()
    {
        var (sup, _, _, _) = NewSupervisor();
        var r = sup.Ask<SceneOpResult>(new RecallSceneByName("ghost", "t1")).Result;
        Assert.False(r.Ok);
        Assert.Equal("not_found", r.Error);
    }

    [Fact]
    public void DeviceRemoved_prunes_the_member_from_every_scene()
    {
        var (sup, _, _, store) = NewSupervisor();
        sup.Ask<SceneOpResult>(new StoreScene("movie", new[] { "lamp" }, null, "t1")).Wait();

        Sys.EventStream.Publish(new DeviceRemoved((5UL, 1)));

        AwaitAssert(() => Assert.False(store.Scenes["movie"].ContainsKey((5, 1))));
    }
}
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~ScenesSupervisorTests"`
Expected: FAIL — `ScenesSupervisor` missing.

- [ ] **Step 4: Implement `ScenesSupervisor`**

Create `src/Matterhorn/Scenes/ScenesSupervisor.cs`:

```csharp
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
        Context.ActorOf(SceneActor.Props(name, values, _gateway), $"scene-{name}");

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
        if (_actors.TryGetValue(slug, out var existing)) existing.Tell(new UpdateSceneValues(values));
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
            if (values.Remove(d.Key)) { _actors[scene].Tell(new UpdateSceneValues(values)); touched = true; }
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
```

Create `src/Matterhorn/Scenes/ScenesRef.cs`:

```csharp
using Akka.Actor;
namespace Matterhorn.Scenes;
/// <summary>DI handle to the scenes supervisor for the REST facade.</summary>
public sealed record ScenesRef(IActorRef Ref);
```

> The snapshot converts stored state values (`object?` from `DeviceStateSnapshot.State`) to `JsonElement` via `JsonSerializer.SerializeToElement`. This matches how `MatterEndpointActor` holds state as `Dictionary<string, object?>`.

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~ScenesSupervisorTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Matterhorn/Scenes/ src/Matterhorn/Mqtt/MqttTopics.cs src/Matterhorn/Bridge/BridgeMessages.cs src/Matterhorn/Bridge/MatterGatewayActor.cs src/Matterhorn.Test/Scenes/ScenesSupervisorTests.cs
git commit -m "feat: ScenesSupervisor with snapshot capture, recall, pruning"
```

---

### Task 15: MQTT + REST + wiring for scenes

**Files:**
- Modify: `src/Matterhorn/Mqtt/MqttCommandRouter.cs` (`scene/*` actions)
- Modify: `contracts/matterhorn.openapi.yaml` (+ scene paths/schemas)
- Modify: `src/Matterhorn/Api/MatterhornController.cs`
- Modify: `src/Matterhorn/Program.cs` (already registers scenes in Task 8 Step 6 — verify)
- Test: `src/Matterhorn.Test/Mqtt/MqttCommandRouterTests.cs`, `src/Matterhorn.Test/Api/MatterhornControllerTests.cs`

**Interfaces:**
- Consumes: `ScenesSupervisor` messages (Task 14), `ScenesRef` (Task 14).
- Produces REST: `GET /api/scenes`, `PUT /api/scenes/{name}`, `DELETE /api/scenes/{name}`, `POST /api/scenes/{name}/recall`, `POST /api/scenes/{name}/rename`.

- [ ] **Step 1: Write the failing router test**

Add to `MqttCommandRouterTests`:

```csharp
[Fact]
public void Scene_store_request_forwards_StoreScene()
{
    var targets = Targets(out _, out _, out var scenes);
    MqttCommandRouter.Route(_topics, "matterhorn/bridge/request/scene/store",
        """{"name":"movie","devices":["lamp","strip"],"transaction":"t1"}""", targets);

    var msg = scenes.ExpectMsg<Matterhorn.Scenes.StoreScene>();
    Assert.Equal("movie", msg.Name);
    Assert.Equal(new[] { "lamp", "strip" }, msg.Devices);
    Assert.Null(msg.ExplicitState);
}

[Fact]
public void Scene_recall_request_forwards_RecallSceneByName()
{
    var targets = Targets(out _, out _, out var scenes);
    MqttCommandRouter.Route(_topics, "matterhorn/bridge/request/scene/recall",
        """{"name":"movie","transaction":"t2"}""", targets);

    scenes.ExpectMsg<Matterhorn.Scenes.RecallSceneByName>(m => m.Name == "movie");
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~Scene_store_request|FullyQualifiedName~Scene_recall_request"`
Expected: FAIL.

- [ ] **Step 3: Add scene routing to the router**

In `MqttCommandRouter.RouteRequest`, before the single-segment `switch`, after the group branch:

```csharp
if (action.StartsWith("scene/"))
{
    RouteScene(action["scene/".Length..], root, targets.Scenes);
    return;
}
```

Add:

```csharp
private static void RouteScene(string sub, JsonElement root, ICanTell scenes)
{
    string Tx() => root.TryGetProperty("transaction", out var t) ? t.GetString() ?? "" : "";
    string? Str(string p) => root.TryGetProperty(p, out var v) ? v.GetString() : null;
    switch (sub)
    {
        case "store" when Str("name") is { } n:
            var devices = root.TryGetProperty("devices", out var d) && d.ValueKind == JsonValueKind.Array
                ? d.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList()
                : new List<string>();
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>>? explicitState = null;
            if (root.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.Object)
                explicitState = st.EnumerateObject().ToDictionary(
                    p => p.Name,
                    p => (IReadOnlyDictionary<string, JsonElement>)p.Value.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone()));
            scenes.Tell(new Scenes.StoreScene(n, devices, explicitState, Tx()), ActorRefs.NoSender); break;
        case "recall" when Str("name") is { } n:
            scenes.Tell(new Scenes.RecallSceneByName(n, Tx()), ActorRefs.NoSender); break;
        case "remove" when Str("name") is { } n:
            scenes.Tell(new Scenes.DeleteScene(n, Tx()), ActorRefs.NoSender); break;
        case "rename" when Str("from") is { } f && Str("to") is { } t:
            scenes.Tell(new Scenes.RenameScene(f, t, Tx()), ActorRefs.NoSender); break;
    }
}
```

- [ ] **Step 4: Run to verify router passes**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~MqttCommandRouterTests"`
Expected: PASS.

- [ ] **Step 5: Add scene REST to the contract**

In `contracts/matterhorn.openapi.yaml`, add paths:

```yaml
  /api/scenes:
    get:
      operationId: listScenes
      summary: List all scenes.
      tags: [scenes]
      responses:
        '200': { description: Every scene and its member friendly names.,
          content: { application/json: { schema: { type: array, items: { $ref: '#/components/schemas/Scene' } } } } }
  /api/scenes/{name}:
    put:
      operationId: putScene
      summary: Store (create-or-replace) a scene — snapshot members or use explicit state.
      tags: [scenes]
      parameters: [ { $ref: '#/components/parameters/FriendlyName' } ]
      requestBody:
        required: false
        content: { application/json: { schema: { $ref: '#/components/schemas/StoreSceneRequest' } } }
      responses:
        '200': { description: Replaced. }
        '201': { description: Created. }
        '400': { description: Not usable. }
    delete:
      operationId: deleteScene
      summary: Delete a scene.
      tags: [scenes]
      parameters: [ { $ref: '#/components/parameters/FriendlyName' } ]
      responses:
        '202': { description: Deleted. }
        '404': { description: No scene with that name. }
  /api/scenes/{name}/recall:
    post:
      operationId: recallScene
      summary: Recall a scene (fan out its stored values).
      tags: [scenes]
      parameters: [ { $ref: '#/components/parameters/FriendlyName' } ]
      responses:
        '202': { description: Accepted; applied asynchronously. }
        '404': { description: No scene with that name. }
  /api/scenes/{name}/rename:
    post:
      operationId: renameScene
      summary: Rename a scene.
      tags: [scenes]
      parameters: [ { $ref: '#/components/parameters/FriendlyName' } ]
      requestBody:
        required: true
        content: { application/json: { schema: { $ref: '#/components/schemas/RenameRequest' } } }
      responses:
        '200': { description: Renamed. }
        '400': { description: Not usable. }
        '404': { description: No scene with that name. }
        '409': { description: Target name taken. }
```

and schemas:

```yaml
    Scene:
      type: object
      required: [friendly_name, members]
      properties:
        friendly_name: { type: string }
        members: { type: array, items: { type: string } }
    StoreSceneRequest:
      type: object
      properties:
        devices: { type: array, items: { type: string }, description: 'Members to snapshot (ignored when state is present).' }
        state:
          type: object
          additionalProperties: { type: object, additionalProperties: true }
          description: 'Explicit per-device property map; when present, stored verbatim instead of snapshotting.'
```

- [ ] **Step 6: Build to regenerate, then write the failing controller test**

Run: `dotnet build` (regenerates the base with `ListScenes`/`PutScene`/`DeleteScene`/`RecallScene`/`RenameScene`).
Add a controller test mirroring the group tests (probe `ScenesRef` auto-replying `SceneOpResult`), e.g. `RecallScene_returns_202_for_known_scene`.

- [ ] **Step 7: Implement the scene controller methods**

Add `ScenesRef scenes` to the controller constructor and implement:

```csharp
private IActorRef Scn => scenes.Ref;

public override async Task<ActionResult<ICollection<Gen.Scene>>> ListScenes()
{
    var views = await Scn.Ask<IReadOnlyList<Matterhorn.Scenes.SceneView>>(new Matterhorn.Scenes.GetScenes(), Timeout);
    return views.Select(v => new Gen.Scene { Friendly_name = v.FriendlyName, Members = v.Members.ToList() }).ToList();
}

public override async Task<IActionResult> PutScene(string name, Gen.StoreSceneRequest body)
{
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>>? explicitState = null;
    if (body?.State is { Count: > 0 })
        explicitState = body.State.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyDictionary<string, JsonElement>)JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                JsonSerializer.Serialize(kv.Value))!);
    var devices = body?.Devices?.ToList() ?? new List<string>();
    var r = await Scn.Ask<Matterhorn.Scenes.SceneOpResult>(
        new Matterhorn.Scenes.StoreScene(name, devices, explicitState, Guid.NewGuid().ToString("N")), Timeout);
    return r.Ok ? Created($"/api/scenes/{r.Name}", null) : BadRequest();
}

public override async Task<IActionResult> DeleteScene(string name)
{
    var r = await Scn.Ask<Matterhorn.Scenes.SceneOpResult>(new Matterhorn.Scenes.DeleteScene(name, Guid.NewGuid().ToString("N")), Timeout);
    return r.Ok ? Accepted() : NotFound();
}

public override async Task<IActionResult> RecallScene(string name)
{
    var r = await Scn.Ask<Matterhorn.Scenes.SceneOpResult>(new Matterhorn.Scenes.RecallSceneByName(name, Guid.NewGuid().ToString("N")), Timeout);
    return r.Ok ? Accepted() : NotFound();
}

public override async Task<IActionResult> RenameScene(string name, Gen.RenameRequest body)
{
    var r = await Scn.Ask<Matterhorn.Scenes.SceneOpResult>(new Matterhorn.Scenes.RenameScene(name, body.To, Guid.NewGuid().ToString("N")), Timeout);
    return r switch
    {
        { Ok: true } => Ok(),
        { Error: "not_found" } => NotFound(),
        { Error: "name_taken" } => Conflict(),
        _ => BadRequest(),
    };
}
```

- [ ] **Step 8: Build + full suite**

Run: `dotnet build` then `dotnet test src/Matterhorn.Test`
Expected: build OK, all green.

- [ ] **Step 9: Commit**

```bash
git add src/Matterhorn/Mqtt/MqttCommandRouter.cs contracts/matterhorn.openapi.yaml src/Matterhorn/Api/MatterhornController.cs src/Matterhorn.Test/
git commit -m "feat: MQTT + REST scene endpoints"
```

---

### Task 16: Dashboard — scenes UI

**Files:**
- Modify: `src/Matterhorn/wwwroot/index.html`
- Modify: `src/Matterhorn/Api/ServerSentEvents.cs` (subscribe `SceneListChanged`; forward a `scenes` frame)

- [ ] **Step 1: Add the SSE `scenes` frame**

In `ServerSentEvents.cs`, subscribe the bridge to `SceneListChanged`:

```csharp
system.EventStream.Subscribe(bridge, typeof(Matterhorn.Scenes.SceneListChanged));
```

In `SseBridgeActor`:

```csharp
Receive<Matterhorn.Scenes.SceneListChanged>(_ => writer.TryWrite("{\"type\":\"scenes\"}"));
```

- [ ] **Step 2: Add the Scenes UI**

In `index.html`, add a Scenes section + JS:
- `loadScenes()` → `GET /api/scenes`, render each scene as a chip/card with a **Recall** button (`POST /api/scenes/<name>/recall`) and a kebab (rename via `POST …/rename`, delete via `DELETE /api/scenes/<name>`).
- A "Capture scene" form: name input + a device multi-select (checkboxes from the current device list, default all) → `PUT /api/scenes/<name>` with `{devices:[...]}` (snapshot). (Explicit-state authoring is out of scope for the UI — the REST `state` field remains for scripting.)
- In SSE `onmessage`, add `else if (m.type==='scenes'){ loadScenes(); }`.
- Call `loadScenes()` from `loadAll()` and on connect.

- [ ] **Step 3: Manual verification**

With `docker compose up --build`:
1. Set two demo devices to distinct states (one ON dimmed, one OFF).
2. Capture a scene `movie` including both; confirm it appears and `matterhorn/bridge/scenes` is retained with the snapshot.
3. Change both devices to different states; hit **Recall**; confirm they return to the captured states.
4. Confirm a read-only sensor value is NOT part of the scene (inspect `scenes.json` or `bridge/scenes`).
5. Rename + delete the scene; reload; confirm scenes persist across reload.

- [ ] **Step 4: Commit**

```bash
git add src/Matterhorn/wwwroot/index.html src/Matterhorn/Api/ServerSentEvents.cs
git commit -m "feat: dashboard scenes UI + SSE scenes frame"
```

**Part B is now shippable: scenes work over MQTT, REST, and the dashboard.**

---

## Task 17: Docs + final verification

**Files:**
- Modify: `CLAUDE.md` (MQTT topic conventions section), `.env.example` / compose docs if group/scene storage paths need mention.

- [ ] **Step 1: Update `CLAUDE.md` MQTT conventions**

In the "MQTT topic conventions" section, extend the tree description to include `<base>/<group>`, `<base>/bridge/{groups,scenes}`, and `<base>/bridge/request/{group,scene}/*`.

- [ ] **Step 2: Full suite + live smoke**

Run: `dotnet test src/Matterhorn.Test` (all green), then `docker compose up --build` and walk one group + one scene end-to-end on the dashboard.

- [ ] **Step 3: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: document group/scene MQTT topics"
```

---

## Self-Review notes (author checklist — resolved)

- **Spec coverage:** groups store/actor/supervisor (Tasks 4–6), group MQTT+gateway routing (7), wiring+collision (8), group REST (9), group UI (10); scenes store/actor/supervisor (12–14), scene MQTT+REST (15), scene UI (16); shared gateway seams (1–3, 11); optimistic echo (Task 5); snapshot settable-filter (Task 14); pruning (Tasks 6, 14); bidirectional name collision (Tasks 6 create + 8 rename). All spec sections mapped.
- **Deviation from spec (fan-out mechanism):** the spec said entities resolve endpoint actors by path directly; the plan routes fan-out through the gateway via `RouteSet`/`RouteGetState` (by stable key) instead. This is strictly more robust — it reuses the authoritative `_byKey` map, avoids coupling to actor-path/child-naming, and is rename-safe. Entities still never block the gateway. Update the spec's one "direct path" sentence to match if desired.
- **Type consistency:** `GroupOpResult`/`SceneOpResult` error strings are used identically in supervisor + controller; `RouteSet`/`RouteGetState` signatures match across producer/consumer tasks; `CommandTargets` threads through router + `MqttBridgeService`.
- **Known follow-ups (not blockers):** the served contract copy (`src/Matterhorn/openapi/matterhorn.yaml` vs `contracts/…`) — verify which the build/Swagger reads and keep them in sync (noted in Task 9).
