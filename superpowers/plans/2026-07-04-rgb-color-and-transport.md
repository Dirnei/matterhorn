# RGB Color Control + Transport Badge — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expose a Matter bulb's Hue/Saturation color as flat `hue`/`saturation` properties driven by an iro.js color wheel, and show each device's Wi-Fi/Thread/Ethernet transport.

**Architecture:** Server stays color-math-free — it passes raw Matter 0–254 hue/saturation through `PropertyMapping`/`CommandMapping` exactly like `brightness`. The dashboard's picker does the only RGB↔HS work (a linear scale, since iro.js is HSV-native). Color exposes are gated on the `ColorControl` FeatureMap; transport is read from the `NetworkCommissioning` FeatureMap. Spec: `superpowers/specs/2026-07-04-rgb-color-and-transport-design.md`.

**Tech Stack:** .NET 10, Akka.NET, xUnit, contract-first OpenAPI (NSwag), vanilla-JS dashboard + vendored iro.js v5.

## Global Constraints

- **Color values are raw Matter uint8 0–254** on the wire (same as `brightness`); no RGB/hex in the contract.
- **Brightness is never part of color** — `hue`/`saturation` set hue+sat only.
- **TDD**: write the failing test first, watch it fail, then implement. Run `dotnet test src/Matterhorn.Test/Matterhorn.Test.csproj`.
- **Do not add any `Co-Authored-By` / co-author trailer** to commit messages.
- **Do not run the app via `dotnet run`** for live checks — use Docker (`docker compose up --build`, `.env` points at the Pi). `dotnet test`/`dotnet build` directly is fine.
- ColorControl FeatureMap bits: `0x01`=HS, `0x08`=XY, `0x10`=CT. NetworkCommissioning FeatureMap bits: `0x01`=Wi-Fi, `0x02`=Thread, `0x04`=Ethernet.

---

### Task 1: Parse transport + color features in the node codec

**Files:**
- Modify: `src/Matterhorn/Matter/MatterClusters.cs`
- Modify: `src/Matterhorn/Bridge/DeviceDescriptor.cs` (add fields to `EndpointInfo` and `DeviceDescriptor`)
- Modify: `src/Matterhorn/Matter/MatterServerProtocol.cs` (`ParseNode`)
- Test: `src/Matterhorn.Test/Matter/MatterServerProtocolTests.cs`

**Interfaces:**
- Produces: `EndpointInfo` gains `string Transport = "unknown"` and `uint ColorFeatures = 0`; `DeviceDescriptor` gains `string Transport = "unknown"`. `MatterClusters.NetworkCommissioning = 0x0031`.

- [ ] **Step 1: Write the failing tests** — append to `MatterServerProtocolTests.cs`:

```csharp
    [Fact]
    public void ParseNode_reads_wifi_transport_and_color_features()
    {
        var json = """
        {"event":"node_added","data":{"node_id":1,"available":true,"attributes":{
            "0/49/65532":1,
            "1/29/1":[6,8,768],"1/768/65532":25
        }}}
        """;
        var info = Assert.IsType<NodeAdded>(Assert.Single(MatterServerProtocol.ParseIncoming(json))).Endpoint;
        Assert.Equal("wifi", info.Transport);
        Assert.Equal(25u, info.ColorFeatures);
    }

    [Theory]
    [InlineData(1, "wifi")]
    [InlineData(2, "thread")]
    [InlineData(4, "ethernet")]
    [InlineData(3, "thread")]   // Thread wins when both bits set
    [InlineData(0, "unknown")]
    public void ParseNode_decodes_transport_from_network_commissioning(int featureMap, string expected)
    {
        var json = $$"""
        {"event":"node_added","data":{"node_id":1,"available":true,"attributes":{
            "0/49/65532":{{featureMap}},"1/29/1":[6]
        }}}
        """;
        var info = Assert.IsType<NodeAdded>(Assert.Single(MatterServerProtocol.ParseIncoming(json))).Endpoint;
        Assert.Equal(expected, info.Transport);
    }
```

- [ ] **Step 2: Run tests, verify they fail**

Run: `dotnet test src/Matterhorn.Test/Matterhorn.Test.csproj --filter "FullyQualifiedName~ParseNode_reads_wifi|FullyQualifiedName~decodes_transport"`
Expected: FAIL — compile error, `EndpointInfo` has no `Transport`/`ColorFeatures`.

- [ ] **Step 3: Add the constant and record fields**

In `MatterClusters.cs`, under the utility clusters:
```csharp
    public const uint NetworkCommissioning = 0x0031;
```

In `Bridge/DeviceDescriptor.cs`, change `EndpointInfo` and `DeviceDescriptor` to:
```csharp
public record EndpointInfo(
    ulong NodeId, ushort Endpoint, string? VendorName, string? ProductName,
    ushort VendorId, ushort ProductId, string DeviceType, bool Reachable,
    IReadOnlyList<uint> ClusterIds, string Transport = "unknown", uint ColorFeatures = 0);

public record DeviceDescriptor(
    string FriendlyName, string NodeId, ushort Endpoint,
    string? VendorName, string? ProductName, ushort VendorId, ushort ProductId,
    string DeviceType, bool Reachable, IReadOnlyList<ExposeEntry> Exposes, string Transport = "unknown");
```

- [ ] **Step 4: Capture transport + color features in `ParseNode`**

In `MatterServerProtocol.ParseNode`, add locals next to `serverLists`/`deviceTypes`:
```csharp
        var transport = "unknown";
        var colorFeatures = new Dictionary<ushort, uint>();
```
Add two branches inside the `foreach (var attr in attrs.EnumerateObject())` loop, after the existing Descriptor branch:
```csharp
            else if (ep == 0 && cluster == MatterClusters.NetworkCommissioning && attribute == 0xFFFC
                     && attr.Value.ValueKind == JsonValueKind.Number && attr.Value.TryGetUInt32(out var netFm))
                transport = DecodeTransport(netFm);
            else if (cluster == MatterClusters.ColorControl && attribute == 0xFFFC
                     && attr.Value.ValueKind == JsonValueKind.Number && attr.Value.TryGetUInt32(out var colFm))
                colorFeatures[ep] = colFm;
```
Change the final `yield return` to pass the new fields:
```csharp
        foreach (var (ep, clusters) in serverLists)
        {
            var type = deviceTypes.TryGetValue(ep, out var dt) ? DeviceTypeName(dt) : "Unknown";
            yield return new NodeAdded(new EndpointInfo(
                nodeId, ep, vendorName, productName, vendorId, productId, type, reachable, clusters,
                transport, colorFeatures.GetValueOrDefault(ep)));
        }
```
Add the helper near `DeviceTypeName`:
```csharp
    private static string DecodeTransport(uint featureMap) =>
        (featureMap & 0x02) != 0 ? "thread"
        : (featureMap & 0x01) != 0 ? "wifi"
        : (featureMap & 0x04) != 0 ? "ethernet"
        : "unknown";
```

- [ ] **Step 5: Run tests, verify they pass**

Run: `dotnet test src/Matterhorn.Test/Matterhorn.Test.csproj --filter "FullyQualifiedName~ParseNode_reads_wifi|FullyQualifiedName~decodes_transport"`
Expected: PASS (6 cases).

- [ ] **Step 6: Run the full suite** — `dotnet test src/Matterhorn.Test/Matterhorn.Test.csproj` → all green.

- [ ] **Step 7: Commit**

```bash
git add src/Matterhorn/Matter/MatterClusters.cs src/Matterhorn/Bridge/DeviceDescriptor.cs src/Matterhorn/Matter/MatterServerProtocol.cs src/Matterhorn.Test/Matter/MatterServerProtocolTests.cs
git commit -m "feat: parse transport + color features from node attributes"
```

---

### Task 2: Map color reads (hue / saturation / color_mode)

**Files:**
- Modify: `src/Matterhorn/Devices/PropertyMapping.cs`
- Test: `src/Matterhorn.Test/Devices/PropertyMappingTests.cs`

**Interfaces:**
- Produces: readings on `ColorControl` attrs 0/1/8 map to `hue` (int), `saturation` (int), `color_mode` (string).

- [ ] **Step 1: Write the failing test** — append to `PropertyMappingTests.cs`:

```csharp
    [Fact]
    public void Maps_hue_saturation_and_color_mode()
    {
        static JsonElement J(string s) => JsonDocument.Parse(s).RootElement;
        var readings = new[]
        {
            new AttributeReading(1, 1, MatterClusters.ColorControl, 0, J("19")),
            new AttributeReading(1, 1, MatterClusters.ColorControl, 1, J("58")),
            new AttributeReading(1, 1, MatterClusters.ColorControl, 8, J("0")),
        };
        var m = PropertyMapping.Map(readings);
        Assert.Equal(19, m["hue"]);
        Assert.Equal(58, m["saturation"]);
        Assert.Equal("hs", m["color_mode"]);
    }
```

- [ ] **Step 2: Run test, verify it fails**

Run: `dotnet test src/Matterhorn.Test/Matterhorn.Test.csproj --filter "FullyQualifiedName~Maps_hue_saturation"`
Expected: FAIL — `KeyNotFoundException` for `"hue"`.

- [ ] **Step 3: Add the rules** — in `PropertyMapping.cs`, add to the `Rules` array (after the `color_temp` rule):

```csharp
        new(MatterClusters.ColorControl, 0, "hue", v => v.GetInt32()),
        new(MatterClusters.ColorControl, 1, "saturation", v => v.GetInt32()),
        new(MatterClusters.ColorControl, 8, "color_mode",
            v => v.GetInt32() switch { 0 => "hs", 1 => "xy", 2 => "ct", _ => "unknown" }),
```

- [ ] **Step 4: Run test, verify it passes**

Run: `dotnet test src/Matterhorn.Test/Matterhorn.Test.csproj --filter "FullyQualifiedName~Maps_hue_saturation"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Matterhorn/Devices/PropertyMapping.cs src/Matterhorn.Test/Devices/PropertyMappingTests.cs
git commit -m "feat: map ColorControl hue/saturation/color_mode reads"
```

---

### Task 3: Map color writes (MoveToHueAndSaturation / MoveToHue / MoveToSaturation)

**Files:**
- Modify: `src/Matterhorn/Devices/CommandMapping.cs`
- Test: `src/Matterhorn.Test/Devices/CommandMappingTests.cs`

**Interfaces:**
- Produces: a `{hue,saturation}` set → one `MoveToHueAndSaturation`; a single key → `MoveToHue` (with `direction`) / `MoveToSaturation`.

- [ ] **Step 1: Write the failing tests** — append to `CommandMappingTests.cs`:

```csharp
    [Fact]
    public void Hue_and_saturation_together_produce_one_MoveToHueAndSaturation()
    {
        static JsonElement J(string s) => JsonDocument.Parse(s).RootElement;
        var payload = new Dictionary<string, JsonElement> { ["hue"] = J("100"), ["saturation"] = J("200") };
        var cmd = Assert.Single(CommandMapping.Map(payload));
        Assert.Equal(MatterClusters.ColorControl, cmd.ClusterId);
        Assert.Equal("MoveToHueAndSaturation", cmd.CommandName);
        Assert.Equal(100, cmd.Payload["hue"]);
        Assert.Equal(200, cmd.Payload["saturation"]);
    }

    [Fact]
    public void Hue_only_produces_MoveToHue()
    {
        static JsonElement J(string s) => JsonDocument.Parse(s).RootElement;
        var cmd = Assert.Single(CommandMapping.Map(new Dictionary<string, JsonElement> { ["hue"] = J("42") }));
        Assert.Equal("MoveToHue", cmd.CommandName);
        Assert.Equal(42, cmd.Payload["hue"]);
    }

    [Fact]
    public void Saturation_only_produces_MoveToSaturation()
    {
        static JsonElement J(string s) => JsonDocument.Parse(s).RootElement;
        var cmd = Assert.Single(CommandMapping.Map(new Dictionary<string, JsonElement> { ["saturation"] = J("77") }));
        Assert.Equal("MoveToSaturation", cmd.CommandName);
        Assert.Equal(77, cmd.Payload["saturation"]);
    }
```

- [ ] **Step 2: Run tests, verify they fail**

Run: `dotnet test src/Matterhorn.Test/Matterhorn.Test.csproj --filter "FullyQualifiedName~MoveToHue|FullyQualifiedName~MoveToSaturation"`
Expected: FAIL — `Map` returns empty, `Assert.Single` throws.

- [ ] **Step 3: Add color handling** — in `CommandMapping.Map`, immediately before `return cmds;`:

```csharp
        // Color: hue+sat travel together as "the color" -> one combined command; a lone key -> individual.
        var hasHue = setPayload.TryGetValue("hue", out var hue);
        var hasSat = setPayload.TryGetValue("saturation", out var sat);
        if (hasHue && hasSat)
            cmds.Add(new(MatterClusters.ColorControl, "MoveToHueAndSaturation",
                new Dictionary<string, object?> { ["hue"] = hue.GetInt32(), ["saturation"] = sat.GetInt32() }));
        else if (hasHue)
            cmds.Add(new(MatterClusters.ColorControl, "MoveToHue",
                new Dictionary<string, object?> { ["hue"] = hue.GetInt32(), ["direction"] = 0 }));
        else if (hasSat)
            cmds.Add(new(MatterClusters.ColorControl, "MoveToSaturation",
                new Dictionary<string, object?> { ["saturation"] = sat.GetInt32() }));
```

- [ ] **Step 4: Run tests, verify they pass**

Run: `dotnet test src/Matterhorn.Test/Matterhorn.Test.csproj --filter "FullyQualifiedName~MoveToHue|FullyQualifiedName~MoveToSaturation"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Matterhorn/Devices/CommandMapping.cs src/Matterhorn.Test/Devices/CommandMappingTests.cs
git commit -m "feat: map hue/saturation sets to ColorControl commands"
```

---

### Task 4: Gate color exposes on the FeatureMap + carry transport into the descriptor

**Files:**
- Modify: `src/Matterhorn/Bridge/ExposesBuilder.cs`
- Modify: `src/Matterhorn/Bridge/MatterGatewayActor.cs` (`OnNodeAdded`)
- Test: `src/Matterhorn.Test/Bridge/ExposesBuilderTests.cs`, `src/Matterhorn.Test/Bridge/MatterGatewayActorTests.cs`

**Interfaces:**
- Consumes: `EndpointInfo.ColorFeatures`, `EndpointInfo.Transport` (Task 1).
- Produces: `ExposesBuilder.Build(IReadOnlyList<uint> clusterIds, uint colorFeatures)` — HS bit → `hue`+`saturation` exposes, CT bit → `color_temp`. `DeviceDescriptor.Transport` populated by the gateway.

- [ ] **Step 1: Write the failing tests.** In `ExposesBuilderTests.cs`, add (and update any existing test that calls `Build(...)` with one argument to pass `0x10` so `color_temp` still appears):

```csharp
    [Fact]
    public void Hs_feature_adds_hue_and_saturation()
    {
        var exposes = ExposesBuilder.Build(new[] { MatterClusters.ColorControl }, 0x01);
        Assert.Contains(exposes, e => e.Property == "hue");
        Assert.Contains(exposes, e => e.Property == "saturation");
        Assert.DoesNotContain(exposes, e => e.Property == "color_temp");
    }

    [Fact]
    public void Ct_feature_without_hs_adds_only_color_temp()
    {
        var exposes = ExposesBuilder.Build(new[] { MatterClusters.ColorControl }, 0x10);
        Assert.Contains(exposes, e => e.Property == "color_temp");
        Assert.DoesNotContain(exposes, e => e.Property == "hue");
    }
```

In `MatterGatewayActorTests.cs`, add:
```csharp
    [Fact]
    public void NodeAdded_carries_transport_into_the_descriptor()
    {
        var fake = new FakeMatterController();
        var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, new InMemoryMqttPublisher(), new MqttTopics("matterhorn")));
        fake.Emit(new NodeAdded(new EndpointInfo(1, 1, "V", "P", 1, 1, "Extended Color Light", true,
            new[] { MatterClusters.OnOff }, "wifi", 0x01)));

        AwaitAssert(() => Assert.Equal("wifi",
            gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result[0].Transport));
    }
```

- [ ] **Step 2: Run tests, verify they fail**

Run: `dotnet test src/Matterhorn.Test/Matterhorn.Test.csproj --filter "FullyQualifiedName~Hs_feature|FullyQualifiedName~Ct_feature|FullyQualifiedName~carries_transport"`
Expected: FAIL — `Build` has no 2-arg overload; descriptor has no transport wired.

- [ ] **Step 3: Change `ExposesBuilder.Build`.** Replace the signature and the `ColorControl` block:

```csharp
    public static IReadOnlyList<ExposeEntry> Build(IReadOnlyList<uint> clusterIds, uint colorFeatures)
    {
        var list = new List<ExposeEntry>();
        var has = new HashSet<uint>(clusterIds);

        if (has.Contains(MatterClusters.OnOff))
            list.Add(new("binary", "state", All, ValueOn: "ON", ValueOff: "OFF"));
        if (has.Contains(MatterClusters.LevelControl))
            list.Add(new("numeric", "brightness", All, ValueMin: 0, ValueMax: 254));
        if (has.Contains(MatterClusters.ColorControl))
        {
            if ((colorFeatures & 0x01) != 0) // Hue/Saturation
            {
                list.Add(new("numeric", "hue", All, ValueMin: 0, ValueMax: 254));
                list.Add(new("numeric", "saturation", All, ValueMin: 0, ValueMax: 254));
            }
            if ((colorFeatures & 0x10) != 0) // Color Temperature
                list.Add(new("numeric", "color_temp", All, ValueMin: 147, ValueMax: 500, Unit: "mired"));
        }
```
(Leave the rest of the method — BooleanState, OccupancySensing, etc. — unchanged, ending with `return list;`.)

- [ ] **Step 4: Wire the gateway.** In `MatterGatewayActor.OnNodeAdded`, change the descriptor construction:

```csharp
        var descriptor = new DeviceDescriptor(name, info.NodeId.ToString(), info.Endpoint,
            info.VendorName, info.ProductName, info.VendorId, info.ProductId, info.DeviceType, info.Reachable,
            ExposesBuilder.Build(info.ClusterIds, info.ColorFeatures), info.Transport);
```

- [ ] **Step 5: Run tests, verify they pass** — the filtered tests, then the full suite:

Run: `dotnet test src/Matterhorn.Test/Matterhorn.Test.csproj`
Expected: PASS (fix any older `ExposesBuilder.Build(x)` call sites in tests to `Build(x, 0x10)`).

- [ ] **Step 6: Commit**

```bash
git add src/Matterhorn/Bridge/ExposesBuilder.cs src/Matterhorn/Bridge/MatterGatewayActor.cs src/Matterhorn.Test/Bridge/ExposesBuilderTests.cs src/Matterhorn.Test/Bridge/MatterGatewayActorTests.cs
git commit -m "feat: gate color exposes on FeatureMap, carry transport into descriptor"
```

---

### Task 5: Expose transport + color set through the OpenAPI contract

**Files:**
- Modify: `contracts/matterhorn.openapi.yaml` (`Device` schema, `SetRequest` schema)
- Modify: `src/Matterhorn/Api/MatterhornController.cs` (`ToDto`, `SetDeviceState`)
- Test: `src/Matterhorn.Test/Api/MatterhornControllerTests.cs`

**Interfaces:**
- Consumes: `DeviceDescriptor.Transport` (Task 1). Generated DTOs `Gen.Device.Transport`, `Gen.SetRequest.Hue`, `Gen.SetRequest.Saturation` appear after the YAML edit + rebuild (NSwag runs on build).

- [ ] **Step 1: Write the failing test** — append to `MatterhornControllerTests.cs`:

```csharp
    [Fact]
    public async Task Patch_device_forwards_hue_and_saturation()
    {
        var probe = CreateTestProbe();
        var client = ClientWithGateway(probe.Ref);

        var task = client.PatchAsJsonAsync("/api/devices/lamp",
            new Dictionary<string, int> { ["hue"] = 100, ["saturation"] = 200 });

        var msg = probe.ExpectMsg<SetDevice>();
        Assert.Equal(100, msg.Payload["hue"].GetInt32());
        Assert.Equal(200, msg.Payload["saturation"].GetInt32());
        Assert.Equal(HttpStatusCode.Accepted, (await task).StatusCode);
    }
```

- [ ] **Step 2: Run test, verify it fails**

Run: `dotnet test src/Matterhorn.Test/Matterhorn.Test.csproj --filter "FullyQualifiedName~forwards_hue_and_saturation"`
Expected: FAIL — `SetDevice` payload has no `hue` (unknown JSON props are dropped by the current `SetRequest`).

- [ ] **Step 3: Extend the contract.** In `contracts/matterhorn.openapi.yaml`, add to the `Device` schema properties (after `reachable`):
```yaml
        transport: { type: string, description: 'wifi | thread | ethernet | unknown' }
```
And to the `SetRequest` schema properties (after `color_temp`):
```yaml
        hue: { type: integer, format: int32, minimum: 0, maximum: 254, nullable: true }
        saturation: { type: integer, format: int32, minimum: 0, maximum: 254, nullable: true }
```

- [ ] **Step 4: Map the new fields.** In `MatterhornController.cs`, add to `ToDto(DeviceDescriptor d)` (after `Reachable`):
```csharp
        Transport = d.Transport,
```
And in `SetDeviceState`, after the `color_temp` line:
```csharp
        if (body.Hue.HasValue) payload["hue"] = JsonSerializer.SerializeToElement(body.Hue.Value);
        if (body.Saturation.HasValue) payload["saturation"] = JsonSerializer.SerializeToElement(body.Saturation.Value);
```

- [ ] **Step 5: Rebuild (regenerates DTOs) and run the test**

Run: `dotnet build src/Matterhorn/Matterhorn.csproj` then
`dotnet test src/Matterhorn.Test/Matterhorn.Test.csproj --filter "FullyQualifiedName~forwards_hue_and_saturation"`
Expected: build succeeds (NSwag regenerates `Gen.SetRequest`/`Gen.Device`), test PASSES. If the generated property is `Hue`/`Saturation`/`Transport` with different casing, match the generated names.

- [ ] **Step 6: Full suite** — `dotnet test src/Matterhorn.Test/Matterhorn.Test.csproj` → green.

- [ ] **Step 7: Commit**

```bash
git add contracts/matterhorn.openapi.yaml src/Matterhorn/Api/MatterhornController.cs src/Matterhorn.Test/Api/MatterhornControllerTests.cs
git commit -m "feat: expose transport + hue/saturation in the REST contract"
```

---

### Task 6: Show color on the fake demo stack

**Files:**
- Modify: `src/Matterhorn/Dev/DemoDeviceSeeder.cs`

**Interfaces:** none produced; makes the fake bulb exercise the color path so the demo stack (`DevSeed=true`) shows a working picker.

- [ ] **Step 1: Give the demo devices color features + transport.** In `DemoDeviceSeeder.cs`, change the two `EndpointInfo` constructions:
```csharp
        var bulb = new EndpointInfo(1, 1, "Nanoleaf", "Essentials Bulb", 4442, 3, "Extended Color Light", true,
            new[] { MatterClusters.OnOff, MatterClusters.LevelControl, MatterClusters.ColorControl },
            "wifi", 0x1D);   // HS + XY + CT
        var sensor = new EndpointInfo(2, 1, "Aqara", "Motion Sensor", 4447, 42, "OccupancySensor", true,
            new[]
            {
                MatterClusters.OccupancySensing, MatterClusters.TemperatureMeasurement,
                MatterClusters.RelativeHumidityMeasurement, MatterClusters.PowerSource,
            }, "thread", 0);
```

- [ ] **Step 2: Seed initial hue/saturation + color_mode readings.** After the existing bulb `ColorControl` emit line, add:
```csharp
        fake.Emit(new AttributeChanged(new AttributeReading(1, 1, MatterClusters.ColorControl, 0, J("40"))));   // hue
        fake.Emit(new AttributeChanged(new AttributeReading(1, 1, MatterClusters.ColorControl, 1, J("180"))));  // saturation
        fake.Emit(new AttributeChanged(new AttributeReading(1, 1, MatterClusters.ColorControl, 8, J("0"))));    // color_mode = hs
```

- [ ] **Step 3: Verify it builds** — `dotnet build src/Matterhorn/Matterhorn.csproj` → succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Matterhorn/Dev/DemoDeviceSeeder.cs
git commit -m "chore: give demo bulb color features for dashboard testing"
```

---

### Task 7: Dashboard — iro.js color wheel, transport pill, active-mode hint

**Files:**
- Create: `src/Matterhorn/wwwroot/iro.min.js` (vendored)
- Modify: `src/Matterhorn/wwwroot/index.html`

**Interfaces:** consumes `hue`/`saturation` exposes + state and `device.transport` + `state.color_mode` from Tasks 1–5.

- [ ] **Step 1: Vendor iro.js**

Run: `curl.exe -L -o src/Matterhorn/wwwroot/iro.min.js https://cdn.jsdelivr.net/npm/@jaames/iro@5/dist/iro.min.js`
Verify it downloaded (non-trivial size): `ls -l src/Matterhorn/wwwroot/iro.min.js` should show ~30–60 KB. Add `<script src="iro.min.js"></script>` in `index.html` just before the existing `<script>` block (line ~132).

- [ ] **Step 2: Add color-pill + inactive styles** — in the `<style>` block, add:
```css
  .pill { display:inline-block; font-family:var(--mono); font-size:10px; letter-spacing:.05em; text-transform:uppercase;
          padding:1px 6px; border:1px solid var(--line); border-radius:999px; color:var(--muted); margin-left:6px; vertical-align:middle; }
  .picker { display:flex; justify-content:center; padding:4px 0 2px; }
  .inactive { opacity:.4; }
```

- [ ] **Step 3: Render a transport pill** — in `station(d)`, change the `.coords` line to append the pill:
```javascript
         <div class="coords">${esc(d.device_type)} · node ${esc(d.node_id)} / ep ${d.endpoint}${d.transport?`<span class="pill">${esc(d.transport)}</span>`:''}</div>
```

- [ ] **Step 4: Render the color wheel instead of hue/sat sliders** — in `station(d)`, after the `settable`/`readonly` lines, exclude color from the generic rows and add a picker when both exposes exist:
```javascript
  const hasColor = d.exposes.some(e=>e.property==='hue') && d.exposes.some(e=>e.property==='saturation');
  const skip = new Set(hasColor ? ['hue','saturation'] : []);
  const settable2 = settable.filter(e=>!skip.has(e.property));
  const readonly2 = readonly.filter(e=>!skip.has(e.property));
```
Use `settable2`/`readonly2` in the two loops that append rows. Then, after those loops (before `return el;`), add:
```javascript
  if (hasColor) rows.prepend(colorControl(d.friendly_name));
```

- [ ] **Step 5: Add the picker factory + conversions** — in the `<script>`, add:
```javascript
const pickers = new Map();           // friendly_name -> { picker, dragging }
const H=254; // Matter hue/sat max
function colorControl(name){
  const wrap=document.createElement('div'); wrap.className='ctl';
  wrap.innerHTML=`<div class="row"><span class="legend">color</span></div><div class="picker" data-picker="1"></div>`;
  const mount=wrap.querySelector('[data-picker]');
  const picker=new iro.ColorPicker(mount,{width:150,color:{h:0,s:0,v:100},layout:[{component:iro.ui.Wheel}]});
  const rec={picker,dragging:false};
  pickers.set(name,rec);
  picker.on('input:start',()=>rec.dragging=true);
  picker.on('input:end',()=>{ rec.dragging=false;
    const {h,s}=picker.color.hsv;
    patch(name,{hue:Math.round(h*H/360), saturation:Math.round(s*H/100)});
  });
  return wrap;
}
```

- [ ] **Step 6: Sync the picker from live state** — in `applyState(name)`, after the loop over `Object.entries(s)`, add:
```javascript
  const rec=pickers.get(name);
  if (rec && !rec.dragging && s.hue!=null && s.saturation!=null)
    rec.picker.color.hsv={ h:s.hue*360/H, s:s.saturation*100/H, v:100 };
  // De-emphasize whichever color representation is not the live one.
  if (rec){
    const pickerEl=document.querySelector(`#dev-${cssId(name)} .picker`);
    if (pickerEl) pickerEl.classList.toggle('inactive', s.color_mode==='ct');
    const ctRow=document.querySelector(`#dev-${cssId(name)} input[type=range][data-prop="color_temp"]`);
    if (ctRow) ctRow.closest('.ctl')?.classList.toggle('inactive', s.color_mode==='hs');
  }
```

- [ ] **Step 7: Drop stale pickers on re-render** — at the top of `render()`, add `pickers.clear();` (the grid is rebuilt from scratch, so old picker instances must be discarded).

- [ ] **Step 8: Live verification (Docker, against the Pi)**

Ensure `.env` has `CONTROLLER_KIND=python-matter-server` and `CONTROLLER_WS_URL=ws://192.168.0.118:5580/ws`, then:
```bash
docker compose up --build -d
```
Open http://localhost:16090/ and confirm on the H600C card:
- a **Wi-Fi** pill in the header,
- a **color wheel** that, when dragged, changes the real bulb's color,
- turning the wheel updates live (release, then change color from Apple Home → wheel follows),
- `color_temp` slider appears de-emphasized while the bulb is in HS mode.
Then `docker compose down`.

- [ ] **Step 9: Commit**

```bash
git add src/Matterhorn/wwwroot/iro.min.js src/Matterhorn/wwwroot/index.html
git commit -m "feat: color wheel picker + transport pill on the dashboard"
```

---

## Notes for the implementer

- The dashboard (Task 7) has no JS test harness; its "test" is the live Docker check in Step 8. All backend tasks (1–5) are real red-green TDD.
- If NSwag names a generated property differently than assumed (e.g. `Color_temp` casing), match the generated code — the YAML is the source of truth, the C# follows it.
- Keep the server free of color math: hue/saturation are integers passed straight through, exactly like `brightness`.
