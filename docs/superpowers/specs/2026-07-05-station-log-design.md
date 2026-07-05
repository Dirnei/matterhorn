# Station Log — design

## Problem

Commissioning a Matter device takes 30–60s, and during that window the dashboard shows
nothing — the commission form fires a POST, shows a `toast('Commissioning started')`, and then
the UI is silent until a `node_added` event triggers a grid refresh. Users experience this as
"a very long time nothing happens."

Investigation of the upstream matter-server confirmed **there are no commissioning-progress
events on the wire**. `commission_with_code` is a single blocking WebSocket RPC; the upstream
emits only a terminal burst (`node_added`, then `attribute_updated` ×N, maybe `node_updated`)
once the device has essentially already joined. The upstream event catalog is exactly nine types
(`node_added`, `node_updated`, `node_removed`, `node_event`, `attribute_updated`,
`server_info_updated`, `endpoint_added`, `endpoint_removed`, `server_shutdown`); Matterhorn
currently parses four of them.

Given that, the goal is not fake telemetry but **visibility**: surface the events that *do*
happen, in a live log window. (Filling the commissioning wait itself with an in-progress
indicator is a separate feature — see Out of scope.)

## What we're building

A collapsible **bottom console dock** ("Station Log") on the dashboard, showing a live event
stream with two verbosity modes:

- **Activity** (default) — human-readable milestones: commission accepted, device joined,
  online/offline, renamed, removed.
- **Raw** — the firehose: every Matter wire event, color-coded by type, with raw attribute
  paths (e.g. `9/1/6/0 = false`).

Header controls (Minimal surface): the **Activity/Raw toggle**, **pause**, **clear**. The dock
collapses to a one-line status bar showing the last event. Recent history replays on connect so
a page reload isn't blank.

Placement, mode contrast, and control surface were validated visually during brainstorming.

## Architecture

The feature reuses the existing Akka **EventStream → SSE** path that already drives
`DeviceStateChanged`/`DeviceListChanged`. No new I/O path, no REST/OpenAPI change, no MQTT
surface.

```
MatterEvent (ingestion) ─┐
                         ├─► MatterGatewayActor ──► publishes LogEntry ──► EventStream ──┬─► LogBufferActor (ring buffer)
control-plane milestones ┘                                                               └─► SseBridgeActor ─► /api/events ─► dashboard dock
```

### Domain record

One new record, published onto the EventStream:

```csharp
public sealed record LogEntry(
    DateTimeOffset Ts,
    LogCategory Category,   // Activity | Raw
    string Kind,            // slug: "commission", "joined", "attribute_updated", ...
    string Message,         // rendered line text
    string? Device,         // friendly name when known
    LogLevel Level);        // Info | Ok | Warn

public enum LogCategory { Activity, Raw }
public enum LogLevel { Info, Ok, Warn }
```

### Where entries are emitted

All emission is from `MatterGatewayActor`, keeping it the single source:

- **Activity entries** — at milestone points the gateway already handles:
  commission requested/accepted, `CommissionDone` (joined → `Ok`, failed → `Warn`), remove,
  rename, reachability flips (offline → `Warn`, online → `Ok`), first-seen device.
- **Raw entries** — one per `MatterEvent` as it is processed (`NodeAdded`, `AttributeChanged`,
  `ReachabilityChanged`, `NodeRemoved`), plus a marker when `commission_with_code` is sent.

### History — ring buffer

A singleton **`LogBufferActor`** subscribes to `LogEntry` on the EventStream and holds two
bounded queues: **last 100 Activity** + **last 200 Raw** (constants; oldest evicted on
overflow). Survives page reload, not app restart. On a new SSE connection the endpoint `Ask`s it
for the snapshot, streams that first, then the client goes live.

### Transport — SSE frame

`SseBridgeActor` also subscribes to `LogEntry` and forwards a new frame type over the existing
`/api/events` stream:

```json
{"type":"log","ts":"14:03:47","category":"raw","kind":"attribute_updated","msg":"9/1/6/0 = false","level":"info"}
```

### Frontend

Dock markup + styles added to `index.html`, matching the terminal-dark aesthetic validated in
the mockups (enzian/loden/alpenglow accents, mono type). The client:

- keeps two arrays (activity / raw) and renders the active mode;
- auto-scrolls unless **paused**; **clear** empties the view only (not the server buffer);
- hydrates from the replayed buffer on load.

## Error handling

- Buffer overflow drops oldest (bounded queue) — no growth.
- SSE write failure is already handled as client disconnect.
- Only known cases produce a `LogEntry`; malformed upstream data never reaches the log.
- Pause is purely client-side and cannot back-pressure the server.

## Testing

- **`LogBufferActor`** — bounded eviction and snapshot-on-ask (Akka.TestKit).
- **`MatterGatewayActor`** — commissioning / rename / remove / reachability each emit the
  expected `LogEntry` (extend `MatterGatewayActorTests`).
- **`SseBridgeActor`** — a `LogEntry` produces a well-formed `log` frame (extend
  `SseBridgeActorTests`).
- Frontend verified manually / visually.

## Out of scope (YAGNI)

- No per-device filter or text search.
- No disk persistence (container logs already cover durable history).
- No MQTT or REST projection — dashboard-only.
- No configurable buffer sizes (constants).
- No widening of the upstream parser to the five currently-ignored event types (can revisit if
  Raw mode proves it needs them).
- **No commissioning in-progress indicator** (the `…interviewing` ticker/spinner). That is a
  separate feature with its own spec; this PR ships only the log. The Station Log will still
  *show* the commission events as they arrive, which is the visibility win.
