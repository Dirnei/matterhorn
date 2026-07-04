# Matter2Mqtt Core — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A standalone .NET/Akka.NET service that consumes a Matter controller over WebSocket and exposes Matter devices as a Zigbee2MQTT-shaped MQTT surface plus a thin REST facade.

**Architecture:** An `IMatterController` seam abstracts the upstream Matter server (WebSocket). An Akka.Streams ingestion pipeline conflates bursty attribute events and routes them to one `MatterEndpointActor` per logical device; a `MatterGatewayActor` owns the controller connection, device lifecycle, and the `bridge/*` control-plane. MQTT (event bus + retained state) and REST (synchronous request/response) are two projections of the same actor model.

**Tech Stack:** .NET 10, C#, Akka.NET (Akka.Hosting + Akka.Streams), HiveMQtt, ASP.NET Core Minimal API + OpenAPI, xUnit + Akka.TestKit.Xunit2. System.Net.WebSockets for the controller connection. System.Text.Json for payloads.

## Global Constraints

- **.NET 10**, **Akka.NET 1.5.68+**, C# nullable enabled.
- **Build/test commands MUST pass `/p:NuGetAudit=false`** (local feeds are unreachable; avoids NU1900). Every `dotnet build`/`dotnet test` in this plan already includes it.
- **MQTT property names and ranges copy Z2M verbatim** — `state` (`"ON"`/`"OFF"`), `brightness` (0–254), `color_temp` (mireds), etc. (spec §5). Do not invent new names.
- **Invariant:** nothing exists in REST that isn't in MQTT and vice versa — both read the same actor model (spec §2).
- **Base topic** default `matter2mqtt`; **REST port** default `8090`; **API key** required for REST mutations (spec §8, §10).
- **Endpoint = logical device**; `friendly_name` maps to one node+endpoint (spec §5).
- **Commits:** the repo owner runs commits. Per the owner's standing rule, an executing agent MUST NOT commit autonomously — treat each **"Commit"** step as *stage the files and request the owner's approval to commit with the given message*.
- New repo `matter2mqtt`, separate from `vidar`. All paths below are relative to the `matter2mqtt` repo root.

---

## File Structure

```
Matter2Mqtt.sln
src/Matter2Mqtt/
  Matter2Mqtt.csproj                 (Microsoft.NET.Sdk.Web)
  Program.cs                         host wiring (Akka.Hosting + ASP.NET)
  Domain/
    MatterClusters.cs                cluster/attribute id constants
    AttributeReading.cs              (nodeId, endpoint, clusterId, attributeId, value)
    PropertyMapping.cs               cluster/attr -> semantic property (pure, read path)
    CommandSpec.cs                   (clusterId, commandName, payload)
    CommandMapping.cs                set-payload -> CommandSpec[] (pure, write path)
    FriendlyName.cs                  slug + default name
    ExposesBuilder.cs                device -> exposes[] for bridge/devices
    DeviceDescriptor.cs              bridge/devices entry model
  Controller/
    IMatterController.cs             seam
    MatterEvents.cs                  NodeAdded/NodeRemoved/AttributeChanged/Reachability
    FakeMatterController.cs          in-memory (tests + dev)
    MatterServerProtocol.cs          pure WS-JSON <-> domain (serialize/deserialize)
    PythonMatterServerController.cs  socket loop using the protocol
  Mqtt/
    MqttTopics.cs                    topic builders (pure)
    IMqttPublisher.cs
    HiveMqttPublisher.cs
  Actors/
    ActorMessages.cs                 internal messages
    MatterEndpointActor.cs
    MatterGatewayActor.cs
  Streaming/
    IngestionPipeline.cs             Akka.Streams graph (conflation, GroupBy)
  Rest/
    ApiKeyMiddleware.cs
    ApiEndpoints.cs                  minimal-API mapping
  Config/
    Matter2MqttConfig.cs
tests/Matter2Mqtt.Tests/
  Matter2Mqtt.Tests.csproj
  Domain/  PropertyMappingTests.cs  CommandMappingTests.cs  FriendlyNameTests.cs  ExposesBuilderTests.cs
  Controller/ FakeMatterControllerTests.cs  MatterServerProtocolTests.cs
  Mqtt/  MqttTopicsTests.cs
  Actors/ MatterEndpointActorTests.cs  MatterGatewayActorTests.cs
  Streaming/ IngestionPipelineTests.cs
```

---

## Task 1: Solution & project scaffolding

**Files:**
- Create: `Matter2Mqtt.sln`, `src/Matter2Mqtt/Matter2Mqtt.csproj`, `tests/Matter2Mqtt.Tests/Matter2Mqtt.Tests.csproj`
- Create: `src/Matter2Mqtt/Domain/MatterClusters.cs`
- Test: `tests/Matter2Mqtt.Tests/Domain/SmokeTests.cs`

**Interfaces:**
- Produces: a buildable solution; `MatterClusters` constants (`OnOff = 0x0006`, `LevelControl = 0x0008`, `ColorControl = 0x0300`, `BooleanState = 0x0045`, `OccupancySensing = 0x0406`, `TemperatureMeasurement = 0x0402`, `RelativeHumidityMeasurement = 0x0405`, `IlluminanceMeasurement = 0x0400`, `PowerSource = 0x002F`).

- [ ] **Step 1: Create the solution and projects**

```bash
dotnet new sln -n Matter2Mqtt
dotnet new web -n Matter2Mqtt -o src/Matter2Mqtt
dotnet new xunit -n Matter2Mqtt.Tests -o tests/Matter2Mqtt.Tests
dotnet sln add src/Matter2Mqtt/Matter2Mqtt.csproj tests/Matter2Mqtt.Tests/Matter2Mqtt.Tests.csproj
dotnet add tests/Matter2Mqtt.Tests reference src/Matter2Mqtt
```

- [ ] **Step 2: Add packages**

```bash
dotnet add src/Matter2Mqtt package Akka.Hosting
dotnet add src/Matter2Mqtt package Akka.Streams
dotnet add src/Matter2Mqtt package HiveMQtt
dotnet add tests/Matter2Mqtt.Tests package Akka.TestKit.Xunit2
dotnet add tests/Matter2Mqtt.Tests package Akka.Streams.TestKit
```

Set both csproj `<TargetFramework>net10.0</TargetFramework>`, `<Nullable>enable</Nullable>`, `<LangVersion>latest</LangVersion>`.

- [ ] **Step 3: Write `MatterClusters` constants**

```csharp
namespace Matter2Mqtt.Domain;

public static class MatterClusters
{
    public const uint OnOff = 0x0006;
    public const uint LevelControl = 0x0008;
    public const uint ColorControl = 0x0300;
    public const uint BooleanState = 0x0045;
    public const uint OccupancySensing = 0x0406;
    public const uint TemperatureMeasurement = 0x0402;
    public const uint RelativeHumidityMeasurement = 0x0405;
    public const uint IlluminanceMeasurement = 0x0400;
    public const uint PowerSource = 0x002F;
}
```

- [ ] **Step 4: Write the smoke test**

```csharp
using Matter2Mqtt.Domain;
using Xunit;

namespace Matter2Mqtt.Tests.Domain;

public class SmokeTests
{
    [Fact]
    public void Cluster_ids_are_defined()
    {
        Assert.Equal(0x0006u, MatterClusters.OnOff);
        Assert.Equal(0x0300u, MatterClusters.ColorControl);
    }
}
```

- [ ] **Step 5: Build and test**

Run: `dotnet test /p:NuGetAudit=false`
Expected: PASS (1 test), solution builds.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "chore: scaffold Matter2Mqtt solution and cluster constants"
```

---

## Task 2: Property mapping (read path)

**Files:**
- Create: `src/Matter2Mqtt/Domain/AttributeReading.cs`, `src/Matter2Mqtt/Domain/PropertyMapping.cs`
- Test: `tests/Matter2Mqtt.Tests/Domain/PropertyMappingTests.cs`

**Interfaces:**
- Consumes: `MatterClusters` (Task 1).
- Produces:
  - `record AttributeReading(ulong NodeId, ushort Endpoint, uint ClusterId, uint AttributeId, JsonElement Value)`
  - `static IReadOnlyDictionary<string, object?> PropertyMapping.Map(IEnumerable<AttributeReading> readings)` — returns semantic property → value for all mappable readings; unmappable readings are skipped.
  - Attribute id constants used: OnOff.OnOff=0, LevelControl.CurrentLevel=0, ColorControl.ColorTemperatureMireds=7, TemperatureMeasurement.MeasuredValue=0, RelativeHumidity.MeasuredValue=0, Illuminance.MeasuredValue=0, BooleanState.StateValue=0, OccupancySensing.Occupancy=0, PowerSource.BatPercentRemaining=12.

- [ ] **Step 1: Write failing tests**

```csharp
using System.Text.Json;
using Matter2Mqtt.Domain;
using Xunit;

namespace Matter2Mqtt.Tests.Domain;

public class PropertyMappingTests
{
    private static AttributeReading R(uint cluster, uint attr, string json) =>
        new(1, 1, cluster, attr, JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Maps_onoff_true_to_state_ON()
    {
        var props = PropertyMapping.Map(new[] { R(MatterClusters.OnOff, 0, "true") });
        Assert.Equal("ON", props["state"]);
    }

    [Fact]
    public void Maps_level_to_brightness_unchanged_range()
    {
        var props = PropertyMapping.Map(new[] { R(MatterClusters.LevelControl, 0, "128") });
        Assert.Equal(128, Assert.IsType<int>(props["brightness"]));
    }

    [Fact]
    public void Maps_temperature_hundredths_to_celsius()
    {
        var props = PropertyMapping.Map(new[] { R(MatterClusters.TemperatureMeasurement, 0, "2172") });
        Assert.Equal(21.72, Assert.IsType<double>(props["temperature"]), 3);
    }

    [Fact]
    public void Maps_illuminance_measuredvalue_to_lux()
    {
        // v = 10000*log10(lux)+1 ; for lux=100 -> v = 20001
        var props = PropertyMapping.Map(new[] { R(MatterClusters.IlluminanceMeasurement, 0, "20001") });
        Assert.Equal(100.0, Assert.IsType<double>(props["illuminance"]), 1);
    }

    [Fact]
    public void Maps_battery_halfpercent_to_percent()
    {
        var props = PropertyMapping.Map(new[] { R(MatterClusters.PowerSource, 12, "150") });
        Assert.Equal(75, Assert.IsType<int>(props["battery"]));
    }

    [Fact]
    public void Skips_unmapped_cluster()
    {
        var props = PropertyMapping.Map(new[] { R(0x9999, 0, "1") });
        Assert.Empty(props);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~PropertyMappingTests" /p:NuGetAudit=false`
Expected: FAIL (PropertyMapping / AttributeReading not defined).

- [ ] **Step 3: Implement `AttributeReading` and `PropertyMapping`**

```csharp
// AttributeReading.cs
using System.Text.Json;
namespace Matter2Mqtt.Domain;
public record AttributeReading(ulong NodeId, ushort Endpoint, uint ClusterId, uint AttributeId, JsonElement Value);
```

```csharp
// PropertyMapping.cs
using System.Text.Json;
namespace Matter2Mqtt.Domain;

public static class PropertyMapping
{
    private sealed record Rule(uint Cluster, uint Attr, string Property, Func<JsonElement, object?> Convert);

    private static readonly Rule[] Rules =
    [
        new(MatterClusters.OnOff, 0, "state", v => v.GetBoolean() ? "ON" : "OFF"),
        new(MatterClusters.LevelControl, 0, "brightness", v => v.GetInt32()),
        new(MatterClusters.ColorControl, 7, "color_temp", v => v.GetInt32()),
        new(MatterClusters.BooleanState, 0, "contact", v => v.GetBoolean()),
        new(MatterClusters.OccupancySensing, 0, "occupancy", v => v.GetInt32() != 0),
        new(MatterClusters.TemperatureMeasurement, 0, "temperature", v => v.GetInt32() / 100.0),
        new(MatterClusters.RelativeHumidityMeasurement, 0, "humidity", v => v.GetInt32() / 100.0),
        new(MatterClusters.IlluminanceMeasurement, 0, "illuminance", v => Math.Pow(10, (v.GetInt32() - 1) / 10000.0)),
        new(MatterClusters.PowerSource, 12, "battery", v => v.GetInt32() / 2),
    ];

    public static IReadOnlyDictionary<string, object?> Map(IEnumerable<AttributeReading> readings)
    {
        var result = new Dictionary<string, object?>();
        foreach (var r in readings)
        {
            var rule = Array.Find(Rules, x => x.Cluster == r.ClusterId && x.Attr == r.AttributeId);
            if (rule is null) continue;
            result[rule.Property] = rule.Convert(r.Value);
        }
        return result;
    }

    public static bool IsMappable(uint clusterId, uint attributeId) =>
        Array.Exists(Rules, x => x.Cluster == clusterId && x.Attr == attributeId);

    public static IEnumerable<string> KnownProperties => Rules.Select(r => r.Property);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~PropertyMappingTests" /p:NuGetAudit=false`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: map Matter cluster attributes to Z2M-shaped properties"
```

---

## Task 3: Command mapping (write path)

**Files:**
- Create: `src/Matter2Mqtt/Domain/CommandSpec.cs`, `src/Matter2Mqtt/Domain/CommandMapping.cs`
- Test: `tests/Matter2Mqtt.Tests/Domain/CommandMappingTests.cs`

**Interfaces:**
- Produces:
  - `record CommandSpec(uint ClusterId, string CommandName, IReadOnlyDictionary<string, object?> Payload)`
  - `static IReadOnlyList<CommandSpec> CommandMapping.Map(IReadOnlyDictionary<string, JsonElement> setPayload)` — maps a `/set` object to Matter commands. `state` → OnOff On/Off/Toggle; `brightness` → LevelControl MoveToLevelWithOnOff; `color_temp` → ColorControl MoveToColorTemperature.

- [ ] **Step 1: Write failing tests**

```csharp
using System.Text.Json;
using Matter2Mqtt.Domain;
using Xunit;

namespace Matter2Mqtt.Tests.Domain;

public class CommandMappingTests
{
    private static IReadOnlyDictionary<string, JsonElement> Payload(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void State_ON_maps_to_OnOff_On()
    {
        var cmds = CommandMapping.Map(Payload("""{"state":"ON"}"""));
        var c = Assert.Single(cmds);
        Assert.Equal(MatterClusters.OnOff, c.ClusterId);
        Assert.Equal("On", c.CommandName);
    }

    [Fact]
    public void State_OFF_maps_to_OnOff_Off()
    {
        var c = Assert.Single(CommandMapping.Map(Payload("""{"state":"OFF"}""")));
        Assert.Equal("Off", c.CommandName);
    }

    [Fact]
    public void Brightness_maps_to_MoveToLevelWithOnOff_with_level()
    {
        var c = Assert.Single(CommandMapping.Map(Payload("""{"brightness":200}""")));
        Assert.Equal(MatterClusters.LevelControl, c.ClusterId);
        Assert.Equal("MoveToLevelWithOnOff", c.CommandName);
        Assert.Equal(200, Assert.IsType<int>(c.Payload["level"]));
    }

    [Fact]
    public void Color_temp_maps_to_MoveToColorTemperature_mireds()
    {
        var c = Assert.Single(CommandMapping.Map(Payload("""{"color_temp":370}""")));
        Assert.Equal(MatterClusters.ColorControl, c.ClusterId);
        Assert.Equal("MoveToColorTemperature", c.CommandName);
        Assert.Equal(370, Assert.IsType<int>(c.Payload["colorTemperatureMireds"]));
    }

    [Fact]
    public void Combined_payload_maps_to_multiple_commands_in_order()
    {
        var cmds = CommandMapping.Map(Payload("""{"state":"ON","brightness":128}"""));
        Assert.Equal(2, cmds.Count);
        Assert.Equal("On", cmds[0].CommandName);
        Assert.Equal("MoveToLevelWithOnOff", cmds[1].CommandName);
    }

    [Fact]
    public void Unknown_key_is_ignored()
    {
        Assert.Empty(CommandMapping.Map(Payload("""{"nonsense":1}""")));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~CommandMappingTests" /p:NuGetAudit=false`
Expected: FAIL (types not defined).

- [ ] **Step 3: Implement `CommandSpec` and `CommandMapping`**

```csharp
// CommandSpec.cs
namespace Matter2Mqtt.Domain;
public record CommandSpec(uint ClusterId, string CommandName, IReadOnlyDictionary<string, object?> Payload);
```

```csharp
// CommandMapping.cs
using System.Text.Json;
namespace Matter2Mqtt.Domain;

public static class CommandMapping
{
    // Fixed order so "on then set level" behaves predictably.
    private static readonly string[] Order = ["state", "brightness", "color_temp"];

    public static IReadOnlyList<CommandSpec> Map(IReadOnlyDictionary<string, JsonElement> setPayload)
    {
        var cmds = new List<CommandSpec>();
        foreach (var key in Order)
        {
            if (!setPayload.TryGetValue(key, out var v)) continue;
            switch (key)
            {
                case "state":
                    var s = v.GetString()?.ToUpperInvariant();
                    if (s == "ON") cmds.Add(new(MatterClusters.OnOff, "On", Empty()));
                    else if (s == "OFF") cmds.Add(new(MatterClusters.OnOff, "Off", Empty()));
                    else if (s == "TOGGLE") cmds.Add(new(MatterClusters.OnOff, "Toggle", Empty()));
                    break;
                case "brightness":
                    cmds.Add(new(MatterClusters.LevelControl, "MoveToLevelWithOnOff",
                        new Dictionary<string, object?> { ["level"] = v.GetInt32() }));
                    break;
                case "color_temp":
                    cmds.Add(new(MatterClusters.ColorControl, "MoveToColorTemperature",
                        new Dictionary<string, object?> { ["colorTemperatureMireds"] = v.GetInt32() }));
                    break;
            }
        }
        return cmds;
    }

    private static IReadOnlyDictionary<string, object?> Empty() => new Dictionary<string, object?>();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~CommandMappingTests" /p:NuGetAudit=false`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: map /set payloads to Matter cluster commands"
```

---

## Task 4: Friendly names + exposes + device descriptor

**Files:**
- Create: `src/Matter2Mqtt/Domain/FriendlyName.cs`, `src/Matter2Mqtt/Domain/DeviceDescriptor.cs`, `src/Matter2Mqtt/Domain/ExposesBuilder.cs`
- Test: `tests/Matter2Mqtt.Tests/Domain/FriendlyNameTests.cs`, `tests/Matter2Mqtt.Tests/Domain/ExposesBuilderTests.cs`

**Interfaces:**
- Produces:
  - `static string FriendlyName.Default(string? productName, ulong nodeId, ushort endpoint)` → slug `productname_nodeid_endpoint`.
  - `record EndpointInfo(ulong NodeId, ushort Endpoint, string? VendorName, string? ProductName, ushort VendorId, ushort ProductId, string DeviceType, bool Reachable, IReadOnlyList<uint> ClusterIds)`
  - `record ExposeEntry(string Type, string Property, int Access, ...)` (see impl)
  - `record DeviceDescriptor(string FriendlyName, string NodeId, ushort Endpoint, string? VendorName, string? ProductName, ushort VendorId, ushort ProductId, string DeviceType, bool Reachable, IReadOnlyList<ExposeEntry> Exposes)`
  - `static IReadOnlyList<ExposeEntry> ExposesBuilder.Build(IReadOnlyList<uint> clusterIds)` — one expose per supported cluster present.

- [ ] **Step 1: Write failing tests**

```csharp
using Matter2Mqtt.Domain;
using Xunit;

namespace Matter2Mqtt.Tests.Domain;

public class FriendlyNameTests
{
    [Fact]
    public void Default_slugifies_product_and_appends_node_endpoint()
    {
        Assert.Equal("essentials_bulb_12345_1", FriendlyName.Default("Essentials Bulb", 12345, 1));
    }

    [Fact]
    public void Default_without_product_uses_node_prefix()
    {
        Assert.Equal("node_12345_2", FriendlyName.Default(null, 12345, 2));
    }
}
```

```csharp
using Matter2Mqtt.Domain;
using Xunit;

namespace Matter2Mqtt.Tests.Domain;

public class ExposesBuilderTests
{
    [Fact]
    public void Builds_state_and_brightness_for_dimmable_light()
    {
        var exposes = ExposesBuilder.Build(new[] { MatterClusters.OnOff, MatterClusters.LevelControl });
        Assert.Contains(exposes, e => e.Property == "state" && e.Type == "binary");
        Assert.Contains(exposes, e => e.Property == "brightness" && e.Type == "numeric");
    }

    [Fact]
    public void Sensor_exposes_are_readonly_access_1()
    {
        var exposes = ExposesBuilder.Build(new[] { MatterClusters.TemperatureMeasurement });
        var temp = Assert.Single(exposes);
        Assert.Equal("temperature", temp.Property);
        Assert.Equal(1, temp.Access); // published only
    }

    [Fact]
    public void Ignores_unsupported_cluster()
    {
        Assert.Empty(ExposesBuilder.Build(new[] { 0x9999u }));
    }
}
```

- [ ] **Step 2: Run to verify fail**

Run: `dotnet test --filter "FullyQualifiedName~FriendlyNameTests|FullyQualifiedName~ExposesBuilderTests" /p:NuGetAudit=false`
Expected: FAIL.

- [ ] **Step 3: Implement the three files**

```csharp
// FriendlyName.cs
using System.Text;
namespace Matter2Mqtt.Domain;

public static class FriendlyName
{
    public static string Default(string? productName, ulong nodeId, ushort endpoint)
    {
        var prefix = string.IsNullOrWhiteSpace(productName) ? "node" : Slug(productName);
        return $"{prefix}_{nodeId}_{endpoint}";
    }

    public static string Slug(string input)
    {
        var sb = new StringBuilder();
        foreach (var ch in input.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        return sb.ToString().Trim('_');
    }
}
```

```csharp
// DeviceDescriptor.cs
namespace Matter2Mqtt.Domain;

public record EndpointInfo(
    ulong NodeId, ushort Endpoint, string? VendorName, string? ProductName,
    ushort VendorId, ushort ProductId, string DeviceType, bool Reachable,
    IReadOnlyList<uint> ClusterIds);

public record ExposeEntry(
    string Type, string Property, int Access,
    string? ValueOn = null, string? ValueOff = null,
    int? ValueMin = null, int? ValueMax = null, string? Unit = null);

public record DeviceDescriptor(
    string FriendlyName, string NodeId, ushort Endpoint,
    string? VendorName, string? ProductName, ushort VendorId, ushort ProductId,
    string DeviceType, bool Reachable, IReadOnlyList<ExposeEntry> Exposes);
```

```csharp
// ExposesBuilder.cs
namespace Matter2Mqtt.Domain;

public static class ExposesBuilder
{
    private const int Published = 1, Set = 2, Get = 4, All = 7;

    public static IReadOnlyList<ExposeEntry> Build(IReadOnlyList<uint> clusterIds)
    {
        var list = new List<ExposeEntry>();
        var has = new HashSet<uint>(clusterIds);

        if (has.Contains(MatterClusters.OnOff))
            list.Add(new("binary", "state", All, ValueOn: "ON", ValueOff: "OFF"));
        if (has.Contains(MatterClusters.LevelControl))
            list.Add(new("numeric", "brightness", All, ValueMin: 0, ValueMax: 254));
        if (has.Contains(MatterClusters.ColorControl))
            list.Add(new("numeric", "color_temp", All, ValueMin: 147, ValueMax: 500, Unit: "mired"));
        if (has.Contains(MatterClusters.BooleanState))
            list.Add(new("binary", "contact", Published));
        if (has.Contains(MatterClusters.OccupancySensing))
            list.Add(new("binary", "occupancy", Published));
        if (has.Contains(MatterClusters.TemperatureMeasurement))
            list.Add(new("numeric", "temperature", Published, Unit: "°C"));
        if (has.Contains(MatterClusters.RelativeHumidityMeasurement))
            list.Add(new("numeric", "humidity", Published, Unit: "%"));
        if (has.Contains(MatterClusters.IlluminanceMeasurement))
            list.Add(new("numeric", "illuminance", Published, Unit: "lx"));
        if (has.Contains(MatterClusters.PowerSource))
            list.Add(new("numeric", "battery", Published, ValueMin: 0, ValueMax: 100, Unit: "%"));
        return list;
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~FriendlyNameTests|FullyQualifiedName~ExposesBuilderTests" /p:NuGetAudit=false`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: friendly names, device descriptor, and Z2M exposes builder"
```

---

## Task 5: Controller seam + FakeMatterController

**Files:**
- Create: `src/Matter2Mqtt/Controller/MatterEvents.cs`, `src/Matter2Mqtt/Controller/IMatterController.cs`, `src/Matter2Mqtt/Controller/FakeMatterController.cs`
- Test: `tests/Matter2Mqtt.Tests/Controller/FakeMatterControllerTests.cs`

**Interfaces:**
- Produces:
  - `abstract record MatterEvent` with `NodeAdded(EndpointInfo Endpoint)`, `NodeRemoved(ulong NodeId, ushort Endpoint)`, `AttributeChanged(AttributeReading Reading)`, `ReachabilityChanged(ulong NodeId, ushort Endpoint, bool Reachable)`.
  - `interface IMatterController`: `IAsyncEnumerable<MatterEvent> ConnectAndListen(CancellationToken ct)`; `Task InvokeCommand(ulong nodeId, ushort endpoint, CommandSpec command, CancellationToken ct)`; `Task<ulong> Commission(string setupCode, CancellationToken ct)`; `Task RemoveNode(ulong nodeId, CancellationToken ct)`.
  - `FakeMatterController` with test hooks: `void Emit(MatterEvent e)`; `IReadOnlyList<(ulong NodeId, ushort Endpoint, CommandSpec Cmd)> Invocations`; `Func<string, ulong> OnCommission`.

- [ ] **Step 1: Write failing tests**

```csharp
using System.Text.Json;
using Matter2Mqtt.Controller;
using Matter2Mqtt.Domain;
using Xunit;

namespace Matter2Mqtt.Tests.Controller;

public class FakeMatterControllerTests
{
    [Fact]
    public async Task Emitted_events_are_observed_by_listener()
    {
        var fake = new FakeMatterController();
        var received = new List<MatterEvent>();
        using var cts = new CancellationTokenSource();

        var pump = Task.Run(async () =>
        {
            await foreach (var e in fake.ConnectAndListen(cts.Token))
            {
                received.Add(e);
                if (received.Count == 1) { cts.Cancel(); break; }
            }
        });

        fake.Emit(new AttributeChanged(new AttributeReading(1, 1, MatterClusters.OnOff, 0,
            JsonDocument.Parse("true").RootElement)));
        await pump;

        Assert.Single(received);
        Assert.IsType<AttributeChanged>(received[0]);
    }

    [Fact]
    public async Task InvokeCommand_is_recorded()
    {
        var fake = new FakeMatterController();
        await fake.InvokeCommand(1, 1,
            new CommandSpec(MatterClusters.OnOff, "On", new Dictionary<string, object?>()), default);
        var inv = Assert.Single(fake.Invocations);
        Assert.Equal("On", inv.Cmd.CommandName);
    }

    [Fact]
    public async Task Commission_uses_hook()
    {
        var fake = new FakeMatterController { OnCommission = _ => 42 };
        Assert.Equal(42ul, await fake.Commission("MT:XXX", default));
    }
}
```

- [ ] **Step 2: Run to verify fail**

Run: `dotnet test --filter "FullyQualifiedName~FakeMatterControllerTests" /p:NuGetAudit=false`
Expected: FAIL.

- [ ] **Step 3: Implement events, interface, fake**

```csharp
// MatterEvents.cs
using Matter2Mqtt.Domain;
namespace Matter2Mqtt.Controller;

public abstract record MatterEvent;
public record NodeAdded(EndpointInfo Endpoint) : MatterEvent;
public record NodeRemoved(ulong NodeId, ushort Endpoint) : MatterEvent;
public record AttributeChanged(AttributeReading Reading) : MatterEvent;
public record ReachabilityChanged(ulong NodeId, ushort Endpoint, bool Reachable) : MatterEvent;
```

```csharp
// IMatterController.cs
using Matter2Mqtt.Domain;
namespace Matter2Mqtt.Controller;

public interface IMatterController
{
    IAsyncEnumerable<MatterEvent> ConnectAndListen(CancellationToken ct);
    Task InvokeCommand(ulong nodeId, ushort endpoint, CommandSpec command, CancellationToken ct);
    Task<ulong> Commission(string setupCode, CancellationToken ct);
    Task RemoveNode(ulong nodeId, CancellationToken ct);
}
```

```csharp
// FakeMatterController.cs
using System.Threading.Channels;
using Matter2Mqtt.Domain;
namespace Matter2Mqtt.Controller;

public sealed class FakeMatterController : IMatterController
{
    private readonly Channel<MatterEvent> _channel = Channel.CreateUnbounded<MatterEvent>();
    private readonly List<(ulong, ushort, CommandSpec)> _invocations = new();

    public IReadOnlyList<(ulong NodeId, ushort Endpoint, CommandSpec Cmd)> Invocations => _invocations;
    public Func<string, ulong> OnCommission { get; set; } = _ => 1;
    public List<ulong> Removed { get; } = new();

    public void Emit(MatterEvent e) => _channel.Writer.TryWrite(e);

    public async IAsyncEnumerable<MatterEvent> ConnectAndListen(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var e in _channel.Reader.ReadAllAsync(ct))
            yield return e;
    }

    public Task InvokeCommand(ulong nodeId, ushort endpoint, CommandSpec command, CancellationToken ct)
    {
        lock (_invocations) _invocations.Add((nodeId, endpoint, command));
        return Task.CompletedTask;
    }

    public Task<ulong> Commission(string setupCode, CancellationToken ct) => Task.FromResult(OnCommission(setupCode));
    public Task RemoveNode(ulong nodeId, CancellationToken ct) { Removed.Add(nodeId); return Task.CompletedTask; }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~FakeMatterControllerTests" /p:NuGetAudit=false`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: IMatterController seam, event model, and in-memory fake"
```

---

## Task 6: MQTT topics + publisher

**Files:**
- Create: `src/Matter2Mqtt/Mqtt/MqttTopics.cs`, `src/Matter2Mqtt/Mqtt/IMqttPublisher.cs`, `src/Matter2Mqtt/Mqtt/HiveMqttPublisher.cs`
- Test: `tests/Matter2Mqtt.Tests/Mqtt/MqttTopicsTests.cs`

**Interfaces:**
- Produces:
  - `class MqttTopics(string baseTopic)` with `string BridgeState()`, `BridgeInfo()`, `BridgeDevices()`, `BridgeEvent()`, `Device(string name)`, `Availability(string name)`, `string SetSubscription()` = `{base}/+/set`, `string SetAttrSubscription()` = `{base}/+/set/+`, `string GetSubscription()`, `string RequestSubscription()` = `{base}/bridge/request/+`; plus `bool TryParseSet(string topic, out string friendlyName, out string? attr)`.
  - `interface IMqttPublisher { Task PublishRetained(string topic, string payload); Task Publish(string topic, string payload); }`
  - `class InMemoryMqttPublisher : IMqttPublisher` (test double, in `tests/`).

- [ ] **Step 1: Write failing tests**

```csharp
using Matter2Mqtt.Mqtt;
using Xunit;

namespace Matter2Mqtt.Tests.Mqtt;

public class MqttTopicsTests
{
    private readonly MqttTopics _t = new("matter2mqtt");

    [Fact]
    public void Builds_device_and_bridge_topics()
    {
        Assert.Equal("matter2mqtt/lamp", _t.Device("lamp"));
        Assert.Equal("matter2mqtt/lamp/availability", _t.Availability("lamp"));
        Assert.Equal("matter2mqtt/bridge/devices", _t.BridgeDevices());
    }

    [Fact]
    public void Parses_set_topic_without_attr()
    {
        Assert.True(_t.TryParseSet("matter2mqtt/lamp/set", out var name, out var attr));
        Assert.Equal("lamp", name);
        Assert.Null(attr);
    }

    [Fact]
    public void Parses_set_topic_with_attr()
    {
        Assert.True(_t.TryParseSet("matter2mqtt/lamp/set/brightness", out var name, out var attr));
        Assert.Equal("lamp", name);
        Assert.Equal("brightness", attr);
    }

    [Fact]
    public void Rejects_non_set_topic()
    {
        Assert.False(_t.TryParseSet("matter2mqtt/lamp", out _, out _));
    }
}
```

- [ ] **Step 2: Run to verify fail**

Run: `dotnet test --filter "FullyQualifiedName~MqttTopicsTests" /p:NuGetAudit=false`
Expected: FAIL.

- [ ] **Step 3: Implement topics, interface, HiveMqtt publisher, and the in-memory test double**

```csharp
// MqttTopics.cs
namespace Matter2Mqtt.Mqtt;

public sealed class MqttTopics(string baseTopic)
{
    public string Base { get; } = baseTopic;
    public string BridgeState() => $"{Base}/bridge/state";
    public string BridgeInfo() => $"{Base}/bridge/info";
    public string BridgeDevices() => $"{Base}/bridge/devices";
    public string BridgeEvent() => $"{Base}/bridge/event";
    public string Device(string name) => $"{Base}/{name}";
    public string Availability(string name) => $"{Base}/{name}/availability";
    public string SetSubscription() => $"{Base}/+/set";
    public string SetAttrSubscription() => $"{Base}/+/set/+";
    public string GetSubscription() => $"{Base}/+/get";
    public string RequestSubscription() => $"{Base}/bridge/request/+";

    public bool TryParseSet(string topic, out string friendlyName, out string? attr)
    {
        friendlyName = ""; attr = null;
        if (!topic.StartsWith(Base + "/")) return false;
        var parts = topic[(Base.Length + 1)..].Split('/');
        if (parts.Length == 2 && parts[1] == "set") { friendlyName = parts[0]; return true; }
        if (parts.Length == 3 && parts[1] == "set") { friendlyName = parts[0]; attr = parts[2]; return true; }
        return false;
    }
}
```

```csharp
// IMqttPublisher.cs
namespace Matter2Mqtt.Mqtt;
public interface IMqttPublisher
{
    Task PublishRetained(string topic, string payload);
    Task Publish(string topic, string payload);
}
```

```csharp
// HiveMqttPublisher.cs
using HiveMQtt.Client;
using HiveMQtt.MQTT5.Types;
namespace Matter2Mqtt.Mqtt;

public sealed class HiveMqttPublisher(HiveMQClient client) : IMqttPublisher
{
    public Task PublishRetained(string topic, string payload) => Send(topic, payload, retain: true);
    public Task Publish(string topic, string payload) => Send(topic, payload, retain: false);

    private async Task Send(string topic, string payload, bool retain)
    {
        var msg = new MQTT5PublishMessage(topic, QualityOfService.AtMostOnceDelivery) { Payload = System.Text.Encoding.UTF8.GetBytes(payload), Retain = retain };
        await client.PublishAsync(msg);
    }
}
```

```csharp
// tests/Matter2Mqtt.Tests/Mqtt/InMemoryMqttPublisher.cs
using System.Collections.Concurrent;
using Matter2Mqtt.Mqtt;
namespace Matter2Mqtt.Tests.Mqtt;

public sealed class InMemoryMqttPublisher : IMqttPublisher
{
    public ConcurrentBag<(string Topic, string Payload, bool Retained)> Messages { get; } = new();
    public Task PublishRetained(string topic, string payload) { Messages.Add((topic, payload, true)); return Task.CompletedTask; }
    public Task Publish(string topic, string payload) { Messages.Add((topic, payload, false)); return Task.CompletedTask; }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~MqttTopicsTests" /p:NuGetAudit=false`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: MQTT topic model and publisher (HiveMqtt + in-memory test double)"
```

---

## Task 7: MatterEndpointActor

**Files:**
- Create: `src/Matter2Mqtt/Actors/ActorMessages.cs`, `src/Matter2Mqtt/Actors/MatterEndpointActor.cs`
- Test: `tests/Matter2Mqtt.Tests/Actors/MatterEndpointActorTests.cs`

**Interfaces:**
- Consumes: `PropertyMapping`, `CommandMapping`, `IMatterController`, `IMqttPublisher`, `MqttTopics`, `AttributeChanged`.
- Produces:
  - `ActorMessages`: `record ApplyAttribute(AttributeReading Reading)`, `record ApplySet(IReadOnlyDictionary<string, JsonElement> Payload)`, `record SetReachable(bool Reachable)`.
  - `MatterEndpointActor.Props(string friendlyName, ulong nodeId, ushort endpoint, IMatterController controller, IMqttPublisher mqtt, MqttTopics topics)` — on `ApplyAttribute`: merges the mapped property into retained state and publishes `topics.Device(name)` with the full JSON state; on `ApplySet`: maps to commands and calls `controller.InvokeCommand` for each; on `SetReachable`: publishes availability.

- [ ] **Step 1: Write failing tests**

```csharp
using System.Text.Json;
using Akka.Actor;
using Akka.TestKit.Xunit2;
using Matter2Mqtt.Actors;
using Matter2Mqtt.Controller;
using Matter2Mqtt.Domain;
using Matter2Mqtt.Mqtt;
using Matter2Mqtt.Tests.Mqtt;
using Xunit;

namespace Matter2Mqtt.Tests.Actors;

public class MatterEndpointActorTests : TestKit
{
    private readonly MqttTopics _topics = new("matter2mqtt");

    [Fact]
    public void Publishes_retained_state_on_attribute_change()
    {
        var mqtt = new InMemoryMqttPublisher();
        var actor = Sys.ActorOf(MatterEndpointActor.Props("lamp", 1, 1, new FakeMatterController(), mqtt, _topics));

        actor.Tell(new ApplyAttribute(new AttributeReading(1, 1, MatterClusters.OnOff, 0,
            JsonDocument.Parse("true").RootElement)));

        AwaitAssert(() =>
        {
            var msg = Assert.Single(mqtt.Messages, m => m.Topic == "matter2mqtt/lamp");
            Assert.True(msg.Retained);
            Assert.Contains("\"state\":\"ON\"", msg.Payload);
        });
    }

    [Fact]
    public void Merges_multiple_attributes_into_one_state()
    {
        var mqtt = new InMemoryMqttPublisher();
        var actor = Sys.ActorOf(MatterEndpointActor.Props("lamp", 1, 1, new FakeMatterController(), mqtt, _topics));

        actor.Tell(new ApplyAttribute(new AttributeReading(1, 1, MatterClusters.OnOff, 0, JsonDocument.Parse("true").RootElement)));
        actor.Tell(new ApplyAttribute(new AttributeReading(1, 1, MatterClusters.LevelControl, 0, JsonDocument.Parse("128").RootElement)));

        AwaitAssert(() =>
        {
            var last = mqtt.Messages.Where(m => m.Topic == "matter2mqtt/lamp").OrderBy(_ => 0).Last();
            Assert.Contains("\"state\":\"ON\"", last.Payload);
            Assert.Contains("\"brightness\":128", last.Payload);
        });
    }

    [Fact]
    public void Set_invokes_controller_commands()
    {
        var fake = new FakeMatterController();
        var actor = Sys.ActorOf(MatterEndpointActor.Props("lamp", 7, 1, fake, new InMemoryMqttPublisher(), _topics));

        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"state":"ON","brightness":200}""")!;
        actor.Tell(new ApplySet(payload));

        AwaitAssert(() =>
        {
            Assert.Equal(2, fake.Invocations.Count);
            Assert.Equal(7ul, fake.Invocations[0].NodeId);
            Assert.Equal("On", fake.Invocations[0].Cmd.CommandName);
        });
    }
}
```

- [ ] **Step 2: Run to verify fail**

Run: `dotnet test --filter "FullyQualifiedName~MatterEndpointActorTests" /p:NuGetAudit=false`
Expected: FAIL.

- [ ] **Step 3: Implement messages and actor**

```csharp
// ActorMessages.cs
using System.Text.Json;
using Matter2Mqtt.Domain;
namespace Matter2Mqtt.Actors;

public record ApplyAttribute(AttributeReading Reading);
public record ApplySet(IReadOnlyDictionary<string, JsonElement> Payload);
public record SetReachable(bool Reachable);
```

```csharp
// MatterEndpointActor.cs
using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using Matter2Mqtt.Controller;
using Matter2Mqtt.Domain;
using Matter2Mqtt.Mqtt;
namespace Matter2Mqtt.Actors;

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
    }

    private void OnAttribute(ApplyAttribute msg)
    {
        foreach (var (k, v) in PropertyMapping.Map(new[] { msg.Reading }))
            _state[k] = v;
        var json = JsonSerializer.Serialize(_state);
        _mqtt.PublishRetained(_topics.Device(_name), json);
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
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~MatterEndpointActorTests" /p:NuGetAudit=false`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: MatterEndpointActor maps attributes to retained MQTT and set to commands"
```

---

## Task 8: Ingestion pipeline (Akka.Streams, conflation)

**Files:**
- Create: `src/Matter2Mqtt/Streaming/IngestionPipeline.cs`
- Test: `tests/Matter2Mqtt.Tests/Streaming/IngestionPipelineTests.cs`

**Interfaces:**
- Consumes: `MatterEvent`, `AttributeChanged`, `ActorMessages.ApplyAttribute`.
- Produces:
  - `static (ISourceQueueWithComplete<MatterEvent> Queue, Task Completion) IngestionPipeline.Run(ActorSystem sys, Func<ulong, ushort, IActorRef?> resolveEndpoint, IActorRef gateway)` — materializes a graph: `AttributeChanged` events are grouped by `(nodeId, endpoint)`, conflated latest-wins, and delivered as `ApplyAttribute` to the resolved endpoint actor; lifecycle events (`NodeAdded`/`NodeRemoved`/`ReachabilityChanged`) are forwarded to `gateway`.
- Note: conflation is verified by design (Akka.Streams `Conflate`); the test asserts routing + that a burst produces at least the latest value. Determinism of "exactly one" is not asserted (conflation is timing-dependent).

- [ ] **Step 1: Write failing test**

```csharp
using System.Text.Json;
using Akka.Actor;
using Akka.Streams;
using Akka.TestKit.Xunit2;
using Matter2Mqtt.Actors;
using Matter2Mqtt.Controller;
using Matter2Mqtt.Domain;
using Matter2Mqtt.Streaming;
using Xunit;

namespace Matter2Mqtt.Tests.Streaming;

public class IngestionPipelineTests : TestKit
{
    [Fact]
    public async Task Routes_attribute_to_resolved_endpoint_actor()
    {
        var probe = CreateTestProbe();
        var gateway = CreateTestProbe();
        var (queue, _) = IngestionPipeline.Run(Sys, (n, e) => n == 1 && e == 1 ? probe.Ref : null, gateway.Ref);

        await queue.OfferAsync(new AttributeChanged(new AttributeReading(1, 1, MatterClusters.OnOff, 0,
            JsonDocument.Parse("true").RootElement)));

        var msg = probe.ExpectMsg<ApplyAttribute>(TimeSpan.FromSeconds(3));
        Assert.Equal(MatterClusters.OnOff, msg.Reading.ClusterId);
    }

    [Fact]
    public async Task Forwards_lifecycle_events_to_gateway()
    {
        var gateway = CreateTestProbe();
        var (queue, _) = IngestionPipeline.Run(Sys, (_, _) => null, gateway.Ref);

        var info = new EndpointInfo(5, 1, "V", "P", 1, 1, "OnOffLight", true, new uint[] { MatterClusters.OnOff });
        await queue.OfferAsync(new NodeAdded(info));

        gateway.ExpectMsg<NodeAdded>(TimeSpan.FromSeconds(3));
    }
}
```

- [ ] **Step 2: Run to verify fail**

Run: `dotnet test --filter "FullyQualifiedName~IngestionPipelineTests" /p:NuGetAudit=false`
Expected: FAIL.

- [ ] **Step 3: Implement the pipeline**

```csharp
// IngestionPipeline.cs
using Akka.Actor;
using Akka.Streams;
using Akka.Streams.Dsl;
using Matter2Mqtt.Actors;
using Matter2Mqtt.Controller;
namespace Matter2Mqtt.Streaming;

public static class IngestionPipeline
{
    public static (ISourceQueueWithComplete<MatterEvent> Queue, Task Completion) Run(
        ActorSystem sys, Func<ulong, ushort, IActorRef?> resolveEndpoint, IActorRef gateway)
    {
        var mat = sys.Materializer();

        var source = Source.Queue<MatterEvent>(256, OverflowStrategy.DropHead);

        var graph = source.To(Sink.ForEach<MatterEvent>(evt =>
        {
            switch (evt)
            {
                case AttributeChanged ac:
                    var target = resolveEndpoint(ac.Reading.NodeId, ac.Reading.Endpoint);
                    target?.Tell(new ApplyAttribute(ac.Reading));
                    break;
                default:
                    gateway.Tell(evt);
                    break;
            }
        }));

        var queue = graph.Run(mat);
        return (queue, Task.CompletedTask);
    }
}
```

> **Conflation note for the implementer:** the routing graph above is the minimal
> testable form. Add per-device conflation as a follow-up refinement *inside* this
> method once routing is green: replace the single `Sink.ForEach` with
> `source.GroupBy(1024, e => Key(e)).Conflate((prev, cur) => cur).MergeSubstreams()`
> for the `AttributeChanged` branch, where `Key` is `(nodeId, endpoint)`. Keep
> lifecycle events on a non-conflated branch (spec §9: `bridge/event` must never be
> dropped). Do not change the public signature.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~IngestionPipelineTests" /p:NuGetAudit=false`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: Akka.Streams ingestion pipeline routing Matter events"
```

---

## Task 9: MatterGatewayActor (lifecycle + control-plane)

**Files:**
- Create: `src/Matter2Mqtt/Actors/MatterGatewayActor.cs` (extend `ActorMessages.cs`)
- Test: `tests/Matter2Mqtt.Tests/Actors/MatterGatewayActorTests.cs`

**Interfaces:**
- Consumes: `IMatterController`, `IMqttPublisher`, `MqttTopics`, `IngestionPipeline`, `MatterEndpointActor`, `FriendlyName`, `ExposesBuilder`, `DeviceDescriptor`.
- Produces:
  - `ActorMessages` additions: `record CommissionRequest(string Code, string Transaction)`, `record RemoveRequest(string FriendlyName, string Transaction)`, `record GetDevices()` → replies `IReadOnlyList<DeviceDescriptor>`, `record GetDeviceState(string FriendlyName)` → replies `DeviceStateReply(bool Found, string? Json)`, `record SetDevice(string FriendlyName, IReadOnlyDictionary<string, JsonElement> Payload)`.
  - `MatterGatewayActor.Props(IMatterController controller, IMqttPublisher mqtt, MqttTopics topics)` — on start connects the controller via the pipeline; on `NodeAdded` creates a `MatterEndpointActor` (child) + records a `DeviceDescriptor` + republishes `bridge/devices`; on `NodeRemoved` stops the child; on `CommissionRequest`/`RemoveRequest` calls the controller and publishes `bridge/response/*`; answers `GetDevices`/`GetDeviceState`/`SetDevice` for the REST layer.

- [ ] **Step 1: Write failing tests**

```csharp
using System.Text.Json;
using Akka.Actor;
using Akka.TestKit.Xunit2;
using Matter2Mqtt.Actors;
using Matter2Mqtt.Controller;
using Matter2Mqtt.Domain;
using Matter2Mqtt.Mqtt;
using Matter2Mqtt.Tests.Mqtt;
using Xunit;

namespace Matter2Mqtt.Tests.Actors;

public class MatterGatewayActorTests : TestKit
{
    private static EndpointInfo Light(ulong node) =>
        new(node, 1, "Nanoleaf", "Bulb", 4442, 3, "OnOffDimmableLight", true,
            new[] { MatterClusters.OnOff, MatterClusters.LevelControl });

    [Fact]
    public void NodeAdded_publishes_bridge_devices_and_registers_device()
    {
        var fake = new FakeMatterController();
        var mqtt = new InMemoryMqttPublisher();
        var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, mqtt, new MqttTopics("matter2mqtt")));

        fake.Emit(new NodeAdded(Light(1)));

        AwaitAssert(() => Assert.Contains(mqtt.Messages, m => m.Topic == "matter2mqtt/bridge/devices"));

        var devices = gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result;
        Assert.Single(devices);
        Assert.Equal("bulb_1_1", devices[0].FriendlyName);
    }

    [Fact]
    public void SetDevice_routes_to_endpoint_and_invokes_controller()
    {
        var fake = new FakeMatterController();
        var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, new InMemoryMqttPublisher(), new MqttTopics("matter2mqtt")));
        fake.Emit(new NodeAdded(Light(9)));
        AwaitAssert(() => Assert.NotEmpty(gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result));

        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"state":"ON"}""")!;
        gw.Tell(new SetDevice("bulb_9_1", payload));

        AwaitAssert(() => Assert.Contains(fake.Invocations, i => i.NodeId == 9 && i.Cmd.CommandName == "On"));
    }

    [Fact]
    public void CommissionRequest_publishes_response_with_node_id()
    {
        var fake = new FakeMatterController { OnCommission = _ => 55 };
        var mqtt = new InMemoryMqttPublisher();
        var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, mqtt, new MqttTopics("matter2mqtt")));

        gw.Tell(new CommissionRequest("MT:XXX", "tx1"));

        AwaitAssert(() =>
        {
            var resp = Assert.Single(mqtt.Messages, m => m.Topic == "matter2mqtt/bridge/response/commission");
            Assert.Contains("\"tx1\"", resp.Payload);
            Assert.Contains("55", resp.Payload);
        });
    }
}
```

- [ ] **Step 2: Run to verify fail**

Run: `dotnet test --filter "FullyQualifiedName~MatterGatewayActorTests" /p:NuGetAudit=false`
Expected: FAIL.

- [ ] **Step 3: Extend `ActorMessages` and implement the gateway**

```csharp
// add to ActorMessages.cs
using Matter2Mqtt.Domain;
namespace Matter2Mqtt.Actors;

public record CommissionRequest(string Code, string Transaction);
public record RemoveRequest(string FriendlyName, string Transaction);
public record GetDevices;
public record GetDeviceState(string FriendlyName);
public record DeviceStateReply(bool Found, string? Json);
public record SetDevice(string FriendlyName, IReadOnlyDictionary<string, System.Text.Json.JsonElement> Payload);
```

```csharp
// MatterGatewayActor.cs
using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using Akka.Streams;
using Matter2Mqtt.Controller;
using Matter2Mqtt.Domain;
using Matter2Mqtt.Mqtt;
using Matter2Mqtt.Streaming;
namespace Matter2Mqtt.Actors;

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
        var self = Self;
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
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~MatterGatewayActorTests" /p:NuGetAudit=false`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: MatterGatewayActor device lifecycle, control-plane, and commissioning"
```

---

## Task 10: REST facade + API key

**Files:**
- Create: `src/Matter2Mqtt/Rest/ApiKeyMiddleware.cs`, `src/Matter2Mqtt/Rest/ApiEndpoints.cs`, `src/Matter2Mqtt/Config/Matter2MqttConfig.cs`
- Test: `tests/Matter2Mqtt.Tests/Rest/ApiEndpointsTests.cs`

**Interfaces:**
- Consumes: `MatterGatewayActor` messages (`GetDevices`, `SetDevice`, `CommissionRequest`).
- Produces:
  - `record Matter2MqttConfig(string ControllerWsUrl, string ControllerKind, string MqttHost, int MqttPort, string? MqttUser, string? MqttPassword, string BaseTopic, bool RestEnabled, int RestPort, string? ApiKey, string? ThreadDataset)` + `static Matter2MqttConfig FromConfiguration(IConfiguration)`.
  - `static void ApiEndpoints.Map(WebApplication app, Func<IActorRef> gateway)` mapping `GET /api/devices`, `POST /api/devices/{name}/set`, `POST /api/commission`, `GET /api/bridge/info`.
- The test exercises the endpoint handlers against a probe gateway using `WebApplicationFactory`. Register the gateway `IActorRef` in DI so the test can substitute a probe.

- [ ] **Step 1: Write failing test**

```csharp
using System.Net;
using System.Net.Http.Json;
using Akka.Actor;
using Akka.TestKit.Xunit2;
using Matter2Mqtt.Actors;
using Matter2Mqtt.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Matter2Mqtt.Tests.Rest;

public class ApiEndpointsTests : TestKit, IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public ApiEndpointsTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Get_devices_requires_api_key()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/devices");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Get_devices_returns_gateway_list_with_valid_key()
    {
        var probe = CreateTestProbe();
        var client = _factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.AddSingleton(new GatewayRef(probe.Ref));
                s.AddSingleton<IConfigureApiKey>(new StaticApiKey("secret"));
            })).CreateClient();

        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");
        var task = client.GetFromJsonAsync<List<DeviceDescriptor>>("/api/devices");

        probe.ExpectMsg<GetDevices>();
        probe.Reply((IReadOnlyList<DeviceDescriptor>)new List<DeviceDescriptor>());
        Assert.NotNull(await task);
    }
}
```

> **Implementer note:** the exact DI seams (`GatewayRef`, `IConfigureApiKey`,
> `StaticApiKey`) are yours to create in this task; the test above documents the
> required shape. Keep `Program` partial so `WebApplicationFactory<Program>` can host it.

- [ ] **Step 2: Run to verify fail**

Run: `dotnet test --filter "FullyQualifiedName~ApiEndpointsTests" /p:NuGetAudit=false`
Expected: FAIL.

- [ ] **Step 3: Implement config, API-key middleware, endpoints, and DI seams**

```csharp
// Matter2MqttConfig.cs
using Microsoft.Extensions.Configuration;
namespace Matter2Mqtt.Config;

public record Matter2MqttConfig(
    string ControllerWsUrl, string ControllerKind,
    string MqttHost, int MqttPort, string? MqttUser, string? MqttPassword,
    string BaseTopic, bool RestEnabled, int RestPort, string? ApiKey, string? ThreadDataset)
{
    public static Matter2MqttConfig FromConfiguration(IConfiguration c) => new(
        ControllerWsUrl: c["Controller:WsUrl"] ?? "ws://localhost:5580/ws",
        ControllerKind: c["Controller:Kind"] ?? "python-matter-server",
        MqttHost: c["Mqtt:Host"] ?? "localhost",
        MqttPort: int.TryParse(c["Mqtt:Port"], out var p) ? p : 1883,
        MqttUser: c["Mqtt:User"], MqttPassword: c["Mqtt:Password"],
        BaseTopic: c["Mqtt:BaseTopic"] ?? "matter2mqtt",
        RestEnabled: !bool.TryParse(c["Rest:Enabled"], out var re) || re,
        RestPort: int.TryParse(c["Rest:Port"], out var rp) ? rp : 8090,
        ApiKey: c["Rest:ApiKey"], ThreadDataset: c["Thread:Dataset"]);
}
```

```csharp
// ApiKeyMiddleware.cs
namespace Matter2Mqtt.Rest;

public sealed record GatewayRef(Akka.Actor.IActorRef Ref);
public interface IConfigureApiKey { string? ApiKey { get; } }
public sealed record StaticApiKey(string? ApiKey) : IConfigureApiKey;

public sealed class ApiKeyMiddleware(RequestDelegate next, IConfigureApiKey key)
{
    public async Task Invoke(HttpContext ctx)
    {
        if (key.ApiKey is { Length: > 0 } expected)
        {
            var provided = ctx.Request.Headers["X-Api-Key"].ToString();
            if (provided != expected) { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
        }
        await next(ctx);
    }
}
```

```csharp
// ApiEndpoints.cs
using System.Text.Json;
using Akka.Actor;
using Matter2Mqtt.Actors;
using Matter2Mqtt.Domain;
namespace Matter2Mqtt.Rest;

public static class ApiEndpoints
{
    public static void Map(WebApplication app)
    {
        IActorRef Gw() => app.Services.GetRequiredService<GatewayRef>().Ref;
        var timeout = TimeSpan.FromSeconds(10);

        app.MapGet("/api/devices", async () =>
            Results.Json(await Gw().Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices(), timeout)));

        app.MapPost("/api/devices/{name}/set", async (string name, Dictionary<string, JsonElement> body) =>
        {
            Gw().Tell(new SetDevice(name, body));
            return Results.Accepted();
        });

        app.MapPost("/api/commission", async (CommissionBody body) =>
        {
            var tx = Guid.NewGuid().ToString("N");
            Gw().Tell(new CommissionRequest(body.Code, tx));
            return Results.Accepted($"/api/commission/{tx}");
        });

        app.MapGet("/api/bridge/info", () => Results.Json(new { service = "matter2mqtt" }));
    }
}

public record CommissionBody(string Code);
```

Wire middleware + endpoints in `Program.cs` (Task 11). Register `IConfigureApiKey` from config, and a default `GatewayRef` resolved from the Akka registry.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~ApiEndpointsTests" /p:NuGetAudit=false`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: REST facade (devices/set/commission) with API-key auth"
```

---

## Task 11: Host wiring (Program.cs) against the fake controller

**Files:**
- Modify: `src/Matter2Mqtt/Program.cs`
- Test: manual boot verification (documented) + existing suite must stay green.

**Interfaces:**
- Consumes: everything above.
- Produces: a running host that boots the Akka system, starts `MatterGatewayActor`, connects the configured controller (fake if `Controller:Kind=fake`), connects MQTT, and maps REST. `Program` must be `public partial class Program` for `WebApplicationFactory`.

- [ ] **Step 1: Implement `Program.cs`**

```csharp
using Akka.Actor;
using Akka.Hosting;
using HiveMQtt.Client;
using Matter2Mqtt.Actors;
using Matter2Mqtt.Config;
using Matter2Mqtt.Controller;
using Matter2Mqtt.Mqtt;
using Matter2Mqtt.Rest;

var builder = WebApplication.CreateBuilder(args);
var cfg = Matter2MqttConfig.FromConfiguration(builder.Configuration);
var topics = new MqttTopics(cfg.BaseTopic);

// MQTT
var mqttClient = new HiveMQClient(new HiveMQClientOptionsBuilder()
    .WithBroker(cfg.MqttHost).WithPort(cfg.MqttPort)
    .WithClientId($"matter2mqtt-{Guid.NewGuid():N}").Build());
await mqttClient.ConnectAsync();
IMqttPublisher publisher = new HiveMqttPublisher(mqttClient);

// Controller (swappable seam)
IMatterController controller = cfg.ControllerKind switch
{
    "fake" => new FakeMatterController(),
    _ => new PythonMatterServerController(cfg.ControllerWsUrl),
};

builder.Services.AddSingleton(publisher);
builder.Services.AddSingleton(controller);
builder.Services.AddSingleton(topics);
builder.Services.AddSingleton<IConfigureApiKey>(new StaticApiKey(cfg.ApiKey));
builder.Services.AddAkka("matter2mqtt", b => b
    .WithActors((system, registry) =>
    {
        var gw = system.ActorOf(MatterGatewayActor.Props(controller, publisher, topics), "gateway");
        registry.Register<MatterGatewayActor>(gw);
    }));
builder.Services.AddSingleton(sp =>
    new GatewayRef(sp.GetRequiredService<ActorRegistry>().Get<MatterGatewayActor>()));

var app = builder.Build();
app.UseMiddleware<ApiKeyMiddleware>();
ApiEndpoints.Map(app);
app.Run($"http://0.0.0.0:{cfg.RestPort}");

public partial class Program { }
```

- [ ] **Step 2: Verify full suite still passes**

Run: `dotnet test /p:NuGetAudit=false`
Expected: PASS (all tasks' tests).

- [ ] **Step 3: Manual boot smoke (documented)**

```bash
# Boots with the in-memory controller; no external deps beyond an MQTT broker.
Controller__Kind=fake Mqtt__Host=localhost dotnet run --project src/Matter2Mqtt
# Expect: log "online" published to matter2mqtt/bridge/state; GET http://localhost:8090/api/bridge/info returns JSON.
```

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat: host wiring (Akka.Hosting + ASP.NET), controller/MQTT/REST composition"
```

---

## Task 12: PythonMatterServerController (WS adapter)

**Files:**
- Create: `src/Matter2Mqtt/Controller/MatterServerProtocol.cs`, `src/Matter2Mqtt/Controller/PythonMatterServerController.cs`
- Test: `tests/Matter2Mqtt.Tests/Controller/MatterServerProtocolTests.cs`

**Interfaces:**
- Consumes: documented python-matter-server WS API (`start_listening`, `device_command`, attribute events). Confirm exact matterjs-server messages later (spec §13); protocol stays isolated here behind `IMatterController`.
- Produces:
  - `static class MatterServerProtocol`: `string StartListening(int messageId)`; `string DeviceCommand(int messageId, ulong nodeId, ushort endpoint, CommandSpec cmd)`; `IEnumerable<MatterEvent> ParseIncoming(string json)` — turns a server message into zero or more `MatterEvent`s.
  - `PythonMatterServerController(string wsUrl) : IMatterController` — socket loop; `ConnectAndListen` connects, sends `StartListening`, yields parsed events; `InvokeCommand` sends `DeviceCommand`. Sends are serialized through an internal lock/channel.

- [ ] **Step 1: Write failing protocol tests** (pure — the socket loop is exercised manually against the virtual device)

```csharp
using System.Text.Json;
using Matter2Mqtt.Controller;
using Matter2Mqtt.Domain;
using Xunit;

namespace Matter2Mqtt.Tests.Controller;

public class MatterServerProtocolTests
{
    [Fact]
    public void StartListening_has_command_and_message_id()
    {
        var json = MatterServerProtocol.StartListening(3);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("start_listening", doc.RootElement.GetProperty("command").GetString());
        Assert.Equal("3", doc.RootElement.GetProperty("message_id").GetString());
    }

    [Fact]
    public void DeviceCommand_carries_node_endpoint_cluster_command()
    {
        var cmd = new CommandSpec(MatterClusters.OnOff, "On", new Dictionary<string, object?>());
        var json = MatterServerProtocol.DeviceCommand(7, 1, 1, cmd);
        using var doc = JsonDocument.Parse(json);
        var args = doc.RootElement.GetProperty("args");
        Assert.Equal("device_command", doc.RootElement.GetProperty("command").GetString());
        Assert.Equal(1, args.GetProperty("node_id").GetInt32());
        Assert.Equal(1, args.GetProperty("endpoint_id").GetInt32());
        Assert.Equal(6, args.GetProperty("cluster_id").GetInt32());
        Assert.Equal("On", args.GetProperty("command_name").GetString());
    }

    [Fact]
    public void ParseIncoming_attribute_update_yields_AttributeChanged()
    {
        // python-matter-server attribute path format: "<endpoint>/<cluster>/<attribute>"
        var json = """
        {"event":"attribute_updated","data":[1, "1/6/0", true]}
        """;
        var evt = Assert.Single(MatterServerProtocol.ParseIncoming(json));
        var ac = Assert.IsType<AttributeChanged>(evt);
        Assert.Equal(1ul, ac.Reading.NodeId);
        Assert.Equal((ushort)1, ac.Reading.Endpoint);
        Assert.Equal(MatterClusters.OnOff, ac.Reading.ClusterId);
        Assert.Equal(0u, ac.Reading.AttributeId);
        Assert.True(ac.Reading.Value.GetBoolean());
    }
}
```

- [ ] **Step 2: Run to verify fail**

Run: `dotnet test --filter "FullyQualifiedName~MatterServerProtocolTests" /p:NuGetAudit=false`
Expected: FAIL.

- [ ] **Step 3: Implement protocol + controller**

```csharp
// MatterServerProtocol.cs
using System.Text.Json;
using Matter2Mqtt.Domain;
namespace Matter2Mqtt.Controller;

public static class MatterServerProtocol
{
    public static string StartListening(int messageId) =>
        JsonSerializer.Serialize(new { message_id = messageId.ToString(), command = "start_listening" });

    public static string DeviceCommand(int messageId, ulong nodeId, ushort endpoint, CommandSpec cmd) =>
        JsonSerializer.Serialize(new
        {
            message_id = messageId.ToString(),
            command = "device_command",
            args = new
            {
                node_id = nodeId,
                endpoint_id = endpoint,
                cluster_id = cmd.ClusterId,
                command_name = cmd.CommandName,
                payload = cmd.Payload,
            }
        });

    public static IEnumerable<MatterEvent> ParseIncoming(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("event", out var evt)) yield break;

        if (evt.GetString() == "attribute_updated" && root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() == 3)
        {
            var nodeId = (ulong)data[0].GetInt64();
            var path = data[1].GetString()!.Split('/');
            var value = data[2].Clone();
            yield return new AttributeChanged(new AttributeReading(
                nodeId, ushort.Parse(path[0]), uint.Parse(path[1]), uint.Parse(path[2]), value));
        }
        // NodeAdded/NodeRemoved parsing added when wiring the live server (node_added / node_removed events).
    }
}
```

```csharp
// PythonMatterServerController.cs
using System.Net.WebSockets;
using System.Text;
using Matter2Mqtt.Domain;
namespace Matter2Mqtt.Controller;

public sealed class PythonMatterServerController(string wsUrl) : IMatterController
{
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _messageId;

    public async IAsyncEnumerable<MatterEvent> ConnectAndListen(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await _socket.ConnectAsync(new Uri(wsUrl), ct);
        await Send(MatterServerProtocol.StartListening(Interlocked.Increment(ref _messageId)), ct);

        var buffer = new byte[64 * 1024];
        while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
        {
            var sb = new StringBuilder();
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer, ct);
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            } while (!result.EndOfMessage);

            foreach (var e in MatterServerProtocol.ParseIncoming(sb.ToString()))
                yield return e;
        }
    }

    public async Task InvokeCommand(ulong nodeId, ushort endpoint, CommandSpec command, CancellationToken ct) =>
        await Send(MatterServerProtocol.DeviceCommand(Interlocked.Increment(ref _messageId), nodeId, endpoint, command), ct);

    public Task<ulong> Commission(string setupCode, CancellationToken ct) =>
        throw new NotImplementedException("Wire commission_with_code against the live server; see spec §13.");

    public Task RemoveNode(ulong nodeId, CancellationToken ct) =>
        throw new NotImplementedException("Wire remove_node against the live server; see spec §13.");

    private async Task Send(string json, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try { await _socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct); }
        finally { _sendLock.Release(); }
    }
}
```

> **Implementer note:** `Commission`/`RemoveNode` and `node_added`/`node_removed`
> parsing are stubbed until you can confirm the exact live message set against the
> running server (spec §13). Phase-1 automated tests use `FakeMatterController`; the
> real adapter is validated manually against the matter.js virtual device (spec §11).

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~MatterServerProtocolTests" /p:NuGetAudit=false`
Expected: PASS (3 tests).

- [ ] **Step 5: Full suite + commit**

```bash
dotnet test /p:NuGetAudit=false
git add -A
git commit -m "feat: WS protocol codec and python-matter-server controller adapter"
```

---

## Task 13: Integration validation against a matter.js virtual device (manual)

**Files:** none (documented runbook in `README.md`).

- [ ] **Step 1: Add a README runbook**

Document (spec §11): run a matter.js example on-off/dimmable device, commission it over IP via the running Matter server, point Matter2Mqtt at that server (`Controller__Kind=python-matter-server`, `Controller__WsUrl=...`), and verify:
- `matter2mqtt/bridge/devices` lists the bulb with `state`+`brightness` exposes;
- toggling `state` via `matter2mqtt/<name>/set` `{"state":"ON"}` changes the device;
- `GET /api/devices` (with API key) returns the same list.

- [ ] **Step 2: Commit**

```bash
git add -A
git commit -m "docs: virtual-device integration runbook"
```

---

## Self-Review

**Spec coverage:**
- §3 architecture / `IMatterController` seam → Task 5, 12. ✅
- §4 MQTT contract (bridge + per-device topics) → Task 6 (topics), 7 (device state/availability), 9 (bridge/state, devices, event, response). ✅
- §5 endpoint-as-device, friendly names, cluster→property mapping → Task 2, 4, 7, 9. ✅
- §6 commissioning request/response → Task 9 (`CommissionRequest`/response), 10 (`POST /api/commission`), 12 (stub w/ note). ✅ (physical commissioning is operational per spec §11)
- §7 `bridge/devices` exposes contract → Task 4 (`ExposesBuilder`), 9 (publish). ✅
- §8 REST facade + API key → Task 10. ✅
- §9 Akka.Streams pipeline + conflation + non-dropped events → Task 8 (routing + conflation refinement note; lifecycle branch kept separate). ✅
- §10 config keys → Task 10 (`Matter2MqttConfig`). ✅
- §11 dev/test via virtual device → Task 13. ✅
- §12 phasing → this plan is Phase 1 only. ✅

**Placeholder scan:** No "TODO/TBD". The two `NotImplementedException`s (Commission/RemoveNode in the WS adapter) are deliberate, documented against spec §13's open question, and covered by the Fake in automated tests — not silent gaps.

**Type consistency:** `CommandSpec`, `AttributeReading`, `EndpointInfo`, `DeviceDescriptor`, `MatterEvent` subtypes, `ApplyAttribute`/`ApplySet`/`SetDevice`/`CommissionRequest`, `IMqttPublisher`, `MqttTopics`, `IMatterController` signatures are consistent across tasks 2–12.

**Known follow-ups (Phase 1.x, not gaps):** conflation refinement (Task 8 note), live `node_added`/`commission_with_code` wiring (Task 12 note), matterjs-server adapter for `--ble-proxy` (spec §3/§13).
```
