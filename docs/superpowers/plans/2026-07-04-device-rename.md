# Device Rename Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user rename a device at runtime from the dashboard (and over MQTT), persisted to a JSON file so the name survives service restarts and device re-joins.

**Architecture:** All rename logic lives in `MatterGatewayActor`; the MQTT router and the REST controller are thin facades that both converge on a single `RenameRequest` message, so the two surfaces stay at capability parity by construction. A small `INameStore` slice persists a `{(nodeId,endpoint) → name}` override map to `data/names.json`; the gateway applies overrides at device-join time and saves on every successful rename. The per-device `MatterEndpointActor` migrates its retained MQTT topics on rename.

**Tech Stack:** .NET 10, Akka.NET (Akka.Hosting), xUnit + Akka.TestKit.Xunit2, NSwag (contract-first REST), HiveMQtt.

## Global Constraints

- Target framework `net10.0`, `Nullable` enabled — copy verbatim into new files.
- `contracts/matterhorn.openapi.yaml` is the single source of truth for REST; the controller base is generated into `obj/generated/ApiContract.g.cs` by an MSBuild target on every `dotnet build`. Never hand-edit generated code.
- Code style: vertical slices, ASP.NET-style namespaces (`Matterhorn.<Slice>`), no architecture buzzwords. Match the terse, comment-where-non-obvious style of existing files.
- Names become MQTT topic segments — every user-supplied name MUST be passed through `FriendlyName.Slug` before use.
- Commits: conventional-commit style (`feat:`, `test:`, `chore:`), **no `Co-Authored-By` / co-author trailer**.
- Run tests from the repo root with `dotnet test`. Solution builds all projects.

---

### Task 1: Persistence slice — `INameStore` + `JsonNameStore`

A self-contained name-override store. No actor or gateway changes yet.

**Files:**
- Create: `src/Matterhorn/Persistence/INameStore.cs`
- Create: `src/Matterhorn/Persistence/JsonNameStore.cs`
- Create: `src/Matterhorn/Persistence/NullNameStore.cs`
- Create: `src/Matterhorn.Test/Persistence/InMemoryNameStore.cs`
- Test: `src/Matterhorn.Test/Persistence/JsonNameStoreTests.cs`

**Interfaces:**
- Produces:
  - `interface INameStore { IReadOnlyDictionary<(ulong NodeId, ushort Endpoint), string> Load(); void Save(IReadOnlyDictionary<(ulong NodeId, ushort Endpoint), string> names); }`
  - `class JsonNameStore(string path) : INameStore`
  - `class NullNameStore : INameStore` with `static NullNameStore Instance`
  - `class InMemoryNameStore : INameStore` (test double) with a mutable public `Dictionary<(ulong,ushort),string> Names`

- [ ] **Step 1: Write the failing test**

Create `src/Matterhorn.Test/Persistence/JsonNameStoreTests.cs`:

```csharp
using Matterhorn.Persistence;

namespace Matterhorn.Test.Persistence;

public class JsonNameStoreTests
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"names-{Guid.NewGuid():N}.json");

    [Fact]
    public void Load_missing_file_returns_empty()
    {
        var store = new JsonNameStore(TempFile());
        Assert.Empty(store.Load());
    }

    [Fact]
    public void Save_then_load_round_trips_entries()
    {
        var path = TempFile();
        try
        {
            var store = new JsonNameStore(path);
            store.Save(new Dictionary<(ulong, ushort), string>
            {
                [(5UL, (ushort)1)] = "living_room_lamp",
                [(9UL, (ushort)1)] = "desk_bulb",
            });

            var loaded = new JsonNameStore(path).Load();
            Assert.Equal("living_room_lamp", loaded[(5UL, 1)]);
            Assert.Equal("desk_bulb", loaded[(9UL, 1)]);
        }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~JsonNameStoreTests"`
Expected: FAIL — compile error, `JsonNameStore` / `INemStore` do not exist.

- [ ] **Step 3: Write the interface and implementations**

Create `src/Matterhorn/Persistence/INameStore.cs`:

```csharp
namespace Matterhorn.Persistence;

/// <summary>Persists the user's device name overrides, keyed by stable (nodeId, endpoint).</summary>
public interface INameStore
{
    IReadOnlyDictionary<(ulong NodeId, ushort Endpoint), string> Load();
    void Save(IReadOnlyDictionary<(ulong NodeId, ushort Endpoint), string> names);
}
```

Create `src/Matterhorn/Persistence/NullNameStore.cs`:

```csharp
namespace Matterhorn.Persistence;

/// <summary>No-op store — the default when persistence is not configured (e.g. unit tests).</summary>
public sealed class NullNameStore : INameStore
{
    public static readonly NullNameStore Instance = new();
    public IReadOnlyDictionary<(ulong NodeId, ushort Endpoint), string> Load() =>
        new Dictionary<(ulong, ushort), string>();
    public void Save(IReadOnlyDictionary<(ulong NodeId, ushort Endpoint), string> names) { }
}
```

Create `src/Matterhorn/Persistence/JsonNameStore.cs`:

```csharp
using System.Text.Json;

namespace Matterhorn.Persistence;

/// <summary>
/// Stores the override map as one JSON object keyed "&lt;nodeId&gt;_&lt;endpoint&gt;" → custom name,
/// e.g. { "5_1": "living_room_lamp" }. A missing file loads as empty.
/// </summary>
public sealed class JsonNameStore(string path) : INameStore
{
    public IReadOnlyDictionary<(ulong NodeId, ushort Endpoint), string> Load()
    {
        var result = new Dictionary<(ulong, ushort), string>();
        if (!File.Exists(path)) return result;
        Dictionary<string, string>? raw;
        try { raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)); }
        catch (JsonException) { return result; }
        if (raw is null) return result;
        foreach (var (key, name) in raw)
            if (TryParseKey(key, out var parsed)) result[parsed] = name;
        return result;
    }

    public void Save(IReadOnlyDictionary<(ulong NodeId, ushort Endpoint), string> names)
    {
        var raw = names.ToDictionary(kv => $"{kv.Key.NodeId}_{kv.Key.Endpoint}", kv => kv.Value);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(raw, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static bool TryParseKey(string key, out (ulong, ushort) parsed)
    {
        parsed = default;
        var parts = key.Split('_');
        if (parts.Length != 2) return false;
        if (!ulong.TryParse(parts[0], out var node) || !ushort.TryParse(parts[1], out var ep)) return false;
        parsed = (node, ep);
        return true;
    }
}
```

Create `src/Matterhorn.Test/Persistence/InMemoryNameStore.cs`:

```csharp
using Matterhorn.Persistence;

namespace Matterhorn.Test.Persistence;

/// <summary>Test double: holds the override map in memory and records saves.</summary>
public sealed class InMemoryNameStore : INameStore
{
    public Dictionary<(ulong NodeId, ushort Endpoint), string> Names { get; } = new();
    public IReadOnlyDictionary<(ulong NodeId, ushort Endpoint), string> Load() => Names;
    public void Save(IReadOnlyDictionary<(ulong NodeId, ushort Endpoint), string> names)
    {
        Names.Clear();
        foreach (var (k, v) in names) Names[k] = v;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~JsonNameStoreTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Matterhorn/Persistence src/Matterhorn.Test/Persistence
git commit -m "feat: add INameStore + JsonNameStore for name overrides"
```

---

### Task 2: Endpoint actor migrates its retained topics on rename

`MatterEndpointActor` learns a `Rename` message: clear the old retained state + availability topics, adopt the new name, republish under it.

**Files:**
- Modify: `src/Matterhorn/Devices/DeviceMessages.cs`
- Modify: `src/Matterhorn/Devices/MatterEndpointActor.cs:16` (field) and constructor receives
- Test: `src/Matterhorn.Test/Devices/MatterEndpointActorTests.cs`

**Interfaces:**
- Produces: `record Rename(string NewName)` in namespace `Matterhorn.Devices`.
- Consumes: `IMqttPublisher.PublishRetained(topic, "")` clears a retained topic (empty payload).

- [ ] **Step 1: Write the failing test**

Add to `src/Matterhorn.Test/Devices/MatterEndpointActorTests.cs` (new `[Fact]` in the existing class — match the file's existing constructor/setup pattern for building the actor with a `FakeMatterController`, `InMemoryMqttPublisher`, and `MqttTopics("matterhorn")`; the endpoint is created via `MatterEndpointActor.Props("lamp", 5, 1, controller, mqtt, topics)`):

```csharp
[Fact]
public void Rename_clears_old_retained_topics_and_republishes_under_new_name()
{
    var mqtt = new InMemoryMqttPublisher();
    var actor = Sys.ActorOf(MatterEndpointActor.Props("lamp", 5, 1,
        new FakeMatterController(), mqtt, new MqttTopics("matterhorn")));

    // Seed some state so there is a retained payload to migrate.
    actor.Tell(new ApplyAttribute(new AttributeReading(5, 1, MatterClusters.OnOff, 0,
        JsonDocument.Parse("true").RootElement)));
    AwaitAssert(() => Assert.Contains(mqtt.Messages, m => m.Topic == "matterhorn/lamp"));

    actor.Tell(new Rename("desk_bulb"));

    AwaitAssert(() =>
    {
        // Old retained state + availability cleared with an empty retained payload.
        Assert.Contains(mqtt.Messages, m => m.Topic == "matterhorn/lamp" && m.Retained && m.Payload == "");
        Assert.Contains(mqtt.Messages, m => m.Topic == "matterhorn/lamp/availability" && m.Retained && m.Payload == "");
        // State republished under the new name.
        Assert.Contains(mqtt.Messages, m => m.Topic == "matterhorn/desk_bulb" && m.Payload.Contains("\"state\":\"ON\""));
        Assert.Contains(mqtt.Messages, m => m.Topic == "matterhorn/desk_bulb/availability" && m.Payload == "online");
    });
}
```

Note: `MatterEndpointActorTests` may need `using System.Text.Json;` and `using Matterhorn.Matter;` — add if missing.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~MatterEndpointActorTests.Rename_clears_old_retained"`
Expected: FAIL — `Rename` type does not exist / no handler.

- [ ] **Step 3: Add the message**

In `src/Matterhorn/Devices/DeviceMessages.cs`, after the `Republish` record:

```csharp
/// <summary>Adopt a new friendly name: clear the old retained topics and republish under the new one.</summary>
public record Rename(string NewName);
```

- [ ] **Step 4: Make `_name` mutable and add the handler**

In `src/Matterhorn/Devices/MatterEndpointActor.cs`, change the field declaration:

```csharp
    private string _name;
```

(from `private readonly string _name;`)

Add this handler inside the constructor, after the `Receive<Republish>(...)` block:

```csharp
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
```

Note: availability is published non-retained elsewhere (`SetReachable`/`Republish` use `Publish`), so the "clear" uses `PublishRetained(..., "")` defensively while the new availability is published with `Publish` to match existing behaviour.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~MatterEndpointActorTests"`
Expected: PASS (existing tests + the new one).

- [ ] **Step 6: Commit**

```bash
git add src/Matterhorn/Devices/DeviceMessages.cs src/Matterhorn/Devices/MatterEndpointActor.cs src/Matterhorn.Test/Devices/MatterEndpointActorTests.cs
git commit -m "feat: migrate endpoint retained topics on rename"
```

---

### Task 3: Gateway rename handler + override-aware join

The gateway gains the core operation: validate, look up, collision-check, migrate the in-memory model, tell the endpoint actor, persist, republish, and answer both the REST `Ask` (via `Sender`) and the MQTT response topic. It also applies stored overrides at join time.

**Files:**
- Modify: `src/Matterhorn/Bridge/BridgeMessages.cs`
- Modify: `src/Matterhorn/Bridge/MatterGatewayActor.cs`
- Test: `src/Matterhorn.Test/Bridge/MatterGatewayActorTests.cs`

**Interfaces:**
- Produces:
  - `record RenameRequest(string FromName, string ToName, string Transaction)` (namespace `Matterhorn.Bridge`)
  - `record RenameResult(bool Ok, string? Error, string? NewName = null)` — error codes: `"invalid_name"`, `"not_found"`, `"name_taken"`.
  - `MatterGatewayActor.Props(controller, mqtt, topics, INameStore? names = null)` — new optional trailing parameter; existing 3-arg calls keep working.
- Consumes: `Matterhorn.Persistence.INameStore`, `NullNameStore.Instance`, `Matterhorn.Devices.Rename`, `FriendlyName.Slug`.

- [ ] **Step 1: Write the failing tests**

Add to `src/Matterhorn.Test/Bridge/MatterGatewayActorTests.cs` (add `using Matterhorn.Persistence;` and `using Matterhorn.Test.Persistence;` at the top):

```csharp
[Fact]
public void Rename_rekeys_the_device_and_republishes_bridge_devices()
{
    var fake = new FakeMatterController();
    var mqtt = new InMemoryMqttPublisher();
    var store = new InMemoryNameStore();
    var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, mqtt, new MqttTopics("matterhorn"), store));
    fake.Emit(new NodeAdded(Light(5)));
    AwaitAssert(() => Assert.Single(gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result));

    var result = gw.Ask<RenameResult>(new RenameRequest("bulb_5_1", "Living Room Lamp!", "tx1")).Result;

    Assert.True(result.Ok);
    Assert.Equal("living_room_lamp", result.NewName); // slugified
    var devices = gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result;
    Assert.Equal("living_room_lamp", Assert.Single(devices).FriendlyName);
    Assert.Equal("living_room_lamp", store.Names[(5UL, 1)]); // persisted
    AwaitAssert(() => Assert.Contains(mqtt.Messages,
        m => m.Topic == "matterhorn/bridge/response/rename" && m.Payload.Contains("\"tx1\"") && m.Payload.Contains("ok")));
}

[Fact]
public void Rename_unknown_device_returns_not_found()
{
    var fake = new FakeMatterController();
    var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, new InMemoryMqttPublisher(), new MqttTopics("matterhorn")));

    var result = gw.Ask<RenameResult>(new RenameRequest("ghost", "whatever", "tx1")).Result;

    Assert.False(result.Ok);
    Assert.Equal("not_found", result.Error);
}

[Fact]
public void Rename_to_an_existing_name_is_rejected()
{
    var fake = new FakeMatterController();
    var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, new InMemoryMqttPublisher(), new MqttTopics("matterhorn")));
    fake.Emit(new NodeAdded(Light(5)));
    fake.Emit(new NodeAdded(Light(6)));
    AwaitAssert(() => Assert.Equal(2, gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result.Count));

    var result = gw.Ask<RenameResult>(new RenameRequest("bulb_5_1", "bulb_6_1", "tx1")).Result;

    Assert.False(result.Ok);
    Assert.Equal("name_taken", result.Error);
}

[Fact]
public void Rename_with_an_empty_slug_is_rejected()
{
    var fake = new FakeMatterController();
    var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, new InMemoryMqttPublisher(), new MqttTopics("matterhorn")));
    fake.Emit(new NodeAdded(Light(5)));
    AwaitAssert(() => Assert.Single(gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result));

    var result = gw.Ask<RenameResult>(new RenameRequest("bulb_5_1", "!!!", "tx1")).Result;

    Assert.False(result.Ok);
    Assert.Equal("invalid_name", result.Error);
}

[Fact]
public void Stored_override_is_applied_when_the_device_joins()
{
    var fake = new FakeMatterController();
    var store = new InMemoryNameStore();
    store.Names[(5UL, 1)] = "living_room_lamp";
    var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, new InMemoryMqttPublisher(), new MqttTopics("matterhorn"), store));

    fake.Emit(new NodeAdded(Light(5)));

    AwaitAssert(() => Assert.Equal("living_room_lamp",
        Assert.Single(gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result).FriendlyName));
}

[Fact]
public void Set_after_rename_still_reaches_the_device()
{
    var fake = new FakeMatterController();
    var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, new InMemoryMqttPublisher(), new MqttTopics("matterhorn"), new InMemoryNameStore()));
    fake.Emit(new NodeAdded(Light(5)));
    AwaitAssert(() => Assert.Single(gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result));
    Assert.True(gw.Ask<RenameResult>(new RenameRequest("bulb_5_1", "lamp", "tx1")).Result.Ok);

    var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"state":"ON"}""")!;
    gw.Tell(new SetDevice("lamp", payload));

    AwaitAssert(() => Assert.Contains(fake.Invocations, i => i.NodeId == 5 && i.Cmd.CommandName == "On"));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~MatterGatewayActorTests.Rename"`
Expected: FAIL — `RenameRequest` / `RenameResult` / 4-arg `Props` do not exist.

- [ ] **Step 3: Add the gateway messages**

In `src/Matterhorn/Bridge/BridgeMessages.cs`, after the `RemoveRequest` record:

```csharp
public record RenameRequest(string FromName, string ToName, string Transaction);

/// <summary>Reply to a <see cref="RenameRequest"/>. Error is one of: invalid_name, not_found, name_taken.</summary>
public record RenameResult(bool Ok, string? Error, string? NewName = null);
```

- [ ] **Step 4: Wire the store into the gateway and add the handler**

In `src/Matterhorn/Bridge/MatterGatewayActor.cs`:

Add `using Matterhorn.Persistence;` near the other usings.

Add a field alongside `_topics`:

```csharp
    private readonly INameStore _names;
    private readonly Dictionary<(ulong, ushort), string> _overrides;
```

Change `Props` and the constructor signature to accept an optional store:

```csharp
    public static Props Props(IMatterController controller, IMqttPublisher mqtt, MqttTopics topics, INameStore? names = null) =>
        Akka.Actor.Props.Create(() => new MatterGatewayActor(controller, mqtt, topics, names));

    public MatterGatewayActor(IMatterController controller, IMqttPublisher mqtt, MqttTopics topics, INameStore? names = null)
    {
        _controller = controller; _mqtt = mqtt; _topics = topics;
        _names = names ?? NullNameStore.Instance;
        _overrides = new Dictionary<(ulong, ushort), string>(_names.Load());
```

(keep the rest of the constructor body; just add the two `_names`/`_overrides` lines after the existing field assignments)

Register the handler with the other `Receive<...>` calls in the constructor:

```csharp
        Receive<RenameRequest>(OnRename);
```

In `OnNodeAdded`, replace the name derivation line (`MatterGatewayActor.cs:81`):

```csharp
        var name = _overrides.TryGetValue((info.NodeId, info.Endpoint), out var custom)
            ? custom
            : FriendlyName.Default(info.ProductName, info.NodeId, info.Endpoint);
```

Add the handler methods (near `OnRemove`):

```csharp
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
        try { _names.Save(_overrides); }
        catch (Exception ex) { _log.Warning("Failed to persist name override: {Error}", ex.Message); }

        PublishDevices();
        PublishEvent("device_renamed", new { from, to = slug });
        return new RenameResult(true, null, slug);
    }
```

Add `using Matterhorn.Devices;` if the `Rename` type is not already resolvable (the file already references `Matterhorn.Devices` messages like `ApplySet`, so it should be present).

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~MatterGatewayActorTests"`
Expected: PASS (all existing + 6 new).

- [ ] **Step 6: Commit**

```bash
git add src/Matterhorn/Bridge/BridgeMessages.cs src/Matterhorn/Bridge/MatterGatewayActor.cs src/Matterhorn.Test/Bridge/MatterGatewayActorTests.cs
git commit -m "feat: rename devices in the gateway with persisted overrides"
```

---

### Task 4: MQTT facade — implement `bridge/request/rename`

Fill in the existing stub so an inbound `matterhorn/bridge/request/rename` `{from,to,transaction}` becomes a `RenameRequest`.

**Files:**
- Modify: `src/Matterhorn/Mqtt/MqttCommandRouter.cs:63`
- Test: `src/Matterhorn.Test/Mqtt/MqttCommandRouterTests.cs`

**Interfaces:**
- Consumes: `Matterhorn.Bridge.RenameRequest` (from Task 3).

- [ ] **Step 1: Write the failing test**

Add to `src/Matterhorn.Test/Mqtt/MqttCommandRouterTests.cs`:

```csharp
[Fact]
public void Rename_request_forwards_RenameRequest()
{
    var gw = CreateTestProbe();
    MqttCommandRouter.Route(_topics, "matterhorn/bridge/request/rename",
        """{"from":"bulb_5_1","to":"lamp","transaction":"tx7"}""", gw.Ref);

    var msg = gw.ExpectMsg<RenameRequest>();
    Assert.Equal("bulb_5_1", msg.FromName);
    Assert.Equal("lamp", msg.ToName);
    Assert.Equal("tx7", msg.Transaction);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~MqttCommandRouterTests.Rename_request"`
Expected: FAIL — no `RenameRequest` is emitted (`ExpectMsg` times out).

- [ ] **Step 3: Implement the case**

In `src/Matterhorn/Mqtt/MqttCommandRouter.cs`, replace the comment line at the end of the `switch` (`// "rename" is parsed but has no gateway handler yet (known follow-up).`) with:

```csharp
            case "rename":
                var from = root.TryGetProperty("from", out var fr) ? fr.GetString() : null;
                var to = root.TryGetProperty("to", out var tr) ? tr.GetString() : null;
                if (!string.IsNullOrEmpty(from) && !string.IsNullOrEmpty(to))
                    gateway.Tell(new RenameRequest(from, to, Tx()), ActorRefs.NoSender);
                break;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~MqttCommandRouterTests"`
Expected: PASS (existing + new).

- [ ] **Step 5: Commit**

```bash
git add src/Matterhorn/Mqtt/MqttCommandRouter.cs src/Matterhorn.Test/Mqtt/MqttCommandRouterTests.cs
git commit -m "feat: route bridge/request/rename to the gateway"
```

---

### Task 5: REST facade — `POST /api/devices/{name}/rename`

Add the operation to the contract, regenerate, and implement the override mapping `RenameResult` → HTTP status.

**Files:**
- Modify: `contracts/matterhorn.openapi.yaml`
- Modify: `src/Matterhorn/Api/MatterhornController.cs`
- Test: `src/Matterhorn.Test/Api/MatterhornControllerTests.cs`

**Interfaces:**
- Produces: REST `POST /api/devices/{name}/rename` with body `{ "to": string }` → 200 / 400 / 404 / 409.
- Consumes: `Bridge.RenameRequest`, `Bridge.RenameResult`.

- [ ] **Step 1: Add the contract operation**

In `contracts/matterhorn.openapi.yaml`, under the `/api/devices/{name}` path item, add a sibling path after the `/api/devices/{name}` block (before `/api/commission`):

```yaml
  /api/devices/{name}/rename:
    post:
      operationId: renameDevice
      summary: Rename a device.
      tags: [devices]
      parameters:
        - $ref: '#/components/parameters/FriendlyName'
      requestBody:
        required: true
        content:
          application/json:
            schema:
              $ref: '#/components/schemas/RenameRequest'
      responses:
        '200':
          description: Renamed.
        '400':
          description: The requested name is not usable.
        '404':
          description: No device with that friendly name.
        '409':
          description: A device with the target name already exists.
```

And add to `components.schemas` (after `CommissionRequest`):

```yaml
    RenameRequest:
      type: object
      required: [to]
      properties:
        to:
          type: string
          description: The new friendly name (slugified server-side into a legal topic segment).
```

- [ ] **Step 2: Regenerate + confirm the generated signature**

Run: `dotnet build src/Matterhorn/Matterhorn.csproj`
Expected: build FAILS with a message that `MatterhornControllerBase.RenameDevice` is abstract and not implemented (the generator produced a new abstract method). This confirms the contract regenerated. If the build instead passes, open `src/Matterhorn/obj/generated/ApiContract.g.cs` and confirm a `RenameDevice` abstract method and a `RenameRequest` DTO exist before continuing.

- [ ] **Step 3: Write the failing tests**

Add to `src/Matterhorn.Test/Api/MatterhornControllerTests.cs`:

```csharp
[Fact]
public async Task Rename_device_returns_200_on_success()
{
    var probe = CreateTestProbe();
    var client = ClientWithGateway(probe.Ref);

    var task = client.PostAsJsonAsync("/api/devices/bulb_5_1/rename", new Dictionary<string, string> { ["to"] = "lamp" });

    var msg = probe.ExpectMsg<RenameRequest>();
    Assert.Equal("bulb_5_1", msg.FromName);
    Assert.Equal("lamp", msg.ToName);
    probe.Reply(new RenameResult(true, null, "lamp"));

    Assert.Equal(HttpStatusCode.OK, (await task).StatusCode);
}

[Fact]
public async Task Rename_missing_device_returns_404()
{
    var probe = CreateTestProbe();
    var client = ClientWithGateway(probe.Ref);

    var task = client.PostAsJsonAsync("/api/devices/ghost/rename", new Dictionary<string, string> { ["to"] = "lamp" });
    probe.ExpectMsg<RenameRequest>();
    probe.Reply(new RenameResult(false, "not_found"));

    Assert.Equal(HttpStatusCode.NotFound, (await task).StatusCode);
}

[Fact]
public async Task Rename_conflict_returns_409()
{
    var probe = CreateTestProbe();
    var client = ClientWithGateway(probe.Ref);

    var task = client.PostAsJsonAsync("/api/devices/bulb_5_1/rename", new Dictionary<string, string> { ["to"] = "taken" });
    probe.ExpectMsg<RenameRequest>();
    probe.Reply(new RenameResult(false, "name_taken"));

    Assert.Equal(HttpStatusCode.Conflict, (await task).StatusCode);
}

[Fact]
public async Task Rename_invalid_name_returns_400()
{
    var probe = CreateTestProbe();
    var client = ClientWithGateway(probe.Ref);

    var task = client.PostAsJsonAsync("/api/devices/bulb_5_1/rename", new Dictionary<string, string> { ["to"] = "!!!" });
    probe.ExpectMsg<RenameRequest>();
    probe.Reply(new RenameResult(false, "invalid_name"));

    Assert.Equal(HttpStatusCode.BadRequest, (await task).StatusCode);
}
```

- [ ] **Step 4: Implement the override**

In `src/Matterhorn/Api/MatterhornController.cs`, add the method (after `Commission`):

```csharp
    public override async Task<IActionResult> RenameDevice(string name, Gen.RenameRequest body)
    {
        var tx = Guid.NewGuid().ToString("N");
        var result = await Gw.Ask<Bridge.RenameResult>(new Bridge.RenameRequest(name, body.To, tx), Timeout);
        return result switch
        {
            { Ok: true } => Ok(),
            { Error: "not_found" } => NotFound(),
            { Error: "name_taken" } => Conflict(),
            _ => BadRequest(),
        };
    }
```

Note: confirm the generated return type matches (`Task<IActionResult>` for a status-only operation). If the generator produced `Task<ActionResult>` instead, adjust the signature to match the generated abstract method exactly — the body is unchanged.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~MatterhornControllerTests"`
Expected: PASS (existing + 4 new).

- [ ] **Step 6: Commit**

```bash
git add contracts/matterhorn.openapi.yaml src/Matterhorn/Api/MatterhornController.cs src/Matterhorn.Test/Api/MatterhornControllerTests.cs
git commit -m "feat: add REST POST /api/devices/{name}/rename"
```

---

### Task 6: Wire persistence into the host + docker volume

Give the running app a real `JsonNameStore` and a persistent data dir.

**Files:**
- Modify: `src/Matterhorn/Configuration/MatterhornConfig.cs`
- Modify: `src/Matterhorn/Program.cs`
- Modify: `docker-compose.yml`
- Test: `src/Matterhorn.Test/Matter/SmokeTests.cs` (or the existing config test if present)

**Interfaces:**
- Produces: `MatterhornConfig.NamesFile` (string, default `data/names.json`), read from `Storage:NamesFile`.

- [ ] **Step 1: Add the config field (write a failing test first)**

Add to `src/Matterhorn.Test/Matter/SmokeTests.cs` a focused config test (or create `src/Matterhorn.Test/Configuration/MatterhornConfigTests.cs` if that fits the existing layout better):

```csharp
[Fact]
public void NamesFile_defaults_and_reads_from_config()
{
    var defaults = MatterhornConfig.FromConfiguration(
        new ConfigurationBuilder().Build());
    Assert.Equal("data/names.json", defaults.NamesFile);

    var custom = MatterhornConfig.FromConfiguration(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:NamesFile"] = "/data/n.json" }).Build());
    Assert.Equal("/data/n.json", custom.NamesFile);
}
```

Add `using Matterhorn.Configuration;` and `using Microsoft.Extensions.Configuration;` if missing.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~NamesFile_defaults"`
Expected: FAIL — `NamesFile` is not a member of `MatterhornConfig`.

- [ ] **Step 3: Add the config member**

In `src/Matterhorn/Configuration/MatterhornConfig.cs`, add `string NamesFile` as the final positional parameter of the record, and in `FromConfiguration` add as the final argument:

```csharp
        NamesFile: c["Storage:NamesFile"] ?? "data/names.json");
```

(remember to move the closing `)` — the previous last argument `ThreadDataset` now needs a trailing comma)

- [ ] **Step 4: Construct the store and pass it to the gateway**

In `src/Matterhorn/Program.cs`:

Add `using Matterhorn.Persistence;`.

After the `builder.Services.AddSingleton(controller);` line, register the store:

```csharp
builder.Services.AddSingleton<INameStore>(new JsonNameStore(cfg.NamesFile));
```

In the `AddAkka(...)` `WithActors` lambda, resolve it and pass it to `Props`:

```csharp
        var publisher = sp.GetRequiredService<IMqttPublisher>();
        var names = sp.GetRequiredService<INameStore>();
        var gw = system.ActorOf(MatterGatewayActor.Props(controller, publisher, topics, names), "gateway");
        registry.Register<MatterGatewayActor>(gw);
```

- [ ] **Step 5: Add the docker volume**

In `docker-compose.yml`, on the matterhorn app service, add a `Storage__NamesFile` env var and a named volume so the file persists across container recreation. Under the service's `environment:` block add:

```yaml
      Storage__NamesFile: "/data/names.json"
```

Add a `volumes:` entry to the same service:

```yaml
    volumes:
      - matterhorn-data:/data
```

And declare the named volume at the file's top-level `volumes:` section (create it if it does not exist):

```yaml
volumes:
  matterhorn-data:
```

Note: read the current `docker-compose.yml` first and match the exact service key and indentation; the app service is the one carrying the `Controller__Kind` / `Mqtt__Host` env vars.

- [ ] **Step 6: Verify build + full test suite**

Run: `dotnet build` then `dotnet test`
Expected: build succeeds; all tests PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Matterhorn/Configuration/MatterhornConfig.cs src/Matterhorn/Program.cs docker-compose.yml src/Matterhorn.Test
git commit -m "feat: persist device names to a JSON file via config + docker volume"
```

---

### Task 7: Dashboard rename control

Make each device's name editable in the UI, calling the new REST endpoint. The existing SSE `devices` event already triggers a full reload, so no bespoke UI state is needed.

**Files:**
- Modify: `src/Matterhorn/wwwroot/index.html`

**Interfaces:**
- Consumes: `POST /api/devices/{name}/rename` (Task 5); SSE `{"type":"devices"}` → existing `loadAll()`.

- [ ] **Step 1: Add a rename helper**

In the `<script>` of `src/Matterhorn/wwwroot/index.html`, after the `patch(...)` function, add:

```javascript
async function rename(name){
  const to = prompt('Rename “'+name+'” to:', name);
  if (to===null) return;               // cancelled
  const trimmed = to.trim();
  if (!trimmed || trimmed===name) return;
  try {
    const r = await api('/api/devices/'+encodeURIComponent(name)+'/rename',
      { method:'POST', body:JSON.stringify({ to:trimmed }) });
    if (r.status===409) return toast('That name is already taken');
    if (r.status===400) return toast('That name can’t be used');
    if (!r.ok) return toast('Rename failed');
    toast('Renamed');                  // device list refreshes via the SSE "devices" event
  } catch(e){ if(e.message!=='401') toast('Rename failed'); }
}
```

- [ ] **Step 2: Make the name clickable**

In the `station(d)` function, in the `el.innerHTML` template, change the name div to a button-like clickable element with a title. Replace:

```javascript
       <div><div class="name">${esc(d.friendly_name)}</div>
```

with:

```javascript
       <div><div class="name" data-rename title="Click to rename" style="cursor:pointer">${esc(d.friendly_name)}</div>
```

After the `el.innerHTML = ...;` assignment and the `const rows = el.querySelector('.rows');` line, add a click handler:

```javascript
  el.querySelector('[data-rename]').addEventListener('click', ()=>rename(d.friendly_name));
```

- [ ] **Step 3: Manual verification (no automated UI test)**

Per the project's run-via-docker rule, verify against the demo stack:

Run: `docker compose up --build -d` (the default `.env` runs the fake controller with `DevSeed=true`, so demo bulbs appear).
Then:
1. Open the dashboard (the app's mapped port, default REST port 8090 unless remapped in compose).
2. Click a device name, enter a new name, confirm the card re-renders with the slugified name.
3. Confirm the name survives a restart: `docker compose restart` the app service, reload — the renamed device keeps its name (proves `names.json` persisted on the `matterhorn-data` volume).
4. Try renaming to an existing device's name → expect the "already taken" toast.

Run: `docker compose down` when finished.

- [ ] **Step 4: Commit**

```bash
git add src/Matterhorn/wwwroot/index.html
git commit -m "feat: rename a device from the dashboard"
```

---

## Self-Review

**Spec coverage:**
- §1 Core operation → Task 3 (`DoRename`, validate/lookup/collision/migrate/persist/republish, both facades converge on `RenameRequest`). ✓
- §2 Endpoint topic migration → Task 2. ✓
- §3 Persistence (`INameStore`, `JsonNameStore`, `names.json`, load-at-startup, override-on-join, save-on-rename, `Storage:NamesFile` config, docker volume) → Tasks 1, 3 (override-on-join), 6 (config + wiring + volume). ✓
- §4 MQTT facade → Task 4; REST facade → Task 5. ✓
- §5 Dashboard → Task 7. ✓
- §6 Error handling (structured results; REST 400/404/409; MQTT status/error; persistence failure logged, not fatal) → Task 3 (`try/catch` around `Save`, error codes), Task 5 (status mapping). ✓
- Testing section → each task ships its own tests; slug/validation covered by the `invalid_name` gateway test and existing `FriendlyNameTests`. ✓

**Placeholder scan:** No TBD/TODO left; every code step shows complete code. The one deliberate "confirm the generated signature" note in Task 5 is a real generator-output check, not a placeholder — a fallback instruction is given.

**Type consistency:** `RenameRequest(FromName, ToName, Transaction)`, `RenameResult(Ok, Error, NewName)`, `Rename(NewName)`, `INameStore.Load/Save`, and `Props(..., INameStore? names = null)` are used identically across Tasks 2–6. Error codes `invalid_name` / `not_found` / `name_taken` match between gateway (Task 3) and REST mapping (Task 5). ✓
