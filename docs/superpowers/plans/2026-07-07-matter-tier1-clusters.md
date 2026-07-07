# Matter Tier 1 Clusters + Dashboard Controls Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Broaden Matterhorn's Matter coverage to the full Tier 1 cluster set (window covering, door lock, thermostat, fan, light polish, and a batch of read-only sensors) with working web-dashboard controls, projected identically across MQTT, REST, and the dashboard.

**Architecture:** Extend the five table-driven mapping seams (`MatterClusters`, `PropertyMapping`, `CommandMapping`, `ExposesBuilder`, `MatterServerProtocol.DeviceTypeName`) that already turn generic `device_command`/`attribute_updated` traffic into Z2M-shaped properties. Add one new controller verb — `WriteAttribute` — because fan and thermostat are attribute-write configured in Matter, not command-driven. Refactor the write path so `CommandMapping.Map` returns a sum type (`IDeviceWrite` = `CommandSpec` | `AttributeWriteSpec`) that the endpoint actor dispatches to either `InvokeCommand` or `WriteAttribute`. Generalize the exposes-driven dashboard to render any settable numeric/enum property, adding a single generic `enum` control.

**Tech Stack:** .NET 10, Akka.NET + Akka.Hosting, Akka.TestKit.Xunit2, System.Text.Json, NSwag (contract-first REST from `contracts/matterhorn.openapi.yaml`), vanilla-JS dashboard (`src/Matterhorn/wwwroot`).

## Global Constraints

- **.NET 10**; solution `Matterhorn.slnx`; app `src/Matterhorn`, tests `src/Matterhorn.Test`.
- **Vertical slices by concern**, ASP.NET-style namespaces (`Matterhorn.Matter`, `Matterhorn.Devices`, `Matterhorn.Bridge`, …). No layered folders.
- **Contract-first REST:** edit `contracts/matterhorn.openapi.yaml` first, then `dotnet build` regenerates `obj/generated/ApiContract.g.cs`; implement/adjust mapping in `Api/MatterhornController`. Never hand-edit generated code.
- **MQTT and REST are two projections of one model** — every operation exists on both. The write path (`CommandMapping` → endpoint actor) is shared by both routers, so wiring it once covers both.
- **Wire property names are Z2M-shaped snake_case.** Exposes `access` is a bitmask: 1=published, 2=set, 4=get (`ExposesBuilder` constants `Published`/`Set`/`Get`/`All`).
- **DTO wire names are snake_case** via generated `[JsonPropertyName]`; JSON omits nulls (`DefaultIgnoreCondition = WhenWritingNull`, set in `Program.cs`).
- **Cover position inversion:** Matter lift-% is `0=open, 100=closed`; Z2M `position` is `0=closed, 100=open`. Invert on read and write.
- **Build:** `dotnet build`. **Full suite:** `dotnet test src/Matterhorn.Test`. **One test:** `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~<TypeOrMethod>"`.
- **Commit after every task.** Conventional-commit messages (`feat:`, `refactor:`, `test:`). No `Co-Authored-By` trailer. Never `git push`.
- **Dashboard has no automated tests** (repo convention); dashboard tasks verify via `docker compose up --build` and the fake controller's demo devices.

---

## File Structure

- `src/Matterhorn/Matter/MatterClusters.cs` — new cluster id constants.
- `src/Matterhorn/Matter/IMatterController.cs` — add `WriteAttribute`.
- `src/Matterhorn/Matter/MatterServerProtocol.cs` — `write_attribute` codec + new `DeviceTypeName` entries.
- `src/Matterhorn/Matter/MatterServerController.cs` — `WriteAttribute` over the socket.
- `src/Matterhorn/Matter/FakeMatterController.cs` — record attribute writes.
- `src/Matterhorn/Devices/CommandSpec.cs` — `IDeviceWrite` marker + `AttributeWriteSpec`; `CommandSpec : IDeviceWrite`.
- `src/Matterhorn/Devices/CommandMapping.cs` — handler-registry refactor; per-category write handlers.
- `src/Matterhorn/Devices/PropertyMapping.cs` — per-category read rows.
- `src/Matterhorn/Devices/MatterEndpointActor.cs` — dispatch command vs attribute-write in `OnSet`.
- `src/Matterhorn/Bridge/DeviceDescriptor.cs` — `ExposeEntry` gains `Values` (+ `enum` type).
- `src/Matterhorn/Bridge/ExposesBuilder.cs` — per-category exposes.
- `contracts/matterhorn.openapi.yaml` — `Expose.values`.
- `src/Matterhorn/Api/MatterhornController.cs` — map `Values` in `ToDto`.
- `src/Matterhorn/wwwroot/js/views/devices.js` — generalized settable detection, enum control, identify button.
- `src/Matterhorn/wwwroot/app.css` — styles for the enum/segmented control + identify button.
- Tests under `src/Matterhorn.Test/**` mirroring each seam.

---

## Phase 0 — Foundations (write seam, sum type, exposes enum)

### Task 1: `WriteAttribute` controller seam

**Files:**
- Modify: `src/Matterhorn/Matter/IMatterController.cs`
- Modify: `src/Matterhorn/Matter/MatterServerProtocol.cs` (add `WriteAttribute` codec)
- Modify: `src/Matterhorn/Matter/MatterServerController.cs`
- Modify: `src/Matterhorn/Matter/FakeMatterController.cs`
- Test: `src/Matterhorn.Test/Matter/MatterServerProtocolTests.cs`, `src/Matterhorn.Test/Matter/FakeMatterControllerTests.cs`

**Interfaces:**
- Produces: `Task IMatterController.WriteAttribute(ulong nodeId, ushort endpoint, uint clusterId, uint attributeId, object? value, CancellationToken ct)`.
- Produces: `FakeMatterController.AttributeWrites` — `IReadOnlyList<(ulong NodeId, ushort Endpoint, uint ClusterId, uint AttributeId, object? Value)>`.
- Produces: `MatterServerProtocol.WriteAttribute(int messageId, ulong nodeId, ushort endpoint, uint clusterId, uint attributeId, object? value) : string`.

- [ ] **Step 1: Write the failing protocol test**

Add to `src/Matterhorn.Test/Matter/MatterServerProtocolTests.cs`:

```csharp
[Fact]
public void WriteAttribute_serializes_write_attribute_command()
{
    var json = MatterServerProtocol.WriteAttribute(7, nodeId: 42, endpoint: 1, clusterId: 0x0201, attributeId: 0x0012, value: 2100);
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;
    Assert.Equal("7", root.GetProperty("message_id").GetString());
    Assert.Equal("write_attribute", root.GetProperty("command").GetString());
    var args = root.GetProperty("args");
    Assert.Equal(42UL, args.GetProperty("node_id").GetUInt64());
    Assert.Equal("1/513/18", args.GetProperty("attribute_path").GetString());
    Assert.Equal(2100, args.GetProperty("value").GetInt32());
}
```

- [ ] **Step 2: Run it, verify it fails**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~WriteAttribute_serializes_write_attribute_command"`
Expected: FAIL — `MatterServerProtocol` has no `WriteAttribute`.

- [ ] **Step 3: Add the protocol codec**

In `src/Matterhorn/Matter/MatterServerProtocol.cs`, after `DeviceCommand`:

```csharp
public static string WriteAttribute(int messageId, ulong nodeId, ushort endpoint, uint clusterId, uint attributeId, object? value) =>
    JsonSerializer.Serialize(new
    {
        message_id = messageId.ToString(),
        command = "write_attribute",
        args = new
        {
            node_id = nodeId,
            attribute_path = $"{endpoint}/{clusterId}/{attributeId}",
            value,
        }
    });
```

- [ ] **Step 4: Run it, verify it passes**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~WriteAttribute_serializes_write_attribute_command"`
Expected: PASS.

- [ ] **Step 5: Add the interface method**

In `src/Matterhorn/Matter/IMatterController.cs`, add to the interface body:

```csharp
Task WriteAttribute(ulong nodeId, ushort endpoint, uint clusterId, uint attributeId, object? value, CancellationToken ct);
```

- [ ] **Step 6: Implement it on the real controller**

In `src/Matterhorn/Matter/MatterServerController.cs`, after `InvokeCommand`:

```csharp
public async Task WriteAttribute(ulong nodeId, ushort endpoint, uint clusterId, uint attributeId, object? value, CancellationToken ct) =>
    await Send(MatterServerProtocol.WriteAttribute(Interlocked.Increment(ref _messageId), nodeId, endpoint, clusterId, attributeId, value), ct);
```

- [ ] **Step 7: Write the failing fake-controller test**

Add to `src/Matterhorn.Test/Matter/FakeMatterControllerTests.cs`:

```csharp
[Fact]
public async Task WriteAttribute_is_recorded()
{
    var fake = new FakeMatterController();
    await fake.WriteAttribute(42, 1, 0x0201, 0x0012, 2100, CancellationToken.None);
    var w = Assert.Single(fake.AttributeWrites);
    Assert.Equal(42UL, w.NodeId);
    Assert.Equal((ushort)1, w.Endpoint);
    Assert.Equal(0x0201u, w.ClusterId);
    Assert.Equal(0x0012u, w.AttributeId);
    Assert.Equal(2100, w.Value);
}
```

- [ ] **Step 8: Run it, verify it fails**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~WriteAttribute_is_recorded"`
Expected: FAIL — `FakeMatterController` has no `WriteAttribute`/`AttributeWrites`.

- [ ] **Step 9: Implement it on the fake**

In `src/Matterhorn/Matter/FakeMatterController.cs`, add a backing list beside `_invocations`:

```csharp
private readonly List<(ulong, ushort, uint, uint, object?)> _attributeWrites = new();
public IReadOnlyList<(ulong NodeId, ushort Endpoint, uint ClusterId, uint AttributeId, object? Value)> AttributeWrites => _attributeWrites;
```

and the method beside `InvokeCommand`:

```csharp
public Task WriteAttribute(ulong nodeId, ushort endpoint, uint clusterId, uint attributeId, object? value, CancellationToken ct)
{
    lock (_attributeWrites) _attributeWrites.Add((nodeId, endpoint, clusterId, attributeId, value));
    return Task.CompletedTask;
}
```

- [ ] **Step 10: Run the full suite (any other `IMatterController` impls must compile)**

Run: `dotnet test src/Matterhorn.Test`
Expected: PASS (build succeeds — the interface addition forces every impl to compile).

- [ ] **Step 11: Commit**

```bash
git add -A
git commit -m "feat: add WriteAttribute controller seam"
```

---

### Task 2: `IDeviceWrite` sum type + `CommandMapping` registry refactor + actor dispatch

Refactor the write path so a `/set` can produce both cluster commands and attribute writes, without changing existing on/off/level/color behaviour.

**Files:**
- Modify: `src/Matterhorn/Devices/CommandSpec.cs`
- Modify: `src/Matterhorn/Devices/CommandMapping.cs`
- Modify: `src/Matterhorn/Devices/MatterEndpointActor.cs` (`OnSet`, ~line 65)
- Test: `src/Matterhorn.Test/Devices/CommandMappingTests.cs` (migrate), `src/Matterhorn.Test/Devices/MatterEndpointActorTests.cs`

**Interfaces:**
- Produces: `interface IDeviceWrite` (marker); `record CommandSpec(...) : IDeviceWrite` (existing, now implements it); `record AttributeWriteSpec(uint ClusterId, uint AttributeId, object? Value) : IDeviceWrite`.
- Produces: `CommandMapping.Map(IReadOnlyDictionary<string, JsonElement>) : IReadOnlyList<IDeviceWrite>` (return type changed from `IReadOnlyList<CommandSpec>`).
- Consumes (Task 1): `IMatterController.WriteAttribute`.

- [ ] **Step 1: Add the sum type**

Replace `src/Matterhorn/Devices/CommandSpec.cs` contents with:

```csharp
namespace Matterhorn.Devices;

/// <summary>A single write action a <c>/set</c> expands to — a cluster command or an attribute write.</summary>
public interface IDeviceWrite;

/// <summary>A Matter cluster command to invoke on a node endpoint.</summary>
public record CommandSpec(uint ClusterId, string CommandName, IReadOnlyDictionary<string, object?> Payload) : IDeviceWrite;

/// <summary>A Matter attribute write (used where a cluster is configured by attribute, not command).</summary>
public record AttributeWriteSpec(uint ClusterId, uint AttributeId, object? Value) : IDeviceWrite;
```

- [ ] **Step 2: Refactor `CommandMapping` to a handler registry (behaviour-preserving)**

Replace `src/Matterhorn/Devices/CommandMapping.cs` contents with:

```csharp
using System.Text.Json;
using Matterhorn.Matter;

namespace Matterhorn.Devices;

/// <summary>
/// Write path: maps a <c>/set</c> payload of semantic properties into ordered Matter write actions
/// (<see cref="CommandSpec"/> cluster commands and <see cref="AttributeWriteSpec"/> attribute writes).
/// Handlers run in registration order, so "on then set level" stays predictable.
/// </summary>
public static class CommandMapping
{
    private delegate void Handler(IReadOnlyDictionary<string, JsonElement> payload, List<IDeviceWrite> writes);

    private static readonly Handler[] Handlers =
    [
        State, Brightness, ColorTemp, ColorHueSat,
    ];

    public static IReadOnlyList<IDeviceWrite> Map(IReadOnlyDictionary<string, JsonElement> payload)
    {
        var writes = new List<IDeviceWrite>();
        foreach (var h in Handlers) h(payload, writes);
        return writes;
    }

    private static IReadOnlyDictionary<string, object?> Args(params (string, object?)[] kv) =>
        kv.ToDictionary(x => x.Item1, x => x.Item2);
    private static readonly IReadOnlyDictionary<string, object?> NoArgs = new Dictionary<string, object?>();

    private static void State(IReadOnlyDictionary<string, JsonElement> p, List<IDeviceWrite> w)
    {
        if (!p.TryGetValue("state", out var v)) return;
        switch (v.GetString()?.ToUpperInvariant())
        {
            case "ON": w.Add(new CommandSpec(MatterClusters.OnOff, "On", NoArgs)); break;
            case "OFF": w.Add(new CommandSpec(MatterClusters.OnOff, "Off", NoArgs)); break;
            case "TOGGLE": w.Add(new CommandSpec(MatterClusters.OnOff, "Toggle", NoArgs)); break;
        }
    }

    private static void Brightness(IReadOnlyDictionary<string, JsonElement> p, List<IDeviceWrite> w)
    {
        if (!p.TryGetValue("brightness", out var v)) return;
        w.Add(new CommandSpec(MatterClusters.LevelControl, "MoveToLevelWithOnOff",
            Args(("level", v.GetInt32()))));
    }

    private static void ColorTemp(IReadOnlyDictionary<string, JsonElement> p, List<IDeviceWrite> w)
    {
        if (!p.TryGetValue("color_temp", out var v)) return;
        w.Add(new CommandSpec(MatterClusters.ColorControl, "MoveToColorTemperature",
            Args(("colorTemperatureMireds", v.GetInt32()))));
    }

    private static void ColorHueSat(IReadOnlyDictionary<string, JsonElement> p, List<IDeviceWrite> w)
    {
        var hasHue = p.TryGetValue("hue", out var hue);
        var hasSat = p.TryGetValue("saturation", out var sat);
        if (hasHue && hasSat)
            w.Add(new CommandSpec(MatterClusters.ColorControl, "MoveToHueAndSaturation",
                Args(("hue", hue.GetInt32()), ("saturation", sat.GetInt32()))));
        else if (hasHue)
            w.Add(new CommandSpec(MatterClusters.ColorControl, "MoveToHue",
                Args(("hue", hue.GetInt32()), ("direction", 0))));
        else if (hasSat)
            w.Add(new CommandSpec(MatterClusters.ColorControl, "MoveToSaturation",
                Args(("saturation", sat.GetInt32()))));
    }
}
```

- [ ] **Step 3: Migrate existing `CommandMapping` tests to the sum type**

In `src/Matterhorn.Test/Devices/CommandMappingTests.cs`, add a cast helper at the top of the class and use it wherever a returned item's `.ClusterId`/`.CommandName`/`.Payload` is read:

```csharp
private static CommandSpec Cmd(IDeviceWrite w) => Assert.IsType<CommandSpec>(w);
```

Update each existing assertion to go through it, e.g.:

```csharp
[Fact]
public void State_ON_maps_to_OnOff_On()
{
    var c = Cmd(Assert.Single(CommandMapping.Map(Payload("""{"state":"ON"}"""))));
    Assert.Equal(MatterClusters.OnOff, c.ClusterId);
    Assert.Equal("On", c.CommandName);
}

[Fact]
public void Combined_payload_maps_to_multiple_commands_in_order()
{
    var cmds = CommandMapping.Map(Payload("""{"state":"ON","brightness":128}"""));
    Assert.Equal(2, cmds.Count);
    Assert.Equal("On", Cmd(cmds[0]).CommandName);
    Assert.Equal("MoveToLevelWithOnOff", Cmd(cmds[1]).CommandName);
}
```

Apply the same `Cmd(...)` wrap to `State_OFF...`, `Brightness_maps...`, `Color_temp_maps...`, `Hue_and_saturation...`, `Hue_only...`, `Saturation_only...`. Leave `Unknown_key_is_ignored` unchanged (it asserts `Empty`).

- [ ] **Step 4: Dispatch in the endpoint actor**

In `src/Matterhorn/Devices/MatterEndpointActor.cs`, replace the body of `OnSet` (~lines 65-72) with:

```csharp
private async Task OnSet(ApplySet msg)
{
    foreach (var action in CommandMapping.Map(msg.Payload))
    {
        try
        {
            switch (action)
            {
                case CommandSpec c:
                    await _controller.InvokeCommand(_nodeId, _endpoint, c, CancellationToken.None);
                    break;
                case AttributeWriteSpec a:
                    await _controller.WriteAttribute(_nodeId, _endpoint, a.ClusterId, a.AttributeId, a.Value, CancellationToken.None);
                    break;
            }
        }
        catch (Exception ex) { _log.Error(ex, "Write failed for {Name}", _name); }
    }
}
```

- [ ] **Step 5: Run the full suite**

Run: `dotnet test src/Matterhorn.Test`
Expected: PASS (migrated `CommandMappingTests` + all others green).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor: CommandMapping returns IDeviceWrite sum type; actor dispatches command vs attribute write"
```

---

### Task 3: `enum` expose type (`ExposeEntry.Values`) end-to-end

**Files:**
- Modify: `src/Matterhorn/Bridge/DeviceDescriptor.cs` (`ExposeEntry`)
- Modify: `contracts/matterhorn.openapi.yaml` (`Expose.values`)
- Modify: `src/Matterhorn/Api/MatterhornController.cs` (`ToDto`, ~line 208)
- Test: `src/Matterhorn.Test/Api/MatterhornControllerTests.cs`

**Interfaces:**
- Produces: `ExposeEntry(..., IReadOnlyList<string>? Values = null)`; `Type: "enum"` convention with `Values` populated.

- [ ] **Step 1: Extend the record**

In `src/Matterhorn/Bridge/DeviceDescriptor.cs`, change `ExposeEntry` to:

```csharp
/// <summary>A Z2M-style exposes entry. <see cref="Access"/> is a bitmask (1=published, 2=set, 4=get).
/// <see cref="Values"/> lists the allowed values for <c>type == "enum"</c>.</summary>
public record ExposeEntry(
    string Type, string Property, int Access,
    string? ValueOn = null, string? ValueOff = null,
    int? ValueMin = null, int? ValueMax = null, string? Unit = null,
    IReadOnlyList<string>? Values = null);
```

- [ ] **Step 2: Add `values` to the OpenAPI Expose schema**

In `contracts/matterhorn.openapi.yaml`, in the `Expose` schema `properties` block (after `unit:`), add:

```yaml
        values:
          type: array
          nullable: true
          items: { type: string }
        type: { type: string, description: 'binary | numeric | enum' }
```

(Update the existing `type:` line's description to include `enum`; keep the property once.)

- [ ] **Step 3: Rebuild to regenerate the DTO**

Run: `dotnet build`
Expected: build succeeds; `obj/generated/ApiContract.g.cs` now has `Gen.Expose.Values`.

- [ ] **Step 4: Map `Values` in the controller**

In `src/Matterhorn/Api/MatterhornController.cs`, in `ToDto` (~line 208), add to the initializer:

```csharp
        Values = e.Values?.ToList(),
```

- [ ] **Step 5: Write a test asserting an enum expose round-trips to the DTO**

Add to `src/Matterhorn.Test/Api/MatterhornControllerTests.cs` (follow the file's existing mapping-test style; if it tests `ToDto` indirectly via a descriptor→DTO path, mirror that). Minimal direct-style test:

```csharp
[Fact]
public void Enum_expose_maps_values_to_dto()
{
    var entry = new ExposeEntry("enum", "system_mode", 7, Values: new[] { "off", "heat" });
    var descriptor = new DeviceDescriptor("t", "1", 1, null, null, 0, 0, "Thermostat", true, new[] { entry }, "wifi");
    var dto = MatterhornController.ToDeviceDto(descriptor);   // use the actual mapping entry point in this file
    var e = Assert.Single(dto.Exposes);
    Assert.Equal("enum", e.Type);
    Assert.Equal(new[] { "off", "heat" }, e.Values);
}
```

Note: match the real mapper name/visibility in `MatterhornController` (the private `ToDto`/descriptor mapper seen at ~line 194). If it is `private`, assert through the existing public controller test path used elsewhere in this file rather than calling it directly.

- [ ] **Step 6: Run it, verify pass**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~Enum_expose_maps_values_to_dto"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: enum expose type with values, wired through OpenAPI + DTO"
```

---

## Phase 1 — Read-only sensors

### Task 4: sensor read rows + exposes

Pure read mappings; no write path. All rows are `Published`-only exposes.

**Files:**
- Modify: `src/Matterhorn/Matter/MatterClusters.cs`
- Modify: `src/Matterhorn/Devices/PropertyMapping.cs` (`Rules`)
- Modify: `src/Matterhorn/Bridge/ExposesBuilder.cs`
- Test: `src/Matterhorn.Test/Devices/PropertyMappingTests.cs`, `src/Matterhorn.Test/Bridge/ExposesBuilderTests.cs`

**Interfaces:**
- Produces: new properties `pressure`, `flow`, `smoke`, `carbon_monoxide`, `air_quality`, `co2`, `pm25`, `pm10`, `power`, `voltage`, `current`, `energy`, `battery_low`.

- [ ] **Step 1: Add cluster constants**

In `src/Matterhorn/Matter/MatterClusters.cs`, add after `PowerSource`:

```csharp
    public const uint PressureMeasurement = 0x0403;
    public const uint FlowMeasurement = 0x0404;
    public const uint SmokeCoAlarm = 0x005C;
    public const uint AirQuality = 0x005B;
    public const uint CarbonDioxideConcentration = 0x040D;
    public const uint Pm25Concentration = 0x042A;
    public const uint Pm10Concentration = 0x042D;
    public const uint ElectricalPowerMeasurement = 0x0090;
    public const uint ElectricalEnergyMeasurement = 0x0091;
```

- [ ] **Step 2: Write failing read tests**

Add to `src/Matterhorn.Test/Devices/PropertyMappingTests.cs`:

```csharp
[Fact]
public void Maps_pressure_tenths_kpa_to_hpa()
{
    var props = PropertyMapping.Map(new[] { R(MatterClusters.PressureMeasurement, 0, "10132") });
    Assert.Equal(1013.2, Assert.IsType<double>(props["pressure"]), 1);
}

[Fact]
public void Maps_smoke_state_nonzero_to_true()
{
    var props = PropertyMapping.Map(new[] { R(MatterClusters.SmokeCoAlarm, 1, "1") });
    Assert.True(Assert.IsType<bool>(props["smoke"]));
}

[Fact]
public void Maps_active_power_milliwatts_to_watts()
{
    var props = PropertyMapping.Map(new[] { R(MatterClusters.ElectricalPowerMeasurement, 8, "15500") });
    Assert.Equal(15.5, Assert.IsType<double>(props["power"]), 1);
}

[Fact]
public void Maps_battery_charge_level_warning_to_battery_low_true()
{
    var props = PropertyMapping.Map(new[] { R(MatterClusters.PowerSource, 14, "1") });
    Assert.True(Assert.IsType<bool>(props["battery_low"]));
}
```

- [ ] **Step 3: Run them, verify they fail**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~PropertyMappingTests"`
Expected: the four new tests FAIL (`KeyNotFoundException`).

- [ ] **Step 4: Add the read rules**

In `src/Matterhorn/Devices/PropertyMapping.cs`, append to the `Rules` array (before the closing `];`):

```csharp
        new(MatterClusters.PressureMeasurement, 0, "pressure", v => v.GetInt32() / 10.0),
        new(MatterClusters.FlowMeasurement, 0, "flow", v => v.GetInt32() / 10.0),
        new(MatterClusters.SmokeCoAlarm, 1, "smoke", v => v.GetInt32() != 0),
        new(MatterClusters.SmokeCoAlarm, 2, "carbon_monoxide", v => v.GetInt32() != 0),
        new(MatterClusters.AirQuality, 0, "air_quality",
            v => v.GetInt32() switch { 0 => "unknown", 1 => "good", 2 => "fair", 3 => "moderate", 4 => "poor", 5 => "very_poor", 6 => "extremely_poor", _ => "unknown" }),
        new(MatterClusters.CarbonDioxideConcentration, 0, "co2", v => v.GetDouble()),
        new(MatterClusters.Pm25Concentration, 0, "pm25", v => v.GetDouble()),
        new(MatterClusters.Pm10Concentration, 0, "pm10", v => v.GetDouble()),
        new(MatterClusters.ElectricalPowerMeasurement, 8, "power", v => v.GetInt64() / 1000.0),
        new(MatterClusters.ElectricalPowerMeasurement, 4, "voltage", v => v.GetInt64() / 1000.0),
        new(MatterClusters.ElectricalPowerMeasurement, 5, "current", v => v.GetInt64() / 1000.0),
        new(MatterClusters.ElectricalEnergyMeasurement, 1, "energy", v => v.GetInt64() / 1_000_000.0),
        new(MatterClusters.PowerSource, 14, "battery_low", v => v.GetInt32() != 0),
```

Note: concentration-measurement `MeasuredValue` is a float per spec; `GetDouble()` tolerates integer JSON too. Energy attribute id (`CumulativeEnergyImported`, here `1`) and its struct shape vary by matter.js version — confirm against a live meter and adjust this single row if the reading is a nested struct rather than a scalar.

- [ ] **Step 5: Run read tests, verify pass**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~PropertyMappingTests"`
Expected: PASS.

- [ ] **Step 6: Write failing exposes tests**

Add to `src/Matterhorn.Test/Bridge/ExposesBuilderTests.cs`:

```csharp
[Fact]
public void Pressure_cluster_exposes_read_only_pressure()
{
    var exposes = ExposesBuilder.Build(new[] { MatterClusters.PressureMeasurement }, 0);
    var e = Assert.Single(exposes, x => x.Property == "pressure");
    Assert.Equal("numeric", e.Type);
    Assert.Equal(1, e.Access);          // Published only
    Assert.Equal("hPa", e.Unit);
}

[Fact]
public void Power_measurement_exposes_power_voltage_current()
{
    var exposes = ExposesBuilder.Build(new[] { MatterClusters.ElectricalPowerMeasurement }, 0);
    Assert.Contains(exposes, x => x.Property == "power" && x.Unit == "W");
    Assert.Contains(exposes, x => x.Property == "voltage" && x.Unit == "V");
    Assert.Contains(exposes, x => x.Property == "current" && x.Unit == "A");
}
```

- [ ] **Step 7: Run, verify fail**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~ExposesBuilderTests"`
Expected: the two new tests FAIL.

- [ ] **Step 8: Add the exposes rows**

In `src/Matterhorn/Bridge/ExposesBuilder.cs`, before `return list;`, add:

```csharp
        if (has.Contains(MatterClusters.PressureMeasurement))
            list.Add(new("numeric", "pressure", Published, Unit: "hPa"));
        if (has.Contains(MatterClusters.FlowMeasurement))
            list.Add(new("numeric", "flow", Published, Unit: "m³/h"));
        if (has.Contains(MatterClusters.SmokeCoAlarm))
        {
            list.Add(new("binary", "smoke", Published));
            list.Add(new("binary", "carbon_monoxide", Published));
        }
        if (has.Contains(MatterClusters.AirQuality))
            list.Add(new("enum", "air_quality", Published,
                Values: new[] { "unknown", "good", "fair", "moderate", "poor", "very_poor", "extremely_poor" }));
        if (has.Contains(MatterClusters.CarbonDioxideConcentration))
            list.Add(new("numeric", "co2", Published, Unit: "ppm"));
        if (has.Contains(MatterClusters.Pm25Concentration))
            list.Add(new("numeric", "pm25", Published, Unit: "µg/m³"));
        if (has.Contains(MatterClusters.Pm10Concentration))
            list.Add(new("numeric", "pm10", Published, Unit: "µg/m³"));
        if (has.Contains(MatterClusters.ElectricalPowerMeasurement))
        {
            list.Add(new("numeric", "power", Published, Unit: "W"));
            list.Add(new("numeric", "voltage", Published, Unit: "V"));
            list.Add(new("numeric", "current", Published, Unit: "A"));
        }
        if (has.Contains(MatterClusters.ElectricalEnergyMeasurement))
            list.Add(new("numeric", "energy", Published, Unit: "kWh"));
```

Note: `battery_low` shares the `PowerSource` cluster already handled for `battery`; add it inside the existing `PowerSource` block:

```csharp
        if (has.Contains(MatterClusters.PowerSource))
        {
            list.Add(new("numeric", "battery", Published, ValueMin: 0, ValueMax: 100, Unit: "%"));
            list.Add(new("binary", "battery_low", Published));
        }
```

(Replace the existing single-line `PowerSource` add with this block.)

- [ ] **Step 9: Run exposes tests + full suite**

Run: `dotnet test src/Matterhorn.Test`
Expected: PASS.

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "feat: read-only sensor clusters (pressure, flow, smoke/CO, air quality, PM/CO2, power/energy, battery_low)"
```

---

## Phase 2 — Command-driven categories

### Task 5: Window Covering (cover)

**Files:**
- Modify: `src/Matterhorn/Matter/MatterClusters.cs` (`WindowCovering`)
- Modify: `src/Matterhorn/Devices/PropertyMapping.cs` (position, inverted)
- Modify: `src/Matterhorn/Devices/CommandMapping.cs` (add `Cover` handler + register)
- Modify: `src/Matterhorn/Bridge/ExposesBuilder.cs`
- Test: `PropertyMappingTests`, `CommandMappingTests`, `ExposesBuilderTests`

**Interfaces:**
- Produces: property `position` (0–100, inverted); `state` values `OPEN`/`CLOSE`/`STOP`.
- Consumes (Task 2): `CommandSpec`, the `Handlers` registry, the value-based `state` dispatch convention.

- [ ] **Step 1: Add the cluster constant**

In `src/Matterhorn/Matter/MatterClusters.cs`:

```csharp
    public const uint WindowCovering = 0x0102;
```

- [ ] **Step 2: Write failing read test (inverted position)**

Add to `PropertyMappingTests`:

```csharp
[Fact]
public void Maps_cover_lift_percent_inverted_to_position()
{
    // Matter 30% closed-from-open -> Z2M position 70 (open-ness).
    var props = PropertyMapping.Map(new[] { R(MatterClusters.WindowCovering, 8, "30") });
    Assert.Equal(70, Assert.IsType<int>(props["position"]));
}
```

- [ ] **Step 3: Run, verify fail**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~Maps_cover_lift_percent_inverted_to_position"`
Expected: FAIL.

- [ ] **Step 4: Add the read rule**

In `PropertyMapping.cs` `Rules`:

```csharp
        new(MatterClusters.WindowCovering, 8, "position", v => 100 - v.GetInt32()),
```

- [ ] **Step 5: Run, verify pass**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~Maps_cover_lift_percent_inverted_to_position"`
Expected: PASS.

- [ ] **Step 6: Write failing write tests**

Add to `CommandMappingTests`:

```csharp
[Fact]
public void State_OPEN_maps_to_WindowCovering_UpOrOpen()
{
    var c = Cmd(Assert.Single(CommandMapping.Map(Payload("""{"state":"OPEN"}"""))));
    Assert.Equal(MatterClusters.WindowCovering, c.ClusterId);
    Assert.Equal("UpOrOpen", c.CommandName);
}

[Fact]
public void State_STOP_maps_to_WindowCovering_StopMotion()
{
    var c = Cmd(Assert.Single(CommandMapping.Map(Payload("""{"state":"STOP"}"""))));
    Assert.Equal("StopMotion", c.CommandName);
}

[Fact]
public void Position_maps_to_GoToLiftPercentage_inverted()
{
    var c = Cmd(Assert.Single(CommandMapping.Map(Payload("""{"position":70}"""))));
    Assert.Equal(MatterClusters.WindowCovering, c.ClusterId);
    Assert.Equal("GoToLiftPercentage", c.CommandName);
    Assert.Equal(30, Assert.IsType<int>(c.Payload["liftPercentageValue"]));
}
```

- [ ] **Step 7: Run, verify fail**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~CommandMappingTests"`
Expected: the three new tests FAIL.

- [ ] **Step 8: Add the cover write handler + register + extend State**

In `CommandMapping.cs`, extend the `State` handler's switch with cover/lock-agnostic cover cases:

```csharp
            case "OPEN": w.Add(new CommandSpec(MatterClusters.WindowCovering, "UpOrOpen", NoArgs)); break;
            case "CLOSE": w.Add(new CommandSpec(MatterClusters.WindowCovering, "DownOrClose", NoArgs)); break;
            case "STOP": w.Add(new CommandSpec(MatterClusters.WindowCovering, "StopMotion", NoArgs)); break;
```

Add a new handler:

```csharp
    private static void CoverPosition(IReadOnlyDictionary<string, JsonElement> p, List<IDeviceWrite> w)
    {
        if (!p.TryGetValue("position", out var v)) return;
        w.Add(new CommandSpec(MatterClusters.WindowCovering, "GoToLiftPercentage",
            Args(("liftPercentageValue", 100 - v.GetInt32()))));
    }
```

and register it in `Handlers`:

```csharp
        State, Brightness, ColorTemp, ColorHueSat, CoverPosition,
```

- [ ] **Step 9: Run, verify pass**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~CommandMappingTests"`
Expected: PASS.

- [ ] **Step 10: Write failing exposes test**

Add to `ExposesBuilderTests`:

```csharp
[Fact]
public void Window_covering_exposes_state_enum_and_position()
{
    var exposes = ExposesBuilder.Build(new[] { MatterClusters.WindowCovering }, 0);
    var state = Assert.Single(exposes, x => x.Property == "state");
    Assert.Equal("enum", state.Type);
    Assert.Equal(new[] { "OPEN", "CLOSE", "STOP" }, state.Values);
    Assert.True((state.Access & 2) != 0);   // settable
    var pos = Assert.Single(exposes, x => x.Property == "position");
    Assert.Equal(0, pos.ValueMin);
    Assert.Equal(100, pos.ValueMax);
}
```

- [ ] **Step 11: Run, verify fail; add exposes; run, verify pass**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~Window_covering_exposes_state_enum_and_position"` (FAIL).
Add to `ExposesBuilder.cs`:

```csharp
        if (has.Contains(MatterClusters.WindowCovering))
        {
            list.Add(new("enum", "state", Set, Values: new[] { "OPEN", "CLOSE", "STOP" }));
            list.Add(new("numeric", "position", All, ValueMin: 0, ValueMax: 100, Unit: "%"));
        }
```

Re-run the filter: PASS.

- [ ] **Step 12: Commit**

```bash
git add -A
git commit -m "feat: window covering (cover) read/command/exposes with Z2M position inversion"
```

---

### Task 6: Door Lock (lock)

**Files:**
- Modify: `MatterClusters.cs` (`DoorLock`)
- Modify: `PropertyMapping.cs` (LockState → `state`)
- Modify: `CommandMapping.cs` (extend `State` with LOCK/UNLOCK)
- Modify: `ExposesBuilder.cs`
- Test: `PropertyMappingTests`, `CommandMappingTests`, `ExposesBuilderTests`

**Interfaces:**
- Produces: `state` values `LOCK`/`UNLOCK` on cluster 0x0101.

- [ ] **Step 1: Add the cluster constant**

```csharp
    public const uint DoorLock = 0x0101;
```

- [ ] **Step 2: Failing read test**

Add to `PropertyMappingTests`:

```csharp
[Fact]
public void Maps_lockstate_locked_to_state_LOCK()
{
    var props = PropertyMapping.Map(new[] { R(MatterClusters.DoorLock, 0, "1") });
    Assert.Equal("LOCK", props["state"]);
}

[Fact]
public void Maps_lockstate_unlocked_to_state_UNLOCK()
{
    var props = PropertyMapping.Map(new[] { R(MatterClusters.DoorLock, 0, "2") });
    Assert.Equal("UNLOCK", props["state"]);
}
```

- [ ] **Step 3: Run (FAIL), add rule, run (PASS)**

Add to `PropertyMapping.cs` `Rules`:

```csharp
        new(MatterClusters.DoorLock, 0, "state",
            v => v.GetInt32() switch { 1 => "LOCK", 2 => "UNLOCK", _ => "UNKNOWN" }),
```

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~Maps_lockstate"` → PASS.

- [ ] **Step 4: Failing write test**

Add to `CommandMappingTests`:

```csharp
[Fact]
public void State_LOCK_maps_to_DoorLock_LockDoor()
{
    var c = Cmd(Assert.Single(CommandMapping.Map(Payload("""{"state":"LOCK"}"""))));
    Assert.Equal(MatterClusters.DoorLock, c.ClusterId);
    Assert.Equal("LockDoor", c.CommandName);
}

[Fact]
public void State_UNLOCK_maps_to_DoorLock_UnlockDoor()
{
    var c = Cmd(Assert.Single(CommandMapping.Map(Payload("""{"state":"UNLOCK"}"""))));
    Assert.Equal("UnlockDoor", c.CommandName);
}
```

- [ ] **Step 5: Run (FAIL), extend `State` handler, run (PASS)**

Add to the `State` switch in `CommandMapping.cs`:

```csharp
            case "LOCK": w.Add(new CommandSpec(MatterClusters.DoorLock, "LockDoor", NoArgs)); break;
            case "UNLOCK": w.Add(new CommandSpec(MatterClusters.DoorLock, "UnlockDoor", NoArgs)); break;
```

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~DoorLock"` → PASS.

- [ ] **Step 6: Failing exposes test → add → pass**

Add to `ExposesBuilderTests`:

```csharp
[Fact]
public void Door_lock_exposes_state_enum_lock_unlock()
{
    var exposes = ExposesBuilder.Build(new[] { MatterClusters.DoorLock }, 0);
    var state = Assert.Single(exposes, x => x.Property == "state");
    Assert.Equal("enum", state.Type);
    Assert.Equal(new[] { "LOCK", "UNLOCK" }, state.Values);
    Assert.Equal(7, state.Access);   // All
}
```

Add to `ExposesBuilder.cs`:

```csharp
        if (has.Contains(MatterClusters.DoorLock))
            list.Add(new("enum", "state", All, Values: new[] { "LOCK", "UNLOCK" }));
```

Run the filter → PASS.

- [ ] **Step 7: Full suite + commit**

Run: `dotnet test src/Matterhorn.Test` → PASS.

```bash
git add -A
git commit -m "feat: door lock (lock) read/command/exposes"
```

---

## Phase 3 — Attribute-write categories

### Task 7: Thermostat (climate)

**Files:**
- Modify: `MatterClusters.cs` (`Thermostat`)
- Modify: `PropertyMapping.cs` (local temp, setpoints, system_mode)
- Modify: `CommandMapping.cs` (add `Thermostat` handler → `AttributeWriteSpec`)
- Modify: `ExposesBuilder.cs`
- Test: `PropertyMappingTests`, `CommandMappingTests`, `ExposesBuilderTests`

**Interfaces:**
- Produces: `local_temperature` (°C, read), `occupied_heating_setpoint`/`occupied_cooling_setpoint` (°C, read+set), `system_mode` enum `off`/`auto`/`cool`/`heat`.
- Consumes (Task 2): `AttributeWriteSpec`.

- [ ] **Step 1: Add cluster + failing read tests**

```csharp
    public const uint Thermostat = 0x0201;
```

Add to `PropertyMappingTests`:

```csharp
[Fact]
public void Maps_thermostat_local_temperature_hundredths_to_celsius()
{
    var props = PropertyMapping.Map(new[] { R(MatterClusters.Thermostat, 0, "2150") });
    Assert.Equal(21.5, Assert.IsType<double>(props["local_temperature"]), 2);
}

[Fact]
public void Maps_thermostat_system_mode_heat()
{
    var props = PropertyMapping.Map(new[] { R(MatterClusters.Thermostat, 0x1C, "4") });
    Assert.Equal("heat", props["system_mode"]);
}
```

- [ ] **Step 2: Run (FAIL), add rules, run (PASS)**

Add to `PropertyMapping.cs` `Rules`:

```csharp
        new(MatterClusters.Thermostat, 0x00, "local_temperature", v => v.GetInt32() / 100.0),
        new(MatterClusters.Thermostat, 0x12, "occupied_heating_setpoint", v => v.GetInt32() / 100.0),
        new(MatterClusters.Thermostat, 0x11, "occupied_cooling_setpoint", v => v.GetInt32() / 100.0),
        new(MatterClusters.Thermostat, 0x1C, "system_mode",
            v => v.GetInt32() switch { 0 => "off", 1 => "auto", 3 => "cool", 4 => "heat", _ => "unknown" }),
```

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~Maps_thermostat"` → PASS.

- [ ] **Step 3: Failing write tests (attribute writes)**

Add to `CommandMappingTests` a cast helper for attribute writes at the top of the class:

```csharp
private static AttributeWriteSpec Attr(IDeviceWrite w) => Assert.IsType<AttributeWriteSpec>(w);
```

Then:

```csharp
[Fact]
public void Heating_setpoint_maps_to_attribute_write_hundredths()
{
    var a = Attr(Assert.Single(CommandMapping.Map(Payload("""{"occupied_heating_setpoint":21.5}"""))));
    Assert.Equal(MatterClusters.Thermostat, a.ClusterId);
    Assert.Equal(0x12u, a.AttributeId);
    Assert.Equal(2150, Assert.IsType<int>(a.Value));
}

[Fact]
public void System_mode_heat_maps_to_attribute_write_enum()
{
    var a = Attr(Assert.Single(CommandMapping.Map(Payload("""{"system_mode":"heat"}"""))));
    Assert.Equal(0x1Cu, a.AttributeId);
    Assert.Equal(4, Assert.IsType<int>(a.Value));
}
```

- [ ] **Step 4: Run (FAIL), add handler + register, run (PASS)**

Add to `CommandMapping.cs`:

```csharp
    private static void Thermostat(IReadOnlyDictionary<string, JsonElement> p, List<IDeviceWrite> w)
    {
        if (p.TryGetValue("occupied_heating_setpoint", out var h))
            w.Add(new AttributeWriteSpec(MatterClusters.Thermostat, 0x12, (int)Math.Round(h.GetDouble() * 100)));
        if (p.TryGetValue("occupied_cooling_setpoint", out var c))
            w.Add(new AttributeWriteSpec(MatterClusters.Thermostat, 0x11, (int)Math.Round(c.GetDouble() * 100)));
        if (p.TryGetValue("system_mode", out var m))
        {
            var mode = m.GetString() switch { "off" => 0, "auto" => 1, "cool" => 3, "heat" => 4, _ => -1 };
            if (mode >= 0) w.Add(new AttributeWriteSpec(MatterClusters.Thermostat, 0x1C, mode));
        }
    }
```

Register in `Handlers`:

```csharp
        State, Brightness, ColorTemp, ColorHueSat, CoverPosition, Thermostat,
```

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~setpoint OR FullyQualifiedName~System_mode"` → PASS. (Or run the whole `CommandMappingTests` class.)

- [ ] **Step 5: Failing exposes test → add → pass**

Add to `ExposesBuilderTests`:

```csharp
[Fact]
public void Thermostat_exposes_temp_setpoints_and_mode()
{
    var exposes = ExposesBuilder.Build(new[] { MatterClusters.Thermostat }, 0);
    Assert.Contains(exposes, x => x.Property == "local_temperature" && x.Access == 1);
    Assert.Contains(exposes, x => x.Property == "occupied_heating_setpoint" && (x.Access & 2) != 0);
    var mode = Assert.Single(exposes, x => x.Property == "system_mode");
    Assert.Equal(new[] { "off", "auto", "cool", "heat" }, mode.Values);
}
```

Add to `ExposesBuilder.cs`:

```csharp
        if (has.Contains(MatterClusters.Thermostat))
        {
            list.Add(new("numeric", "local_temperature", Published, Unit: "°C"));
            list.Add(new("numeric", "occupied_heating_setpoint", All, ValueMin: 5, ValueMax: 35, Unit: "°C"));
            list.Add(new("numeric", "occupied_cooling_setpoint", All, ValueMin: 5, ValueMax: 35, Unit: "°C"));
            list.Add(new("enum", "system_mode", All, Values: new[] { "off", "auto", "cool", "heat" }));
        }
```

Run the filter → PASS.

- [ ] **Step 6: Full suite + commit**

```bash
git add -A
git commit -m "feat: thermostat (climate) read + attribute-write setpoints/mode + exposes"
```

---

### Task 8: Fan Control (fan)

**Files:**
- Modify: `MatterClusters.cs` (`FanControl`)
- Modify: `PropertyMapping.cs` (fan_mode, percent)
- Modify: `CommandMapping.cs` (add `Fan` handler → `AttributeWriteSpec`)
- Modify: `ExposesBuilder.cs`
- Test: as above

**Interfaces:**
- Produces: `fan_mode` enum `off`/`low`/`medium`/`high`/`on`/`auto`, `percent` 0–100.

- [ ] **Step 1: Add cluster + failing read tests**

```csharp
    public const uint FanControl = 0x0202;
```

Add to `PropertyMappingTests`:

```csharp
[Fact]
public void Maps_fan_mode_medium()
{
    var props = PropertyMapping.Map(new[] { R(MatterClusters.FanControl, 0, "2") });
    Assert.Equal("medium", props["fan_mode"]);
}

[Fact]
public void Maps_fan_percent_current()
{
    var props = PropertyMapping.Map(new[] { R(MatterClusters.FanControl, 6, "60") });
    Assert.Equal(60, Assert.IsType<int>(props["percent"]));
}
```

- [ ] **Step 2: Run (FAIL), add rules, run (PASS)**

Add to `PropertyMapping.cs` `Rules`:

```csharp
        new(MatterClusters.FanControl, 0, "fan_mode",
            v => v.GetInt32() switch { 0 => "off", 1 => "low", 2 => "medium", 3 => "high", 4 => "on", 5 => "auto", _ => "unknown" }),
        new(MatterClusters.FanControl, 6, "percent", v => v.GetInt32()),
```

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~Maps_fan"` → PASS.

- [ ] **Step 3: Failing write tests**

Add to `CommandMappingTests`:

```csharp
[Fact]
public void Fan_mode_medium_maps_to_attribute_write()
{
    var a = Attr(Assert.Single(CommandMapping.Map(Payload("""{"fan_mode":"medium"}"""))));
    Assert.Equal(MatterClusters.FanControl, a.ClusterId);
    Assert.Equal(0x00u, a.AttributeId);
    Assert.Equal(2, Assert.IsType<int>(a.Value));
}

[Fact]
public void Fan_percent_maps_to_percent_setting_write()
{
    var a = Attr(Assert.Single(CommandMapping.Map(Payload("""{"percent":60}"""))));
    Assert.Equal(0x02u, a.AttributeId);
    Assert.Equal(60, Assert.IsType<int>(a.Value));
}
```

- [ ] **Step 4: Run (FAIL), add handler + register, run (PASS)**

Add to `CommandMapping.cs`:

```csharp
    private static void Fan(IReadOnlyDictionary<string, JsonElement> p, List<IDeviceWrite> w)
    {
        if (p.TryGetValue("fan_mode", out var m))
        {
            var mode = m.GetString() switch { "off" => 0, "low" => 1, "medium" => 2, "high" => 3, "on" => 4, "auto" => 5, _ => -1 };
            if (mode >= 0) w.Add(new AttributeWriteSpec(MatterClusters.FanControl, 0x00, mode));
        }
        if (p.TryGetValue("percent", out var pct))
            w.Add(new AttributeWriteSpec(MatterClusters.FanControl, 0x02, pct.GetInt32()));
    }
```

Register in `Handlers`:

```csharp
        State, Brightness, ColorTemp, ColorHueSat, CoverPosition, Thermostat, Fan,
```

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~Fan_"` → PASS.

- [ ] **Step 5: Failing exposes test → add → pass**

Add to `ExposesBuilderTests`:

```csharp
[Fact]
public void Fan_control_exposes_mode_enum_and_percent()
{
    var exposes = ExposesBuilder.Build(new[] { MatterClusters.FanControl }, 0);
    var mode = Assert.Single(exposes, x => x.Property == "fan_mode");
    Assert.Equal(new[] { "off", "low", "medium", "high", "on", "auto" }, mode.Values);
    Assert.Contains(exposes, x => x.Property == "percent" && x.ValueMax == 100);
}
```

Add to `ExposesBuilder.cs`:

```csharp
        if (has.Contains(MatterClusters.FanControl))
        {
            list.Add(new("enum", "fan_mode", All, Values: new[] { "off", "low", "medium", "high", "on", "auto" }));
            list.Add(new("numeric", "percent", All, ValueMin: 0, ValueMax: 100, Unit: "%"));
        }
```

Run the filter → PASS.

- [ ] **Step 6: Full suite + commit**

```bash
git add -A
git commit -m "feat: fan control (fan) read + attribute-write mode/percent + exposes"
```

---

## Phase 4 — Light polish

### Task 9: Color xy

**Files:** `PropertyMapping.cs`, `CommandMapping.cs` (new `ColorXy` handler), `ExposesBuilder.cs`, tests.

**Interfaces:** `color_x`/`color_y` (0–1 floats) on cluster 0x0300 attrs 3/4; write via `MoveToColor`.

- [ ] **Step 1: Failing read test**

```csharp
[Fact]
public void Maps_color_x_from_currentx_to_unit_fraction()
{
    // CurrentX is 0..65279 in 1/65536 units; 32768 ~= 0.5.
    var props = PropertyMapping.Map(new[] { R(MatterClusters.ColorControl, 3, "32768") });
    Assert.Equal(0.5, Assert.IsType<double>(props["color_x"]), 2);
}
```

- [ ] **Step 2: Run (FAIL), add rules, run (PASS)**

Add to `PropertyMapping.cs` `Rules`:

```csharp
        new(MatterClusters.ColorControl, 3, "color_x", v => v.GetInt32() / 65536.0),
        new(MatterClusters.ColorControl, 4, "color_y", v => v.GetInt32() / 65536.0),
```

Run filter → PASS.

- [ ] **Step 3: Failing write test**

```csharp
[Fact]
public void Color_xy_together_map_to_MoveToColor()
{
    var c = Cmd(Assert.Single(CommandMapping.Map(Payload("""{"color_x":0.5,"color_y":0.4}"""))));
    Assert.Equal(MatterClusters.ColorControl, c.ClusterId);
    Assert.Equal("MoveToColor", c.CommandName);
    Assert.Equal(32768, Assert.IsType<int>(c.Payload["colorX"]));
    Assert.Equal(26214, Assert.IsType<int>(c.Payload["colorY"]));
}
```

- [ ] **Step 4: Run (FAIL), add handler + register, run (PASS)**

Add to `CommandMapping.cs`:

```csharp
    private static void ColorXy(IReadOnlyDictionary<string, JsonElement> p, List<IDeviceWrite> w)
    {
        if (!p.TryGetValue("color_x", out var x) || !p.TryGetValue("color_y", out var y)) return;
        w.Add(new CommandSpec(MatterClusters.ColorControl, "MoveToColor",
            Args(("colorX", (int)Math.Round(x.GetDouble() * 65536)), ("colorY", (int)Math.Round(y.GetDouble() * 65536)))));
    }
```

Register after `ColorHueSat`:

```csharp
        State, Brightness, ColorTemp, ColorHueSat, ColorXy, CoverPosition, Thermostat, Fan,
```

Run filter → PASS.

- [ ] **Step 5: Exposes + commit**

Add to the existing `ColorControl` block in `ExposesBuilder.cs` (after the color-temp branch), gated on the xy feature bit (`colorFeatures & 0x08`, "XY"):

```csharp
            if ((colorFeatures & 0x08) != 0) // XY
            {
                list.Add(new("numeric", "color_x", All, ValueMin: 0, ValueMax: 1));
                list.Add(new("numeric", "color_y", All, ValueMin: 0, ValueMax: 1));
            }
```

Add an exposes test asserting color_x/color_y appear when `colorFeatures` has bit `0x08`, then:

```bash
dotnet test src/Matterhorn.Test
git add -A
git commit -m "feat: color xy read/command/exposes"
```

---

### Task 10: Level transition modifier

`transition` is a modifier on `brightness`, not a standalone property (no expose).

**Files:** `CommandMapping.cs` (`Brightness` handler), `CommandMappingTests`.

- [ ] **Step 1: Failing test**

```csharp
[Fact]
public void Brightness_with_transition_sets_transitionTime_tenths()
{
    var c = Cmd(Assert.Single(CommandMapping.Map(Payload("""{"brightness":100,"transition":2}"""))));
    Assert.Equal("MoveToLevelWithOnOff", c.CommandName);
    Assert.Equal(100, Assert.IsType<int>(c.Payload["level"]));
    Assert.Equal(20, Assert.IsType<int>(c.Payload["transitionTime"]));   // 2s -> 20 (0.1s units)
}

[Fact]
public void Brightness_without_transition_still_single_arg()
{
    var c = Cmd(Assert.Single(CommandMapping.Map(Payload("""{"brightness":100}"""))));
    Assert.False(c.Payload.ContainsKey("transitionTime"));
}
```

- [ ] **Step 2: Run (FAIL), update `Brightness` handler, run (PASS)**

Replace the `Brightness` handler body in `CommandMapping.cs`:

```csharp
    private static void Brightness(IReadOnlyDictionary<string, JsonElement> p, List<IDeviceWrite> w)
    {
        if (!p.TryGetValue("brightness", out var v)) return;
        var args = new Dictionary<string, object?> { ["level"] = v.GetInt32() };
        if (p.TryGetValue("transition", out var t))
            args["transitionTime"] = (int)Math.Round(t.GetDouble() * 10);
        w.Add(new CommandSpec(MatterClusters.LevelControl, "MoveToLevelWithOnOff", args));
    }
```

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~transition"` → PASS.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "feat: brightness transition modifier -> MoveToLevelWithOnOff transitionTime"
```

---

### Task 11: On/Off effect + Identify

**Files:** `MatterClusters.cs` (`Identify`), `CommandMapping.cs` (`Effect`, `Identify` handlers), `ExposesBuilder.cs`, tests.

**Interfaces:** `effect` enum (`blink`) → `OnOff.OffWithEffect`; `identify` numeric (seconds) → `Identify.Identify`.

- [ ] **Step 1: Add cluster + failing write tests**

```csharp
    public const uint Identify = 0x0003;
```

Add to `CommandMappingTests`:

```csharp
[Fact]
public void Identify_seconds_maps_to_Identify_command()
{
    var c = Cmd(Assert.Single(CommandMapping.Map(Payload("""{"identify":5}"""))));
    Assert.Equal(MatterClusters.Identify, c.ClusterId);
    Assert.Equal("Identify", c.CommandName);
    Assert.Equal(5, Assert.IsType<int>(c.Payload["identifyTime"]));
}

[Fact]
public void Effect_blink_maps_to_OffWithEffect()
{
    var c = Cmd(Assert.Single(CommandMapping.Map(Payload("""{"effect":"blink"}"""))));
    Assert.Equal(MatterClusters.OnOff, c.ClusterId);
    Assert.Equal("OffWithEffect", c.CommandName);
}
```

- [ ] **Step 2: Run (FAIL), add handlers + register, run (PASS)**

Add to `CommandMapping.cs`:

```csharp
    private static void Effect(IReadOnlyDictionary<string, JsonElement> p, List<IDeviceWrite> w)
    {
        if (!p.TryGetValue("effect", out _)) return;
        w.Add(new CommandSpec(MatterClusters.OnOff, "OffWithEffect",
            Args(("effectIdentifier", 0), ("effectVariant", 0))));
    }

    private static void IdentifyCmd(IReadOnlyDictionary<string, JsonElement> p, List<IDeviceWrite> w)
    {
        if (!p.TryGetValue("identify", out var v)) return;
        w.Add(new CommandSpec(MatterClusters.Identify, "Identify", Args(("identifyTime", v.GetInt32()))));
    }
```

Register:

```csharp
        State, Brightness, ColorTemp, ColorHueSat, ColorXy, CoverPosition, Thermostat, Fan, Effect, IdentifyCmd,
```

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~Identify_seconds OR FullyQualifiedName~Effect_blink"` → PASS.

- [ ] **Step 3: Exposes (identify button + effect enum) → add → pass**

Add to `ExposesBuilder.cs`:

```csharp
        if (has.Contains(MatterClusters.Identify))
            list.Add(new("numeric", "identify", Set, ValueMin: 0, ValueMax: 60, Unit: "s"));
```

(Effect is optional/OnOff-gated; only add an `effect` enum expose if a demo device needs it — skip for YAGNI unless the fake seeds one.)

Add an exposes test asserting `identify` is present and set-only when the `Identify` cluster is reported.

- [ ] **Step 4: Full suite + commit**

```bash
dotnet test src/Matterhorn.Test
git add -A
git commit -m "feat: identify command + on/off effect write mappings"
```

---

## Phase 5 — Dashboard

Dashboard has no automated tests; verify each task with `docker compose up --build` (fake controller + demo devices; dashboard http://localhost:16090). To exercise the new categories, first extend the demo seeder (see Task 12 Step 1).

### Task 12: Generic settable + enum control + demo devices

**Files:**
- Modify: `src/Matterhorn/Dev/DemoDeviceSeeder.cs` (seed a cover, lock, thermostat, fan for manual verification)
- Modify: `src/Matterhorn/wwwroot/js/views/devices.js`
- Modify: `src/Matterhorn/wwwroot/app.css`

- [ ] **Step 1: Seed demo devices for the new categories**

In `src/Matterhorn/Dev/DemoDeviceSeeder.cs`, add nodes whose endpoints report the new clusters (follow the file's existing `NodeAdded`/`EndpointInfo` seeding pattern, using the cluster ids from `MatterClusters`: `WindowCovering`, `DoorLock`, `Thermostat`, `FanControl`, and a sensor bundle). Emit a couple of `AttributeChanged` seed readings each (e.g. cover `position` 8→"40", lock LockState 0→"1", thermostat local temp + setpoint + mode, fan mode + percent) so the cards render with live values.

- [ ] **Step 2: Generalize settable detection**

In `src/Matterhorn/wwwroot/js/views/devices.js`, replace the hardcoded `SETTABLE` gate. Change the `station(d)` split (lines ~78-81) to:

```javascript
  const hasColor = d.exposes.some(e=>e.property==='hue') && d.exposes.some(e=>e.property==='saturation');
  const skip = new Set(hasColor ? ['hue','saturation'] : []);
  skip.add('transition');                                   // modifier, not a control
  const settable = d.exposes.filter(e=>(e.access&2) && !skip.has(e.property));
  const readonly = d.exposes.filter(e=>!(e.access&2) && !skip.has(e.property));
```

Delete the now-unused `const SETTABLE = new Set(['state','brightness','color_temp']);` line.

- [ ] **Step 3: Add an enum control renderer**

In `control(name,e)`, add an enum branch before the numeric `else`. The full function becomes:

```javascript
function control(name,e){
  const wrap=document.createElement('div');
  if (e.type==='enum'){
    wrap.className='ctl';
    const label=esc(e.property.replace('_',' '));
    const btns=(e.values||[]).map(v=>`<button class="seg" data-prop="${e.property}" data-val="${esc(v)}">${esc(v.toLowerCase())}</button>`).join('');
    wrap.innerHTML=`<div class="row"><span class="legend">${label}</span></div><div class="segmented">${btns}</div>`;
    wrap.querySelectorAll('button.seg').forEach(b=>
      b.addEventListener('click',()=>patch(name,{[e.property]:b.dataset.val})));
  } else if (e.property==='state'){
    wrap.className='row';
    wrap.innerHTML=`<span class="legend">state</span>
      <span class="switch"><input type="checkbox" data-prop="state"><span class="slot"></span><span class="knob"></span></span>`;
    wrap.querySelector('input').addEventListener('change',ev=>patch(name,{state:ev.target.checked?'ON':'OFF'}));
  } else {
    wrap.className='ctl';
    const min=e.value_min??0, max=e.value_max??254;
    const label=esc(e.property.replace('_',' '));
    wrap.innerHTML=`<div class="row"><span class="legend">${label}</span>
        <input class="entry" type="number" inputmode="numeric" min="${min}" max="${max}" data-prop="${e.property}-val" aria-label="${label} value"></div>
      <input type="range" min="${min}" max="${max}" data-prop="${e.property}">`;
    const s=wrap.querySelector('input[type=range]');
    const box=wrap.querySelector('input[type=number]');
    const clamp=v=>Math.min(max,Math.max(min,Math.round(v)));
    const startDrag=()=>s.dataset.dragging='1', endDrag=()=>delete s.dataset.dragging;
    s.addEventListener('pointerdown',startDrag);
    s.addEventListener('pointerup',endDrag);
    s.addEventListener('pointercancel',endDrag);
    s.addEventListener('input',()=>{ if(box!==document.activeElement) box.value=s.value; });
    s.addEventListener('change',()=>{ endDrag(); patch(name,{[e.property]:Number(s.value)}); });
    box.addEventListener('input',()=>{ if(box.value!=='') s.value=clamp(Number(box.value)); });
    box.addEventListener('change',()=>{ if(box.value==='') return; const v=clamp(Number(box.value)); box.value=v; s.value=v; patch(name,{[e.property]:v}); });
    box.addEventListener('keydown',ev=>{ if(ev.key==='Enter'){ ev.preventDefault(); box.blur(); } });
  }
  return wrap;
}
```

Note: `state` for cover/lock is `type:"enum"` (Task 5/6 exposes), so it correctly takes the enum branch; OnOff `state` is `type:"binary"` and takes the toggle branch. No collision.

- [ ] **Step 4: Reflect enum state in `applyState`**

In `applyState(name)`, inside the `for (const [prop,val] of Object.entries(s))` loop, add before the readout fallback:

```javascript
    const segs=el.querySelectorAll(`button.seg[data-prop="${prop}"]`);
    if (segs.length){ segs.forEach(b=>b.classList.toggle('active', b.dataset.val.toUpperCase()===String(val).toUpperCase())); continue; }
```

- [ ] **Step 5: Style the segmented control**

In `src/Matterhorn/wwwroot/app.css`, add (match the existing control aesthetic — reuse tokens/spacing already in the file):

```css
.segmented{ display:flex; flex-wrap:wrap; gap:6px; }
.segmented .seg{ flex:1 1 auto; padding:6px 10px; border-radius:8px; cursor:pointer;
  border:1px solid var(--line,#334); background:transparent; color:inherit; font:inherit; }
.segmented .seg.active{ background:var(--accent,#3b82f6); border-color:var(--accent,#3b82f6); color:#fff; }
```

- [ ] **Step 6: Manual verification**

Run: `docker compose up --build`
Open http://localhost:16090. Confirm: cover shows OPEN/CLOSE/STOP segmented + position slider; lock shows LOCK/UNLOCK; thermostat shows temp readout + setpoint sliders + mode segmented; fan shows mode segmented + percent slider; sensors show read-only badges. Click each control and confirm the fake echoes/logs a set (state updates where the fake echoes attributes).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: exposes-driven enum control + generic settable rendering; seed demo cover/lock/thermostat/fan"
```

---

### Task 13: Identify action button

`identify` is set-only with no readback; render it as a momentary button rather than a slider.

**Files:** `src/Matterhorn/wwwroot/js/views/devices.js`, `app.css`.

- [ ] **Step 1: Special-case `identify` in `control`**

In `control(name,e)`, add at the very top (before the `enum` branch):

```javascript
  if (e.property==='identify'){
    wrap.className='row';
    wrap.innerHTML=`<span class="legend">identify</span><button class="btn-ident">blink</button>`;
    wrap.querySelector('button').addEventListener('click',()=>patch(name,{identify:10}));
    return wrap;
  }
```

- [ ] **Step 2: Style + verify**

Add a `.btn-ident` style consistent with existing buttons in `app.css`. Run `docker compose up --build`, click identify on a light, confirm a set is issued (visible in the station log / fake controller).

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "feat: identify action button on device stations"
```

---

## Self-Review Notes (verification checklist for the implementer)

- **Spec coverage:** window covering (T5), door lock (T6), thermostat (T7), fan (T8), color xy (T9), transition (T10), effect+identify (T11), all read-only sensors (T4), write_attribute seam (T1), enum exposes (T3), sum-type write path (T2), dashboard controls (T12-13). Every spec category maps to a task.
- **Type consistency:** `CommandMapping.Map` returns `IReadOnlyList<IDeviceWrite>` after T2; every later task's tests use the `Cmd(...)`/`Attr(...)` cast helpers. `AttributeWriteSpec(ClusterId, AttributeId, Value)` and `CommandSpec(ClusterId, CommandName, Payload)` field names are used verbatim throughout.
- **Cover inversion** appears symmetrically in T5 read (`100 - value`) and write (`100 - position`).
- **Attribute ids** are hex-literal `uint` (e.g. `0x12u`) in tests to match `AttributeWriteSpec.AttributeId : uint`.
- **Open item flagged in-spec:** energy attribute id/struct shape and concentration scaling (T4 Step 4 note) — confirm against a live device; single-row fixes.
- **Device-type names:** `MatterServerProtocol.DeviceTypeName` already covers Window Covering (0x0202), Door Lock (0x000A), Thermostat (0x0301). Add `0x002B => "Fan"` during T8 if a demo/live fan shows as a hex code (optional polish; not gating).
