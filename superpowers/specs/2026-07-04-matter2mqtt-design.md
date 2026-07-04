# Matter2Mqtt — Design Spec

**Date:** 2026-07-04
**Status:** Approved for planning
**Repo:** new standalone repo (`matter2mqtt`), separate from `vidar`

---

## 1. Purpose

A single-purpose service that bridges Matter devices to MQTT — the Zigbee2MQTT of
Matter. It owns a Matter fabric, tracks device state, and exposes **two views of one
model**: a Z2M-shaped **MQTT** surface (event bus + retained state) and a thin
**REST** facade (synchronous request/response). It does nothing else — no dashboard,
no automation engine, no "platform." Consumers (Vidar, other dashboards) build on top.

### Guiding principle
Do one thing and expose it so others build on it. Matter2Mqtt makes Matter device
data and control **available**; it does not consume its own data.

---

## 2. Non-goals (anti-monolith guardrails)

- **No automation/rules engine, no UI, no historian.** Those are separate consumers.
- **No second product.** MQTT and REST are two projections of the same internal
  model. **Invariant: nothing exists in REST that isn't in MQTT, and vice versa.**
  They can never drift into two APIs.
- **No re-implementation of the Matter stack.** The Matter protocol (matter.js) is an
  off-the-shelf dependency behind a WebSocket, treated like the MQTT broker: run it,
  don't maintain it.
- **No Thread/BLE commissioning logic in this service.** Physical commissioning
  (BLE radio placement, Thread dataset) is upstream/operational. Matter2Mqtt only
  *triggers* commissioning by passing a setup code to the controller.

---

## 3. Architecture

```
┌───────────────────────────┐        ┌──────────────────────────┐   WS   ┌──────────────────────────────┐
│ SLZB-MR5U (192.168.0.233)  │        │  matterjs-server          │◄──────►│ Matter2Mqtt (.NET / Akka.NET) │──► MQTT
│  OTBR = Thread network      │◄Thread►│  Matter controller/fabric │        │  gateway + per-endpoint actors │──► REST + OpenAPI
│  + Thread dataset (OTBR API)│        │  (deploy on VM)           │        │  mapping / names / exposes     │
└───────────────────────────┘        └──────────────────────────┘        └──────────────────────────────┘
```

### Components
- **SLZB-MR5U OTBR** — existing. Thread Border Router; source of the Thread
  Operational Dataset (pulled from its OTBR REST API, handed to the controller once).
- **Matter controller** — `matterjs-server` (matter.js-based), deployed on the VM.
  Owns the fabric, commissions devices, maintains subscriptions, exposes a WebSocket
  control API. Chosen over python-matter-server because of `--ble-proxy`, which lets a
  BLE-less VM commission through a remote radio (e.g. a €5 ESP32) later.
- **Matter2Mqtt** — this project. Pure .NET/Akka.NET. Consumes the controller WS,
  models devices as actors, publishes MQTT, serves REST.

### The `IMatterController` seam
All controller I/O sits behind one interface so the upstream is swappable
(matterjs-server ↔ python-matter-server ↔ future). Capabilities:

- `ConnectAndListen()` → establishes WS, requests full node dump + event stream
- events: `NodeAdded`, `NodeUpdated`, `NodeRemoved`, `AttributeChanged`, `NodeReachabilityChanged`
- `InvokeCommand(nodeId, endpointId, clusterId, commandName, payload)`
- `ReadAttribute(nodeId, endpointId, clusterId, attributeId)`
- `Commission(setupCode)` → `(nodeId, interviewed endpoints)`
- `RemoveNode(nodeId)`

Concrete adapter maps these onto the controller's WS messages (e.g. python-matter-server's
`start_listening` / `device_command`; matterjs-server's equivalents).

---

## 4. MQTT contract

### Conventions
- **Base topic:** `matter2mqtt` (configurable).
- **Retain:** per-device state, `bridge/devices`, `bridge/state` retained. Commands not retained.
- **QoS 0** default.
- **LWT:** `matter2mqtt/bridge/state` = `{"state":"offline"}`.
- **Payloads:** JSON, semantic property names — consumers never see cluster IDs.

### Bridge / meta topics

| Topic | Dir | Payload |
|---|---|---|
| `matter2mqtt/bridge/state` | pub | `{"state":"online\|offline"}` (LWT) |
| `matter2mqtt/bridge/info` | pub | `{version, matterjs_version, fabric_id, commissioning_open, log_level}` |
| `matter2mqtt/bridge/devices` | pub | retained JSON array — discovery contract (§7) |
| `matter2mqtt/bridge/event` | pub | `{type, data}`: `device_joined`, `device_leave`, `device_interview`, `commission_started/completed/failed` |
| `matter2mqtt/bridge/request/<action>` | sub | management + commissioning (§6) |
| `matter2mqtt/bridge/response/<action>` | pub | result, correlated by `transaction` |

### Per-device topics

| Topic | Dir | Payload |
|---|---|---|
| `matter2mqtt/<friendly_name>` | pub | retained state, e.g. `{"state":"ON","brightness":254,"color_temp":370}` |
| `matter2mqtt/<friendly_name>/set` | sub | `{"state":"ON","brightness":128}` — keys → Matter commands |
| `matter2mqtt/<friendly_name>/set/<attr>` | sub | single-attr shortcut, e.g. `.../set/state` = `ON` |
| `matter2mqtt/<friendly_name>/get` | sub | `{"state":""}` forces a read |
| `matter2mqtt/<friendly_name>/availability` | pub | `online`/`offline` from subscription/reachability |

---

## 5. Device model

### Decision: endpoint = logical device (divergence from Z2M, deliberate)
A Matter *node* can host many endpoints (2-gang switch = 2; a bridge = 20+).
A `friendly_name` therefore maps to **one endpoint** (one controllable thing), not a
node. `bridge/devices` records the owning `node_id` per endpoint for grouping.
Rationale: flattening a multi-endpoint bridge into one topic is unusable.

### Friendly names
- Default: `<product_name>_<node_id>_<endpoint>`, slugified.
- Renameable via `bridge/request/rename`; mapping persisted (retained) and survives restart.

### Cluster → MQTT property mapping
Property names/ranges copy Z2M so consumer logic (and the Vidar plugin) transfers.

| Matter cluster (id) | Attribute | Property | Conversion |
|---|---|---|---|
| OnOff (0x0006) | OnOff | `state` | bool → `"ON"`/`"OFF"`; set → On/Off/Toggle |
| LevelControl (0x0008) | CurrentLevel | `brightness` | 0–254 (Z2M range); set → MoveToLevelWithOnOff |
| ColorControl (0x0300) | ColorTemperatureMireds | `color_temp` | mireds; set → MoveToColorTemperature |
| ColorControl (0x0300) | Hue/Sat or X/Y | `color` | `{"hue","saturation"}` or `{"x","y"}`; set → MoveToHueAndSaturation / MoveToColor |
| BooleanState (0x0045) | StateValue | `contact` | bool |
| OccupancySensing (0x0406) | Occupancy | `occupancy` | bool |
| TemperatureMeasurement (0x0402) | MeasuredValue | `temperature` | int16 0.01°C → ÷100 |
| RelativeHumidity (0x0405) | MeasuredValue | `humidity` | uint16 0.01% → ÷100 |
| IlluminanceMeasurement (0x0400) | MeasuredValue | `illuminance` | `lux = 10^((v-1)/10000)` |
| PowerSource (0x002F) | BatPercentRemaining | `battery` | half-percent → ÷2 |

The mapping is table-driven; a property is emitted only for a cluster/attribute
actually present, so output self-adapts to each device (Z2M `exposes` behavior).

---

## 6. Commissioning (Matter2Mqtt owns pairing — the "permit_join" analogue)

Matter pairs with a **setup code** (QR `MT:` string or 11-digit manual code); there is
no permit-join. The service exposes commissioning but does not implement the BLE/Thread
transport — that is the controller's job (matterjs-server + ble-proxy).

- **Request** `bridge/request/commission` → `{"code":"MT:Y.K90...","transaction":"abc"}`
- **Response** `bridge/response/commission` → `{"transaction":"abc","status":"ok","node_id":"1234...","error":null}`
- **Remove** `bridge/request/remove` → `{"id":"living_room_lamp","transaction":"..."}`
- **Rename** `bridge/request/rename` → `{"from":"...","to":"...","transaction":"..."}`
- Progress streamed on `bridge/event`: `commission_started` → `device_interview` → `device_joined`.

---

## 7. `bridge/devices` discovery contract

Retained array; each entry uses a Z2M-style `exposes` so any consumer can auto-build UI:

```json
{
  "friendly_name": "living_room_lamp",
  "node_id": "12345678901234567",
  "endpoint": 1,
  "vendor_name": "Nanoleaf", "product_name": "Essentials Bulb",
  "vendor_id": 4442, "product_id": 3,
  "device_type": "OnOffDimmableLight",
  "reachable": true,
  "exposes": [
    {"type":"binary","property":"state","access":7,"value_on":"ON","value_off":"OFF"},
    {"type":"numeric","property":"brightness","access":7,"value_min":0,"value_max":254},
    {"type":"numeric","property":"color_temp","access":7,"value_min":147,"value_max":500,"unit":"mired"}
  ]
}
```

`access` is a Z2M-style bitmask (1=published, 2=set, 4=get; 7 = all).

---

## 8. REST facade (option A — bundled, thin)

ASP.NET Core minimal API + generated OpenAPI. A **request/response view of the same
internal model** — reads and actions only, never a state store.

```
GET    /api/devices                → same shape as bridge/devices
GET    /api/devices/{name}         → definition + current state
GET    /api/devices/{name}/state   → current state only
POST   /api/devices/{name}/set     → {state, brightness, ...}   (sync ack)
POST   /api/devices/{name}/rename  → {to}
DELETE /api/devices/{name}         → decommission
POST   /api/commission             → {code} → SYNC {node_id, devices:[...]} | error   ★
GET    /api/bridge/info            → version, fabric, health
GET    /api/events                 → optional SSE stream mirroring bridge/event
```

- **Why REST earns its place:** synchronous commissioning/management (no MQTT
  transaction-id correlation), and reads for non-MQTT consumers (browser SPA, curl,
  a future native app).
- **Auth:** API key / bearer token (endpoints can commission and remove devices).
- Reads/actions are served from the live actor model (via `Ask`); REST and MQTT share
  the single source of truth.

---

## 9. Akka.NET actor model

Mirrors the Vidar bridge/device-actor pattern (e.g. Roborock).

- **`MatterGatewayActor`** — owns the `IMatterController` WS connection; requests the
  node dump + event stream; fans events out to endpoint actors; reconnects on drop
  (supervision); publishes `bridge/*`; handles `bridge/request/*` and REST actions
  that target the fabric (commission/remove).
- **`MatterEndpointActor`** (one per logical device) — holds endpoint state; maps
  clusters → properties; publishes retained `matter2mqtt/<name>` + `/availability`;
  translates `/set` and REST `set` into `InvokeCommand`. Per-device supervision =
  per-device fault isolation.
- **MQTT publisher** and **REST host** are projections that read actor state via `Ask`.
- Friendly-name/config store persists name mappings.

Plumbing/patterns may be lifted from `Vidar.Core` without coupling the repos.

### Data flow (Akka.Streams)

The controller→MQTT ingestion path is an **Akka.Streams** graph, not ad-hoc event
handling. Matter attribute streams are chatty and bursty (a dimming bulb spams
`CurrentLevel`; a reconnect replays a full node dump), so the pipeline needs
backpressure and conflation — Akka.Streams provides both, and it is already the
codebase idiom (`Zigbee2MqttBridgeActor` feeds a `ChannelSource.FromReader`).

```
WS receive loop  →  Channel<RawEvent>  →  ChannelSource
    → parse (WS message → typed AttributeChanged/NodeAdded/...)
    → map    (cluster/attribute → semantic property, §5 table)
    → GroupBy(nodeId+endpoint)         // per-device substreams; one chatty device
                                       // can't starve others
        → conflate (latest-wins)       // coalesce update bursts → publish newest only
        → throttle (optional, per-device rate cap)
    → mergeSubstreams
    → sink: Tell MatterEndpointActor  /  publish retained MQTT
```

Design notes:
- **Bounded buffers** everywhere (the `Channel` and stream stages) → predictable memory
  under WS bursts; overflow strategy = drop-oldest for state (latest wins), never for
  `bridge/event` (commissioning progress must not be lost — separate, non-conflated path).
- **Backpressure** propagates from the MQTT sink / actor mailboxes back to the WS reader,
  so a slow broker slows ingestion instead of ballooning memory.
- **Command path** (`/set`, REST `set`) is the mirror: a bounded `Channel<Command>` →
  `Source` → map to `InvokeCommand` → controller WS, matching the existing Z2M outbound
  pattern.
- The stream is materialized inside `MatterGatewayActor` (`Context.Materializer()`); on
  WS drop the stream is torn down and rebuilt on reconnect under actor supervision.

> **R3 considered and declined.** In an actor system already using Akka.Streams, R3
> would be a third data-movement paradigm overlapping actors + streams, and being
> push-based it lacks the Reactive-Streams backpressure this bursty WS source needs.
> Akka.Streams is the single streaming abstraction. Revisit only if a specific
> in-memory reactive need surfaces (e.g. SSE fan-out) that Akka.Streams/`EventStream`
> doesn't serve cleanly.

---

## 10. Configuration

| Key | Purpose | Default |
|---|---|---|
| `controller.wsUrl` | Matter controller WebSocket URL | — (required) |
| `controller.kind` | `matterjs` \| `python-matter-server` | `matterjs` |
| `mqtt.host` / `mqtt.port` / `mqtt.user` / `mqtt.password` | broker | — / 1883 |
| `mqtt.baseTopic` | base topic | `matter2mqtt` |
| `rest.enabled` / `rest.port` / `rest.apiKey` | REST facade | `true` / 8090 / — |
| `thread.dataset` | Thread Operational Dataset (from SLZB OTBR) passed to controller | — |

---

## 11. Development & test strategy

Build and test the entire brain with **zero hardware and zero BLE**:

- Run a **matter.js virtual example device** (fake OnOff/dimmable bulb) on the LAN.
- Commission it **over IP** (on-network commissioning — no Bluetooth needed for a
  software node) via the controller.
- Matter2Mqtt (actors, mapping, MQTT, REST) is developed against this fake device.
- Unit-test the pure mapping layer (cluster payloads → properties, and set-payloads →
  commands) with no controller, exactly like the Ecowitt/Roborock mapper tests.

Physical Thread commissioning (ESP32 ble-proxy + SLZB Thread dataset) is a separate,
later, operational step — it does not block building the service.

---

## 12. Roadmap / phasing

Each phase is its own spec → plan → build cycle.

- **Phase 1 — Matter2Mqtt core (this spec).** Controller WS → actors → MQTT (+ REST).
  Read + control, plus the commission *trigger* (functional over IP against the virtual
  device; functional for physical Thread devices once the upstream ble-proxy is set up —
  an operational step, not a code change). Delivers value standalone.
- **Phase 2 — Vidar Matter plugin.** A `PluginActorBase` clone of the Zigbee2Mqtt
  plugin that consumes the Matter2Mqtt MQTT topics. Small.
- **Phase 3 — Native commissioning app (parked).** iOS app (MatterSupport) for
  phone-BLE pairing onto the fabric. Only if the ESP32/ble-proxy path proves annoying;
  large effort (Apple Developer account, entitlements, distribution).

---

## 13. Open questions

- **matterjs-server WS API shape** — confirm exact message set for commission / events
  when wiring the concrete `IMatterController` adapter (may differ from
  python-matter-server's documented API).
- **Color model** — expose hue/sat, x/y, or both? Default to whatever the device's
  ColorControl reports; normalize later if needed.
- **Multi-fabric** — devices already paired to Apple/Google. Out of scope for Phase 1;
  Matter2Mqtt owns its own fabric.
```
