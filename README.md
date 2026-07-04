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

## Configuration

| Env / key | Purpose | Default |
|---|---|---|
| `Controller__WsUrl` | Matter controller WebSocket URL | `ws://localhost:5580/ws` |
| `Controller__Kind` | `fake` \| `python-matter-server` \| `matterjs` | `python-matter-server` |
| `Mqtt__Host` / `Mqtt__Port` | broker | `localhost` / `1883` |
| `Mqtt__BaseTopic` | base topic | `matter2mqtt` |
| `Rest__Port` | REST port | `8090` |
| `Rest__ApiKey` | required for REST access when set (sent as `X-Api-Key`) | — (open if unset) |

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

