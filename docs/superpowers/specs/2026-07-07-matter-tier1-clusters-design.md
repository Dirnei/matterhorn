# Matter Tier 1 Clusters + Dashboard Controls — Design

**Date:** 2026-07-07
**Context:** `2026-07-07-matter-feature-gap-analysis.md` (Tier 1 items).
**Status:** Approved design. Implementation plan: `../plans/2026-07-07-matter-tier1-clusters.md`.

## Goal

Broaden Matterhorn's Matter device coverage to the full Tier 1 set — every cluster reachable through
the existing generic `device_command` / `attribute_updated` plumbing — and give each new category a
working web-dashboard control, not just a read-only card. Projected identically across MQTT, REST,
and the dashboard, per the "one model, three projections" rule.

## Scope

New categories: **Window Covering, Door Lock, Thermostat, Fan Control.** Light polish: **color xy,
level-transition modifier, on/off effects, identify.** Read-only sensors: **pressure, flow,
smoke/CO, air quality, CO2/PM2.5/PM10, electrical power/energy, battery-low.**

Out of scope (deferred): native Matter Groups/Scenes clusters, bindings, ACL, ICD, OTA, commissioning
modes, Matter *event* parsing (all Tier 2/3 per the gap analysis).

## Key decisions

### 1. Add a `write_attribute` seam (the one architectural addition)

Window Covering and Door Lock are command-driven (UpOrOpen/StopMotion, LockDoor/UnlockDoor) and need
nothing new. **Fan Control and Thermostat are attribute-write configured in Matter** — you set fan
speed / thermostat setpoint / mode by writing an attribute, not invoking a command. Matterhorn has no
attribute-write path today.

Therefore this plan adds a `write_attribute` seam mirroring the existing `InvokeCommand`:

- `IMatterController.WriteAttribute(ulong nodeId, ushort endpoint, uint clusterId, uint attributeId, object? value, CancellationToken ct)`
- `MatterServerController` implementation (WS command `write_attribute`, args
  `{node_id, attribute_path: "<ep>/<cluster>/<attribute>", value}`) — pure codec addition in
  `MatterServerProtocol`.
- `FakeMatterController` records writes (an `AttributeWrites` list, mirroring `Invocations`) for tests.

This is the single highest-leverage Tier 2 verb; pulling it forward is deliberate. Without it, fan +
thermostat would ship read-only.

### 2. Exposes stay flat; add an `enum` type

Z2M's real wire uses nested composite exposes (`cover`/`lock`/`climate`/`fan`). Matterhorn's
`ExposeEntry`, generated DTO, and dashboard are all flat. Keep flat. Extend `ExposeEntry` with:

- `Type: "enum"` and a `IReadOnlyList<string>? Values` for enumerated properties.

No composite/nested exposes. Each category is a set of flat properties (table below).

### 3. Write path: per-property handler table + value-based `state` dispatch

`CommandMapping.Map` is a hardcoded `switch`; it would bloat, and `state` now carries three meanings.
Refactor toward a per-property handler registry (like `PropertyMapping.Rules`). Disambiguate `state`
by **value**: `OPEN/CLOSE/STOP` → WindowCovering; `LOCK/UNLOCK` → DoorLock; `ON/OFF/TOGGLE` → OnOff.
No new device state is needed in the endpoint actor.

The mapper returns a heterogeneous ordered list of write actions — existing `CommandSpec` **and** a new
`AttributeWriteSpec(uint ClusterId, uint AttributeId, object? Value)`. The endpoint actor dispatches
each to `InvokeCommand` or `WriteAttribute`. `MatterEndpointActor.OnSet` changes from
`foreach cmd → InvokeCommand` to `foreach action → (command ? InvokeCommand : WriteAttribute)`.

### 4. Cover position inversion

Matter lift-% is `0 = fully open, 100 = fully closed`; Z2M `position` is `0 = closed, 100 = open`.
Invert on both read (`position = 100 - matter`) and write (`GoToLiftPercentage` gets `100 - position`),
so the surface stays Z2M-correct.

## Per-category mapping

Cluster ids added to `MatterClusters`. Read rows go in `PropertyMapping`; write actions in
`CommandMapping`; exposes in `ExposesBuilder`; device-type names in `MatterServerProtocol.DeviceTypeName`.

### Window Covering — 0x0102 (command-driven)

- **Read:** CurrentPositionLiftPercentage (attr 0x0008, 0–100) → `position` (inverted).
- **Write:** `state` OPEN → UpOrOpen (0x00); CLOSE → DownOrClose (0x01); STOP → StopMotion (0x02).
  `position` → GoToLiftPercentage (0x05, `liftPercentageValue = 100 - position`).
- **Exposes:** `state` enum [OPEN, CLOSE, STOP] (set); `position` numeric 0–100 (all).
- **Device types:** 0x0202 Window Covering.

### Door Lock — 0x0101 (command-driven)

- **Read:** LockState (attr 0x0000): 1 Locked → `state` LOCK; 2 Unlocked → `state` UNLOCK
  (0 NotFullyLocked → leave prior / UNLOCK).
- **Write:** `state` LOCK → LockDoor (0x00); UNLOCK → UnlockDoor (0x01).
- **Exposes:** `state` enum [LOCK, UNLOCK] (all).
- **Device types:** 0x000A Door Lock (already named).

### Thermostat — 0x0201 (attribute-write)

- **Read:** LocalTemperature (0x0000, int16, ÷100 °C) → `local_temperature`;
  OccupiedHeatingSetpoint (0x0012) → `occupied_heating_setpoint`;
  OccupiedCoolingSetpoint (0x0011) → `occupied_cooling_setpoint`;
  SystemMode (0x001C enum: 0 off, 1 auto, 3 cool, 4 heat) → `system_mode`.
- **Write (write_attribute):** `occupied_heating_setpoint`/`occupied_cooling_setpoint` → the matching
  attribute (×100); `system_mode` → SystemMode (enum encode).
- **Exposes:** `local_temperature` numeric °C (published); `occupied_heating_setpoint` /
  `occupied_cooling_setpoint` numeric °C (all); `system_mode` enum [off, auto, cool, heat] (all).
- **Device types:** 0x0301 Thermostat (already named).

### Fan Control — 0x0202 (attribute-write)

- **Read:** FanMode (0x0000 enum: 0 off, 1 low, 2 medium, 3 high, 4 on, 5 auto) → `fan_mode`;
  PercentCurrent (0x0006) → `percent`.
- **Write (write_attribute):** `fan_mode` → FanMode; `percent` → PercentSetting (0x0002).
- **Exposes:** `fan_mode` enum [off, low, medium, high, on, auto] (all); `percent` numeric 0–100 (all).
- **Device types:** 0x002B Fan (add to name map).

### Light polish — 0x0300 / 0x0008 / 0x0006 / 0x0003

- **Color xy:** read CurrentX (0x0300/0x0003), CurrentY (0x0004), ÷65536 → `color_x`/`color_y`
  (0–1). Write `color_x`+`color_y` → MoveToColor (0x07, `colorX`/`colorY` = value ×65536).
  Exposes: `color_x`, `color_y` numeric.
- **Level transition:** `transition` (seconds) is a *modifier*, not an expose — when present with
  `brightness`, set `transitionTime` (×10, 0.1 s units) on MoveToLevelWithOnOff.
- **On/Off effect:** `effect` OFF-effect → OffWithEffect (0x40). Exposes: `effect` enum. (Minimal;
  effect id/variant fixed.)
- **Identify:** `/set {"identify": <seconds>}` → Identify command (cluster 0x0003, cmd 0x00,
  `identifyTime`). Exposes: `identify` numeric (set-only).

### Read-only sensors

Pure `PropertyMapping` + `ExposesBuilder` rows (published access only):

| Property | Cluster / attr | Convert | Unit |
|---|---|---|---|
| `pressure` | PressureMeasurement 0x0403 / 0 | ÷10 | hPa |
| `flow` | FlowMeasurement 0x0404 / 0 | ÷10 | m³/h |
| `smoke` | SmokeCOAlarm 0x005C / SmokeState 0x0001 | != 0 | binary |
| `carbon_monoxide` | SmokeCOAlarm 0x005C / COState 0x0002 | != 0 | binary |
| `air_quality` | AirQuality 0x005B / 0 | enum→text | — |
| `co2` | CO2 Concentration 0x040D / MeasuredValue 0 | float | ppm |
| `pm25` | PM2.5 0x042A / 0 | float | µg/m³ |
| `pm10` | PM10 0x042D / 0 | float | µg/m³ |
| `power` | ElectricalPowerMeasurement 0x0090 / ActivePower 0x0008 | ÷1000 | W |
| `voltage` | 0x0090 / Voltage 0x0004 | ÷1000 | V |
| `current` | 0x0090 / ActiveCurrent 0x0005 | ÷1000 | A |
| `energy` | ElectricalEnergyMeasurement 0x0091 / CumulativeEnergyImported | ÷1e6 | kWh |
| `battery_low` | PowerSource 0x002F / BatChargeLevel 0x000E | != 0 (0 ok) | binary |

(Exact concentration-measurement cluster ids and scaling to be confirmed against a live device during
implementation; the mapping is table-local and cheap to adjust.)

## Dashboard

Vanilla-JS additions in `wwwroot/js`, driven by the exposes each device reports (the existing
pattern):

- **Cover:** open / stop / close buttons + a `position` slider.
- **Lock:** a LOCK/UNLOCK toggle.
- **Thermostat:** temperature readout + heating/cooling setpoint steppers + `system_mode` select.
- **Fan:** `fan_mode` select + `percent` slider.
- **Enum properties (generic):** any `enum` expose renders as a `<select>` posting `/set`.
- **New sensors:** read-only badges with units, reusing the existing numeric/binary display.

No automated dashboard tests (consistent with the repo).

## Testing

- `PropertyMapping` unit tests: one per new read row (reading → property/value), incl. cover-position
  inversion and enum decodes.
- `CommandMapping` unit tests: payload → ordered `CommandSpec`/`AttributeWriteSpec`, incl. `state`
  value-based dispatch (ON vs OPEN vs LOCK) and cover-position inversion on write.
- `ExposesBuilder` unit tests: one per category asserting the emitted exposes (types, values, access).
- `FakeMatterController`: assert `WriteAttribute` is recorded with the right path/value.
- `MatterEndpointActor` tests: a `/set` for a fan/thermostat produces a `WriteAttribute`; a cover/lock
  `/set` produces the right `InvokeCommand`.

## Files touched

- `Matter/MatterClusters.cs` — new cluster constants.
- `Matter/IMatterController.cs`, `Matter/MatterServerController.cs`, `Matter/FakeMatterController.cs`,
  `Matter/MatterServerProtocol.cs` — `WriteAttribute` seam + codec + device-type names.
- `Devices/PropertyMapping.cs` — read rows.
- `Devices/CommandMapping.cs` — refactor to handler table; command + attribute-write actions.
- `Devices/CommandSpec.cs` (or a sibling) — new `AttributeWriteSpec`.
- `Devices/MatterEndpointActor.cs` — dispatch command vs attribute-write in `OnSet`.
- `Bridge/DeviceDescriptor.cs` — `ExposeEntry` gains `Values` (+ enum).
- `Bridge/ExposesBuilder.cs` — per-category exposes.
- `wwwroot/js/*`, `wwwroot/index.html`, `wwwroot/app.css` — new controls.
- REST: no `contracts/matterhorn.openapi.yaml` change expected (exposes are already generic); confirm
  the `enum`/`Values` field is representable in the existing exposes DTO, extend the YAML if not.
- Tests under `src/Matterhorn.Test/**`.
