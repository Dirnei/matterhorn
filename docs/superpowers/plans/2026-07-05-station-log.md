# Station Log Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a collapsible bottom "Station Log" console to the dashboard that streams live events in two modes (Activity milestones / Raw wire firehose), with recent history replayed on connect.

**Architecture:** Reuse the existing Akka **EventStream → SSE** path. The gateway publishes a new `LogEntry` message at milestone and wire-event points; a singleton `LogBufferActor` retains a bounded ring buffer and answers a snapshot Ask; the `/api/events` SSE endpoint replays the snapshot then streams live `log` frames; the dashboard renders them in a dock. No REST/OpenAPI, MQTT, or persistence changes.

**Tech Stack:** .NET 10, Akka.NET (ReceiveActor, EventStream), Akka.Hosting (ActorRegistry), ASP.NET minimal API (SSE), xUnit + Akka.TestKit, vanilla JS/HTML/CSS in `wwwroot/index.html`.

## Global Constraints

- Target framework **.NET 10**; follow vertical-slice layout and ASP.NET-style namespaces.
- **No REST/OpenAPI change, no MQTT projection, no disk persistence** — dashboard-only, over the existing `/api/events` SSE stream.
- Ring buffer sizes are **constants**: **100 Activity + 200 Raw** (oldest evicted on overflow).
- Two verbosity modes: **Activity** (default) and **Raw**. Header controls are **Minimal**: mode toggle, pause, clear. Collapses to a one-line status bar.
- **No commissioning in-progress ticker/spinner** — that is a separate feature. This plan ships only the log.
- Commit messages: single-line subject only (no body), no `Co-Authored-By` trailer, never push.

---

### Task 1: Log message contract + `LogBufferActor`

**Files:**
- Create: `src/Matterhorn/Bridge/LogMessages.cs`
- Create: `src/Matterhorn/Bridge/LogBufferActor.cs`
- Test: `src/Matterhorn.Test/Bridge/LogBufferActorTests.cs`

**Interfaces:**
- Consumes: nothing (foundation task).
- Produces:
  - `LogEntry(DateTimeOffset Ts, LogCategory Category, string Kind, string Message, string? Device, LogLevel Level)` — published on `Context.System.EventStream`.
  - `enum LogCategory { Activity, Raw }`, `enum LogLevel { Info, Ok, Warn }`
  - `GetLogSnapshot` (Ask request), `LogSnapshot(IReadOnlyList<LogEntry> Activity, IReadOnlyList<LogEntry> Raw)` (reply).
  - `LogBufferActor` with `static Props Props()`.

- [ ] **Step 1: Write `LogMessages.cs`**

```csharp
namespace Matterhorn.Bridge;

/// <summary>A single dashboard log line. Published on the actor-system EventStream by the
/// gateway; retained (bounded) by <see cref="LogBufferActor"/> and forwarded to SSE.</summary>
public record LogEntry(
    DateTimeOffset Ts,
    LogCategory Category,   // Activity = human milestone, Raw = wire event
    string Kind,            // slug: "commission", "joined", "attribute_updated", ...
    string Message,         // fully-rendered line text
    string? Device,         // friendly name when known
    LogLevel Level);        // drives colour: Info | Ok | Warn

public enum LogCategory { Activity, Raw }
public enum LogLevel { Info, Ok, Warn }

/// <summary>Ask the <see cref="LogBufferActor"/> for its retained buffer (replayed on SSE connect).</summary>
public record GetLogSnapshot;
public record LogSnapshot(IReadOnlyList<LogEntry> Activity, IReadOnlyList<LogEntry> Raw);
```

- [ ] **Step 2: Write the failing test for eviction + snapshot**

```csharp
using Akka.Actor;
using Akka.TestKit.Xunit2;
using Matterhorn.Bridge;

namespace Matterhorn.Test.Bridge;

public class LogBufferActorTests : TestKit
{
    private static LogEntry Entry(LogCategory cat, int n) =>
        new(DateTimeOffset.UnixEpoch.AddSeconds(n), cat, "k", $"msg{n}", null, LogLevel.Info);

    [Fact]
    public void Snapshot_returns_activity_and_raw_in_order()
    {
        var buf = Sys.ActorOf(LogBufferActor.Props());
        Sys.EventStream.Publish(Entry(LogCategory.Activity, 1));
        Sys.EventStream.Publish(Entry(LogCategory.Raw, 2));
        Sys.EventStream.Publish(Entry(LogCategory.Activity, 3));

        AwaitAssert(() =>
        {
            var snap = buf.Ask<LogSnapshot>(new GetLogSnapshot()).Result;
            Assert.Equal(new[] { "msg1", "msg3" }, snap.Activity.Select(e => e.Message));
            Assert.Equal(new[] { "msg2" }, snap.Raw.Select(e => e.Message));
        });
    }

    [Fact]
    public void Raw_buffer_evicts_oldest_beyond_cap()
    {
        var buf = Sys.ActorOf(LogBufferActor.Props());
        for (var i = 0; i < 205; i++) Sys.EventStream.Publish(Entry(LogCategory.Raw, i));

        AwaitAssert(() =>
        {
            var snap = buf.Ask<LogSnapshot>(new GetLogSnapshot()).Result;
            Assert.Equal(200, snap.Raw.Count);
            Assert.Equal("msg5", snap.Raw[0].Message);   // 0..4 evicted
            Assert.Equal("msg204", snap.Raw[^1].Message);
        });
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~LogBufferActorTests"`
Expected: FAIL — `LogBufferActor` does not exist (compile error).

- [ ] **Step 4: Write `LogBufferActor.cs`**

```csharp
using Akka.Actor;

namespace Matterhorn.Bridge;

/// <summary>
/// Singleton ring buffer of recent <see cref="LogEntry"/> lines. Subscribes to the EventStream
/// and keeps the last N per category so a browser that connects (or reloads) can replay recent
/// history before going live. Survives page reload, not app restart.
/// </summary>
public sealed class LogBufferActor : ReceiveActor
{
    private const int ActivityCap = 100;
    private const int RawCap = 200;
    private readonly Queue<LogEntry> _activity = new();
    private readonly Queue<LogEntry> _raw = new();

    public static Props Props() => Akka.Actor.Props.Create(() => new LogBufferActor());

    public LogBufferActor()
    {
        Receive<LogEntry>(e =>
        {
            var (q, cap) = e.Category == LogCategory.Activity ? (_activity, ActivityCap) : (_raw, RawCap);
            q.Enqueue(e);
            while (q.Count > cap) q.Dequeue();
        });
        Receive<GetLogSnapshot>(_ =>
            Sender.Tell(new LogSnapshot(_activity.ToList(), _raw.ToList())));
    }

    protected override void PreStart() => Context.System.EventStream.Subscribe(Self, typeof(LogEntry));
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~LogBufferActorTests"`
Expected: PASS (both tests).

- [ ] **Step 6: Commit**

```bash
git add src/Matterhorn/Bridge/LogMessages.cs src/Matterhorn/Bridge/LogBufferActor.cs src/Matterhorn.Test/Bridge/LogBufferActorTests.cs
git commit -m "feat: add LogEntry contract and LogBufferActor ring buffer"
```

---

### Task 2: Gateway emits `LogEntry` at milestones and wire events

**Files:**
- Modify: `src/Matterhorn/Bridge/MatterGatewayActor.cs`
- Test: `src/Matterhorn.Test/Bridge/MatterGatewayActorTests.cs`

**Interfaces:**
- Consumes: `LogEntry`, `LogCategory`, `LogLevel` (Task 1).
- Produces: `LogEntry` values on the EventStream at the points below. No new public method signatures.

Emission map (Activity = milestone, Raw = wire):

| Point in gateway | Category | Kind | Message | Level |
|---|---|---|---|---|
| `OnCommission` (request accepted) | Activity | `commission` | `setup code accepted` | Info |
| `OnCommission` (command sent) | Raw | `commission_with_code` | `→ sent` | Info |
| `OnCommissionDone` (error only) | Activity | `commission_failed` | `commissioning failed: {error}` | Warn |
| `OnNodeAdded` (top, always) | Raw | `node_added` | `node {id} · {product}` | Ok |
| `OnNodeAdded` (after register) | Activity | `joined` | `{name} joined · node {id}` | Ok |
| `AttributeChanged` handler | Raw | `attribute_updated` | `{node}/{ep}/{cluster}/{attr} = {value}` | Info |
| `OnReachabilityChanged` (top, always) | Raw | `node_updated` | `node {id} reachable={bool}` | Info |
| `OnReachabilityChanged` (on flip) | Activity | `online`/`offline` | `{name} online`/`{name} offline` | Ok / Warn |
| `OnNodeRemoved` (top) | Raw | `node_removed` | `node {id}` | Info |
| `OnNodeRemoved` (per endpoint) | Activity | `removed` | `{name} removed` | Info |
| `DoRename` (success) | Activity | `renamed` | `{from} → {slug}` | Info |

- [ ] **Step 1: Write the failing tests**

Add these to `MatterGatewayActorTests.cs` (the `Light(node)` helper and `using`s already exist in the file):

```csharp
[Fact]
public void NodeAdded_emits_activity_joined_and_raw_node_added()
{
    Sys.EventStream.Subscribe(TestActor, typeof(LogEntry));
    var gw = Sys.ActorOf(MatterGatewayActor.Props(
        new FakeMatterController(), new InMemoryMqttPublisher(), new MqttTopics("matterhorn")));

    gw.Tell(new NodeAdded(Light(5)));

    var entries = new List<LogEntry>();
    AwaitAssert(() =>
    {
        while (TryReceiveOne(out var m, TimeSpan.Zero) && m is LogEntry le) entries.Add(le);
        Assert.Contains(entries, e => e.Category == LogCategory.Raw && e.Kind == "node_added");
        Assert.Contains(entries, e => e.Category == LogCategory.Activity && e.Kind == "joined"
                                      && e.Level == LogLevel.Ok && e.Device == "bulb_5_1");
    });
}

[Fact]
public void CommissionRequest_emits_activity_commission_entry()
{
    Sys.EventStream.Subscribe(TestActor, typeof(LogEntry));
    var gw = Sys.ActorOf(MatterGatewayActor.Props(
        new FakeMatterController(), new InMemoryMqttPublisher(), new MqttTopics("matterhorn")));

    gw.Tell(new CommissionRequest("MT:ABC", "t1"));

    AwaitAssert(() =>
    {
        var seen = false;
        while (TryReceiveOne(out var m, TimeSpan.Zero))
            if (m is LogEntry { Category: LogCategory.Activity, Kind: "commission" }) seen = true;
        Assert.True(seen);
    });
}

[Fact]
public void AttributeChanged_emits_raw_line_with_path()
{
    Sys.EventStream.Subscribe(TestActor, typeof(LogEntry));
    var gw = Sys.ActorOf(MatterGatewayActor.Props(
        new FakeMatterController(), new InMemoryMqttPublisher(), new MqttTopics("matterhorn")));
    gw.Tell(new NodeAdded(Light(9)));

    var reading = new AttributeReading(9, 1, MatterClusters.OnOff, 0,
        System.Text.Json.JsonSerializer.SerializeToElement(false));
    gw.Tell(new AttributeChanged(reading));

    AwaitAssert(() =>
    {
        var found = false;
        while (TryReceiveOne(out var m, TimeSpan.Zero))
            if (m is LogEntry { Category: LogCategory.Raw, Kind: "attribute_updated" } le
                && le.Message.Contains("9/1/6/0")) found = true;
        Assert.True(found);
    });
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~MatterGatewayActorTests"`
Expected: FAIL on the three new tests — no `LogEntry` is published yet (assertions unmet).

- [ ] **Step 3: Add the `Log` helper**

In `MatterGatewayActor.cs`, add this private method next to `PublishEvent` (near the bottom of the class):

```csharp
private void Log(LogCategory category, string kind, string message,
                 string? device = null, LogLevel level = LogLevel.Info) =>
    Context.System.EventStream.Publish(new LogEntry(DateTimeOffset.Now, category, kind, message, device, level));
```

- [ ] **Step 4: Emit at each point**

`OnCommission` — add both lines before the existing `_controller.Commission(...)` call:

```csharp
private void OnCommission(CommissionRequest req)
{
    Log(LogCategory.Activity, "commission", "setup code accepted");
    Log(LogCategory.Raw, "commission_with_code", "→ sent");
    _controller.Commission(req.Code, CancellationToken.None).ContinueWith(t => t.IsFaulted
        ? new CommissionDone(req.Transaction, null, t.Exception!.GetBaseException().Message)
        : new CommissionDone(req.Transaction, t.Result, null)).PipeTo(Self);
}
```

`OnCommissionDone` — log a failure milestone before the MQTT publish:

```csharp
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
```

`OnNodeAdded` — add the Raw line at the very top (so it logs even on the duplicate-name early return), and the Activity line next to the existing `PublishEvent("device_joined", ...)`:

```csharp
private void OnNodeAdded(NodeAdded msg)
{
    var info = msg.Endpoint;
    Log(LogCategory.Raw, "node_added", $"node {info.NodeId} · {info.ProductName}", level: LogLevel.Ok);
    var name = _overrides.TryGetValue((info.NodeId, info.Endpoint), out var custom)
        ? custom
        : FriendlyName.Default(info.ProductName, info.NodeId, info.Endpoint);
    if (_byName.ContainsKey(name)) return;
    // ... existing registration unchanged ...
    PublishDevices();
    PublishEvent("device_joined", new { friendly_name = name });
    Log(LogCategory.Activity, "joined", $"{name} joined · node {info.NodeId}", device: name, level: LogLevel.Ok);
}
```

`AttributeChanged` handler — replace the inline lambda in the constructor with one that logs first:

```csharp
Receive<AttributeChanged>(ac =>
{
    var r = ac.Reading;
    Log(LogCategory.Raw, "attribute_updated",
        $"{r.NodeId}/{r.Endpoint}/{r.ClusterId}/{r.AttributeId} = {r.Value.GetRawText()}");
    if (_byKey.TryGetValue((r.NodeId, r.Endpoint), out var reg))
        reg.Actor.Tell(new ApplyAttribute(r));
});
```

`OnReachabilityChanged` — Raw at top always, Activity only after the flip guard:

```csharp
private void OnReachabilityChanged(ReachabilityChanged r)
{
    Log(LogCategory.Raw, "node_updated", $"node {r.NodeId} reachable={r.Reachable}");
    if (!_byKey.TryGetValue((r.NodeId, r.Endpoint), out var reg)) return;
    reg.Actor.Tell(new SetReachable(r.Reachable));
    if (reg.Descriptor.Reachable == r.Reachable) return;
    Log(LogCategory.Activity, r.Reachable ? "online" : "offline",
        $"{reg.FriendlyName} {(r.Reachable ? "online" : "offline")}",
        device: reg.FriendlyName, level: r.Reachable ? LogLevel.Ok : LogLevel.Warn);
    var updated = reg with { Descriptor = reg.Descriptor with { Reachable = r.Reachable } };
    _byKey[(r.NodeId, r.Endpoint)] = updated;
    _byName[reg.FriendlyName] = updated;
    PublishDevices();
}
```

`OnNodeRemoved` — Raw at top, Activity per endpoint next to the existing `PublishEvent("device_leave", ...)`:

```csharp
private void OnNodeRemoved(NodeRemoved msg)
{
    Log(LogCategory.Raw, "node_removed", $"node {msg.NodeId}");
    var keys = _byKey.Keys.Where(k => k.Item1 == msg.NodeId).ToList();
    if (keys.Count == 0) return;
    var prunedOverride = false;
    foreach (var key in keys)
    {
        if (!_byKey.Remove(key, out var reg)) continue;
        _byName.Remove(reg.FriendlyName);
        Context.Stop(reg.Actor);
        prunedOverride |= _overrides.Remove(key);
        PublishEvent("device_leave", new { friendly_name = reg.FriendlyName });
        Log(LogCategory.Activity, "removed", $"{reg.FriendlyName} removed", device: reg.FriendlyName);
    }
    if (prunedOverride) SaveOverrides();
    PublishDevices();
}
```

`DoRename` — Activity line next to the existing `PublishEvent("device_renamed", ...)`:

```csharp
PublishDevices();
PublishEvent("device_renamed", new { from, to = slug });
Log(LogCategory.Activity, "renamed", $"{from} → {slug}", device: slug);
return new RenameResult(true, null, slug);
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~MatterGatewayActorTests"`
Expected: PASS (new tests plus all pre-existing gateway tests).

- [ ] **Step 6: Commit**

```bash
git add src/Matterhorn/Bridge/MatterGatewayActor.cs src/Matterhorn.Test/Bridge/MatterGatewayActorTests.cs
git commit -m "feat: emit Station Log entries from the gateway"
```

---

### Task 3: SSE `log` frame + history replay on connect

**Files:**
- Modify: `src/Matterhorn/Api/ServerSentEvents.cs`
- Modify: `src/Matterhorn/Program.cs`
- Test: `src/Matterhorn.Test/Api/SseBridgeActorTests.cs`

**Interfaces:**
- Consumes: `LogEntry`, `GetLogSnapshot`, `LogSnapshot`, `LogBufferActor` (Tasks 1–2); `ActorRegistry` (Akka.Hosting DI).
- Produces: SSE frames of shape
  `{"type":"log","ts":"HH:mm:ss","category":"activity|raw","kind":"...","msg":"...","device":"...|null","level":"info|ok|warn"}`.

- [ ] **Step 1: Write the failing test**

Add to `SseBridgeActorTests.cs`:

```csharp
[Fact]
public async Task Forwards_log_entry_as_sse_json()
{
    var ch = Channel.CreateUnbounded<string>();
    var actor = Sys.ActorOf(SseBridgeActor.Props(ch.Writer));

    var ts = new DateTimeOffset(2026, 7, 5, 14, 3, 47, TimeSpan.Zero);
    actor.Tell(new LogEntry(ts, LogCategory.Raw, "attribute_updated", "9/1/6/0 = false", null, LogLevel.Info));

    var msg = await ch.Reader.ReadAsync();
    Assert.Equal(
        """{"type":"log","ts":"14:03:47","category":"raw","kind":"attribute_updated","msg":"9/1/6/0 = false","device":null,"level":"info"}""",
        msg);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~SseBridgeActorTests"`
Expected: FAIL — `SseBridgeActor` does not handle `LogEntry` (no frame produced; read blocks/throws).

- [ ] **Step 3: Add the frame formatter and handler**

In `ServerSentEvents.cs`, add a `using Matterhorn.Bridge;` if not present. Add a shared formatter and a `LogEntry` handler in `SseBridgeActor`:

```csharp
public sealed class SseBridgeActor : ReceiveActor
{
    public static Props Props(ChannelWriter<string> writer) =>
        Akka.Actor.Props.Create(() => new SseBridgeActor(writer));

    public SseBridgeActor(ChannelWriter<string> writer)
    {
        Receive<DeviceStateChanged>(e => writer.TryWrite(
            $"{{\"type\":\"state\",\"device\":{JsonSerializer.Serialize(e.FriendlyName)},\"state\":{e.StateJson}}}"));
        Receive<DeviceListChanged>(_ => writer.TryWrite("{\"type\":\"devices\"}"));
        Receive<LogEntry>(e => writer.TryWrite(LogFrame(e)));
    }

    /// <summary>Renders one log line as the dashboard SSE frame. Shared by live (this actor) and
    /// the snapshot replay in <see cref="ServerSentEvents"/>.</summary>
    public static string LogFrame(LogEntry e) =>
        $"{{\"type\":\"log\",\"ts\":\"{e.Ts:HH:mm:ss}\"," +
        $"\"category\":\"{(e.Category == LogCategory.Activity ? "activity" : "raw")}\"," +
        $"\"kind\":{JsonSerializer.Serialize(e.Kind)}," +
        $"\"msg\":{JsonSerializer.Serialize(e.Message)}," +
        $"\"device\":{JsonSerializer.Serialize(e.Device)}," +
        $"\"level\":\"{e.Level.ToString().ToLowerInvariant()}\"}}";
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~SseBridgeActorTests"`
Expected: PASS (all three tests).

- [ ] **Step 5: Register `LogBufferActor` in `Program.cs`**

In the `WithActors` lambda, create and register the buffer next to the gateway:

```csharp
.WithActors((system, registry) =>
{
    var publisher = sp.GetRequiredService<IMqttPublisher>();
    var names = sp.GetRequiredService<INameStore>();
    var gw = system.ActorOf(MatterGatewayActor.Props(controller, publisher, topics, names), "gateway");
    registry.Register<MatterGatewayActor>(gw);
    var logBuffer = system.ActorOf(LogBufferActor.Props(), "logbuffer");
    registry.Register<LogBufferActor>(logBuffer);
}));
```

- [ ] **Step 6: Replay the snapshot then subscribe to live logs**

In `ServerSentEvents.MapDeviceEvents`, add `ActorRegistry registry` to the handler parameters and subscribe/replay. Add `using Akka.Hosting;` at the top of the file.

```csharp
app.MapGet("/api/events", async (HttpContext ctx, ActorSystem system, ActorRegistry registry) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers.Connection = "keep-alive";

    var channel = Channel.CreateUnbounded<string>();
    var bridge = system.ActorOf(SseBridgeActor.Props(channel.Writer));
    system.EventStream.Subscribe(bridge, typeof(DeviceStateChanged));
    system.EventStream.Subscribe(bridge, typeof(DeviceListChanged));
    system.EventStream.Subscribe(bridge, typeof(LogEntry));
    try
    {
        await ctx.Response.WriteAsync(": connected\n\n", ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

        // Replay recent log history so a reload / late connection isn't blank.
        var buffer = registry.Get<LogBufferActor>();
        var snap = await buffer.Ask<LogSnapshot>(new GetLogSnapshot(), TimeSpan.FromSeconds(2), ctx.RequestAborted);
        foreach (var e in snap.Activity.Concat(snap.Raw))
        {
            await ctx.Response.WriteAsync($"data: {SseBridgeActor.LogFrame(e)}\n\n", ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }

        await foreach (var msg in channel.Reader.ReadAllAsync(ctx.RequestAborted))
        {
            await ctx.Response.WriteAsync($"data: {msg}\n\n", ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }
    }
    catch (OperationCanceledException) { /* client disconnected */ }
    finally
    {
        system.EventStream.Unsubscribe(bridge);
        system.Stop(bridge);
    }
});
```

Note the accepted simplification: a log entry arriving between the snapshot read and the first live read could appear twice or (rarely) be missed — harmless for a best-effort log.

- [ ] **Step 7: Build and run the full suite**

Run: `dotnet test src/Matterhorn.Test`
Expected: PASS (whole suite). Confirms the `Program.cs` wiring compiles and nothing regressed.

- [ ] **Step 8: Commit**

```bash
git add src/Matterhorn/Api/ServerSentEvents.cs src/Matterhorn/Program.cs src/Matterhorn.Test/Api/SseBridgeActorTests.cs
git commit -m "feat: stream Station Log frames over SSE with history replay"
```

---

### Task 4: Dashboard dock UI

**Files:**
- Modify: `src/Matterhorn/wwwroot/index.html`

**Interfaces:**
- Consumes: SSE `log` frames (Task 3): `{type:"log", ts, category, kind, msg, device, level}`.
- Produces: no code interface (UI only). Verified manually.

- [ ] **Step 1: Add the dock styles**

Inside the `<style>` block in `index.html`, append (colours reuse the existing terminal-dark palette from the mockup):

```css
  /* Station Log dock */
  .dock { position:fixed; left:0; right:0; bottom:0; z-index:25; background:#0f151b; color:#c7d4de;
          font-family:var(--mono); border-top:1px solid #26313b; box-shadow:0 -6px 20px rgba(0,0,0,.28); }
  .dock .dhd { display:flex; align-items:center; justify-content:space-between; gap:10px; padding:6px 12px;
               background:#161f28; border-bottom:1px solid #26313b; cursor:pointer; }
  .dock .dtitle { color:#8797a5; letter-spacing:.13em; text-transform:uppercase; font-size:10px; font-weight:700; }
  .dock .dctrls { display:flex; align-items:center; gap:8px; }
  .dock .dctrls .last { color:#5b6b78; font-size:11px; max-width:38vw; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
  .dtoggle { display:flex; border:1px solid #26313b; border-radius:5px; overflow:hidden; font-size:10px; }
  .dtoggle span { padding:2px 9px; color:#8797a5; cursor:pointer; }
  .dtoggle span.on { background:var(--enzian); color:#fff; }
  .dbtn { font-size:10px; color:#8797a5; background:transparent; border:1px solid #26313b; border-radius:5px; padding:2px 8px; cursor:pointer; }
  .dbtn.active { color:#fff; border-color:var(--enzian); }
  .dock .dbody { height:180px; overflow-y:auto; padding:7px 12px; font-size:12px; line-height:1.7; }
  .dock.collapsed .dbody { display:none; }
  .dock .ln { white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
  .dock .ln .t { color:#5b6b78; }
  .dock .ln.info .k { color:#6fb0e8; }
  .dock .ln.ok   .k { color:#8fb974; }
  .dock .ln.warn .k { color:#d9724b; }
  main { padding-bottom:210px; }  /* keep the grid clear of the docked console */
```

- [ ] **Step 2: Add the dock markup**

Immediately before the closing `</body>` (after the `<div class="toast" ...>` if present, before `<script>`), add:

```html
<div class="dock collapsed" id="dock">
  <div class="dhd" id="dockHead">
    <span class="dtitle">▤ Station Log</span>
    <div class="dctrls" onclick="event.stopPropagation()">
      <span class="last" id="dockLast">waiting for events…</span>
      <span class="dtoggle" id="dockMode">
        <span class="on" data-mode="activity">Activity</span><span data-mode="raw">Raw</span>
      </span>
      <button class="dbtn" id="dockPause">pause</button>
      <button class="dbtn" id="dockClear">clear</button>
      <button class="dbtn" id="dockToggle">▲</button>
    </div>
  </div>
  <div class="dbody" id="dockBody"></div>
</div>
```

- [ ] **Step 3: Add the dock script**

Inside the existing `<script>` block, add this before `connect();` at the bottom, and extend the SSE handler:

```javascript
// ---- Station Log dock ----
const dock={el:document.getElementById('dock'),body:document.getElementById('dockBody'),
  last:document.getElementById('dockLast'),mode:'activity',paused:false,
  buf:{activity:[],raw:[]},cap:250};
function dockRender(){
  const rows=dock.buf[dock.mode];
  dock.body.innerHTML=rows.map(e=>
    `<div class="ln ${e.level}"><span class="t">${e.ts}</span> `+
    `<span class="k">${esc(e.kind)}</span> ${esc(e.msg)}`+
    `${e.device?` <span class="t">· ${esc(e.device)}</span>`:''}</div>`).join('');
  if(!dock.paused) dock.body.scrollTop=dock.body.scrollHeight;
}
function dockPush(e){
  const arr=dock.buf[e.category]; if(!arr) return;
  arr.push(e); if(arr.length>dock.cap) arr.shift();
  dock.last.textContent=`last: ${e.kind} · ${e.ts}`;
  if(e.category===dock.mode && !dock.paused) dockRender();
}
document.getElementById('dockMode').addEventListener('click',ev=>{
  const m=ev.target.dataset.mode; if(!m) return;
  dock.mode=m;
  [...ev.currentTarget.children].forEach(s=>s.classList.toggle('on',s.dataset.mode===m));
  dockRender();
});
document.getElementById('dockPause').addEventListener('click',ev=>{
  dock.paused=!dock.paused; ev.target.classList.toggle('active',dock.paused);
  ev.target.textContent=dock.paused?'resume':'pause'; if(!dock.paused) dockRender();
});
document.getElementById('dockClear').addEventListener('click',()=>{
  dock.buf[dock.mode]=[]; dockRender();
});
function dockToggleCollapse(){
  const c=dock.el.classList.toggle('collapsed');
  document.getElementById('dockToggle').textContent=c?'▲':'▼';
}
document.getElementById('dockHead').addEventListener('click',dockToggleCollapse);
```

Then extend the existing `es.onmessage` handler (currently handling `state` and `devices`) to route `log` frames:

```javascript
  es.onmessage=ev=>{
    let m; try{ m=JSON.parse(ev.data); }catch{ return; }
    if (m.type==='state'){ states.set(m.device,m.state); applyState(m.device); }
    else if (m.type==='devices'){ loadAll(); }
    else if (m.type==='log'){ dockPush(m); }
  };
```

- [ ] **Step 4: Verify manually with the fake controller**

Run the Docker dev stack (per project convention — do not background `dotnet run`):

```bash
docker compose up --build
```

Open `http://localhost:16090` and confirm:
- The dock sits at the bottom, collapsed, showing "Station Log" and a last-event summary.
- Clicking the header expands/collapses it (arrow flips ▲/▼).
- Demo device state changes appear as **Activity** lines; switching to **Raw** shows `attribute_updated` firehose lines with `node/ep/cluster/attr = value` paths.
- **pause** freezes auto-scroll and the view; **resume** catches up. **clear** empties the current mode.
- Reload the page mid-activity: recent lines reappear immediately (history replay).

- [ ] **Step 5: Commit**

```bash
git add src/Matterhorn/wwwroot/index.html
git commit -m "feat: add Station Log console dock to dashboard"
```

---

## Notes for the implementer

- `MatterClusters.OnOff` is `6`, so the attribute path in the Task 2 test renders `9/1/6/0`. Reuse `MatterClusters` constants rather than magic numbers where a test needs a cluster id.
- The gateway is the *only* emitter of `LogEntry`; do not publish log entries from endpoint actors or the MQTT/REST routers — keep the single-source invariant.
- Do not touch `contracts/matterhorn.openapi.yaml`, the MQTT topic tree, or add config keys — the log is dashboard-only by design.
