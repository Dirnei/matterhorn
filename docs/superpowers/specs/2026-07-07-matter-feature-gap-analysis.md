# Matter Feature Gap Analysis

**Date:** 2026-07-07
**Status:** Reference / analysis. Tier 1 is planned in `../plans/2026-07-07-matter-tier1-clusters.md`.

## How to read this

The controlling architectural fact: **matterjs-server's WebSocket API is generic, and Matterhorn
uses only a small slice of it.** The `IMatterController` seam (`Matter/IMatterController.cs`) exposes
just four verbs:

- `start_listening` → node snapshot + `attribute_updated` / `node_*` event stream
- `device_command` → invoke *any* cluster command on *any* endpoint
- `commission_with_code` (hardcoded `network_only=true`)
- `remove_node`

Because `device_command` invokes any command and `attribute_updated` streams any attribute, **most
device-feature gaps are implementable in Matterhorn alone** — new rows in five table-driven files
(`Matter/MatterClusters.cs`, `Devices/PropertyMapping.cs`, `Devices/CommandMapping.cs`,
`Bridge/ExposesBuilder.cs`, and the `DeviceTypeName` map in `Matter/MatterServerProtocol.cs`). Only a
handful of things genuinely need upstream (matterjs-server) work.

## What's implemented today (baseline)

Clusters mapped:

| Cluster | Read | Write |
|---|---|---|
| OnOff (0x0006) | `state` | on / off / toggle |
| LevelControl (0x0008) | `brightness` | MoveToLevelWithOnOff |
| ColorControl (0x0300) | `hue`, `saturation`, `color_temp`, `color_mode` | hue / sat / color_temp |
| BooleanState (0x0045) | `contact` | — |
| OccupancySensing (0x0406) | `occupancy` | — |
| TemperatureMeasurement (0x0402) | `temperature` | — |
| RelativeHumidityMeasurement (0x0405) | `humidity` | — |
| IlluminanceMeasurement (0x0400) | `illuminance` | — |
| PowerSource (0x002F) | `battery` | — |

Control plane: commission (on-network only), remove, rename. Plus software-side groups/scenes
(bridge-owned optimistic fan-out — *not* the Matter Groups/Scenes clusters).

Everything flows through generic `device_command` (write) and `attribute_updated` (read). There is
**no** attribute-write path, **no** Matter *event* parsing (only `attribute_updated`), and
commissioning is BLE-less / on-network only.

---

## Tier 1 — Implementable in Matterhorn alone, zero matterjs changes

New rows in the five mapping files above; they ride the existing `device_command` / `attribute_updated`
plumbing. Effort per item is small (typically <10 lines + a test).

| Feature | What it needs | Notes |
|---|---|---|
| **Color xy** (0x0300 attrs 3/4, cmd MoveToColor) | mapping rows | `color_mode` already reads `"xy"` but there is no xy property/command |
| **LevelControl transitions** | `transition` → `transitionTime` on MoveToLevel | pure payload addition |
| **On/Off effects** (OffWithEffect) | command mapping | Z2M-style `effect` |
| **Window Covering** (0x0102) | new cluster; Up/Down/Stop/GoToLiftPercentage; position read | maps to Z2M `cover` |
| **Thermostat** (0x0201) — read + command | read local temp / setpoints / mode; SetpointRaiseLower command | Z2M `climate`; setpoint *writes* are Tier 2 |
| **Door Lock** (0x0101) — state | read LockState; Lock/Unlock commands | lock *events* (jammed, pin used) are Tier 3 |
| **More sensors** | read-only rows: Pressure (0x0403), Flow (0x0404), Smoke/CO (0x005C), Air quality (0x005B), CO2/PM concentration | several device types already named |
| **Electrical / energy metering** (0x0090 / 0x0B04, or Matter 1.3 EnergyMeasurement 0x0091) | read-only rows | Z2M `power` / `energy` / `voltage` / `current` |
| **Battery detail** | add BatChargeLevel / BatChargeState alongside existing BatPercentRemaining | rows only |
| **Fan Control** (0x0202) | read/write fan mode + percent | mapping |
| **Identify** (0x0003) | Identify command | trivial exposes/command |

---

## Tier 2 — Implementable in Matterhorn, but needs one new controller verb (matterjs already supports it)

matterjs-server / python-matter-server already expose these WS commands; Matterhorn just doesn't call
them. Each needs a new `IMatterController` method + both impls (`MatterServerController` real,
`FakeMatterController` fake) + a new gateway message, wired into **both** REST and MQTT routers.

| Feature | Upstream WS command | Why it's needed |
|---|---|---|
| **Attribute writes** | `write_attribute` | Config is done by writing attributes, not commands: Thermostat setpoints, LevelControl `OnLevel`/`OnOffTransitionTime`, Occupancy timeouts, Color `StartUpColorTemperature`. Unlocks the config side of many Tier-1 clusters. |
| **Explicit attribute read** | `read_attribute` | On-demand refresh of a value not in the subscription cache. |
| **BLE + Thread/Wi-Fi onboarding** | `commission_on_network`, `set_wifi_credentials`, `set_thread_operational_dataset` | Today commissioning is `network_only=true` (device must already be on IP). A fresh BLE-only device needs these. A software controller may have no BLE radio — a host constraint, not just code. |
| **Open commissioning window** | `open_commissioning_window` | Share an already-joined device to another fabric/admin (multi-admin); returns a pairing code. |
| **Re-interview / ping / get node** | `interview_node`, `ping_node`, `get_node` | Diagnostics + recovering a device whose cluster list changed. |
| **OTA firmware** | `check_node_update` / `update_node` (verify names) | Surface available updates + trigger. |
| **Matter Events** (not attributes) | already in the `event` stream | Switch multipress/long-press (Switch 0x003B `MultiPressComplete`), door-lock operation events, boolean-state events. The receive loop only parses `attribute_updated`; add an `event`-type branch in `MatterServerProtocol.ParseIncoming`. **May need zero new verb** — just parse frames matterjs already sends. |

---

## Tier 3 — Genuinely needs matterjs-server extension (upstream work)

Not (fully) exposed by the matter-server WS API; blocked until upstream grows the surface.

| Feature | Why it's blocked | Confidence |
|---|---|---|
| **Native Matter groupcast** (Groups 0x0004 + Group Key Management 0x003F + multicast IPv6 send) | matter-server is controller/unicast-oriented; it doesn't expose group keyset provisioning or multicast send. **This is why groups are software fan-out today.** True Matter groups (one multicast frame, instant sync, works when the controller is offline) need upstream group-messaging support. | High — the marquee upstream gap |
| **Bindings** (0x001E) — direct device→device control | A switch driving a light without the hub. Not a first-class verb; *might* be forced via `write_attribute` on the Binding cluster, but fragile/unverified. | Medium |
| **Access Control (ACL) management** (0x001F) | Fine-grained multi-fabric ACL editing. Theoretically reachable via `write_attribute`, but no safe high-level verb. | Medium |
| **ICD management** (0x0046, Intermittently Connected Devices — sleepy Thread sensors) | Check-in / long-idle handling depends on matter.js support being surfaced through the WS API. | Low / uncertain |
| **Commissioning a from-scratch BLE device Matterhorn will own**, when the host has no BLE | Partly Tier 2 (verbs), partly a hardware/host constraint of the deployment. | Medium |

---

## Bottom line

- **~80% of "missing Matter features" are Tier 1** — more clusters/device types, done by extending
  the five mapping tables with no upstream changes. Highest ROI; start here.
- **Tier 2** unlocks the config and lifecycle surface (attribute writes, richer commissioning,
  diagnostics, OTA, Matter events) — matterjs already supports these; it is wiring work through the
  seam + dual routers.
- **Tier 3** is the real upstream list, and it is short: **native Matter groupcast is the standout**
  (software groups are the deliberate workaround), then bindings and ACL/ICD.

Two items to verify before committing to a Tier-2 plan (inferred from the python-matter-server API,
not read from matterjs-server here):

1. Exact OTA verb names (`check_node_update` / `update_node` vs matterjs equivalents).
2. Whether matterjs-server's event stream already emits Matter `event` frames (switch multipress
   etc.) — if so, that feature drops to "just parse it," no upstream needed.
