# Matter2Mqtt

A standalone service that bridges Matter devices to MQTT — the
Zigbee2MQTT of Matter. It consumes a Matter controller over WebSocket and exposes Matter
devices as a **Z2M-shaped MQTT surface** (event bus + retained state) plus a thin **REST**
facade. MQTT and REST are two projections of one internal actor model — nothing lives in one
that isn't in the other.

## Run locally against the in-memory (fake) controller

Boots with no Matter hardware and no BLE. An MQTT broker is optional — if none is running,
publishes are logged and dropped, and the host still starts.

```bash
Controller__Kind=fake Mqtt__Host=localhost dotnet run --project src/Matter2Mqtt
# REST facade on http://localhost:8090
curl http://localhost:8090/api/bridge/info      # -> {"service":"matter2mqtt"}
```

## Test locally with Docker

Brings up **EMQX** + Matter2Mqtt (fake controller, demo devices seeded). Host ports are in the
16000+ range so they don't collide with defaults.

```bash
docker compose up --build
```

| What | Where |
|---|---|
| EMQX dashboard | http://localhost:16083 — login `admin` / `public` |
| REST facade | http://localhost:16090 (e.g. `GET /api/devices`) |
| MQTT broker | `localhost:16883`, base topic `matter2mqtt` |

Two demo devices are seeded: `essentials_bulb_1_1` (on/off + brightness + color_temp) and
`motion_sensor_2_1` (temperature/humidity/occupancy/battery, updated every ~10s).

Watch retained state in the EMQX dashboard (**Diagnose → WebSocket**, subscribe `matter2mqtt/#`),
or with a CLI. Control a device identically over **MQTT or REST**:

```bash
# via MQTT (publish to /set)
mqttx pub -h localhost -p 16883 -t 'matter2mqtt/essentials_bulb_1_1/set' -m '{"state":"OFF"}'
mqttx pub -h localhost -p 16883 -t 'matter2mqtt/essentials_bulb_1_1/set/brightness' -m '120'

# via REST (same effect)
curl -X POST http://localhost:16090/api/devices/essentials_bulb_1_1/set \
  -H 'content-type: application/json' -d '{"state":"ON"}'
```

The retained `matter2mqtt/essentials_bulb_1_1` topic updates in response either way.

## Configuration

| Env / key | Purpose | Default |
|---|---|---|
| `Controller__WsUrl` | Matter controller WebSocket URL | `ws://localhost:5580/ws` |
| `Controller__Kind` | `fake` \| `python-matter-server` \| `matterjs` | `python-matter-server` |
| `Mqtt__Host` / `Mqtt__Port` | broker | `localhost` / `1883` |
| `Mqtt__BaseTopic` | base topic | `matter2mqtt` |
| `Rest__Port` | REST port | `8090` |
| `Rest__ApiKey` | required for REST access when set (sent as `X-Api-Key`) | — (open if unset) |
| `DevSeed` | seed demo devices via the fake controller (dev/testing only) | `false` |

## Integration validation against a matter.js virtual device (manual, spec §11)

Validates the real WS adapter end-to-end with **zero hardware and zero BLE**:

1. Run a **matter.js virtual example device** (a fake OnOff/dimmable bulb) on the LAN.
2. Commission it **over IP** (on-network commissioning, no Bluetooth needed for a software
   node) via your Matter server (python-matter-server / matterjs-server).
3. Point Matter2Mqtt at that server:
   ```bash
   Controller__Kind=python-matter-server Controller__WsUrl=ws://<server>:5580/ws \
     Mqtt__Host=<broker> dotnet run --project src/Matter2Mqtt
   ```
4. Verify:
   - `matter2mqtt/bridge/devices` lists the bulb with `state` + `brightness` exposes;
   - publishing `{"state":"ON"}` to `matter2mqtt/<name>/set` turns the device on;
   - `GET /api/devices` (with `X-Api-Key` when configured) returns the same list.

## Status & known follow-ups

Phase 1: controller WS → actors → MQTT + REST, read + control over both surfaces, plus the
commission trigger. Documented follow-ups (spec §13):

- Live `node_added` / `commission_with_code` / `remove_node` wiring in
  `Matter/PythonMatterServerController.cs` (stubbed; the fake controller covers automated tests).
- `bridge/request/rename` handling (parsed but no gateway handler yet).
- Per-device stream conflation refinement in `Bridge/IngestionPipeline.cs`.

