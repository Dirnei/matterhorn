# Matterhorn

**A neutral bridge that puts your Matter devices on MQTT — the Zigbee2MQTT of Matter.**

Matterhorn commissions Matter devices (or joins ones already paired to Apple Home / Google via
Matter's multi-admin), and re-publishes them as a Zigbee2MQTT-shaped **MQTT** surface, a **REST**
API, and a small **web dashboard**. MQTT and REST are two projections of one internal model —
anything you can see or do on one, you can on the other.

## Why

Matter was supposed to end smart-home lock-in, but in practice a device ends up tied to whatever
app commissioned it — Apple Home, Google Home, Alexa — and getting it into your *own* automations
usually means routing through that vendor's hub or cloud.

Zigbee has [Zigbee2MQTT](https://www.zigbee2mqtt.io/): a neutral bridge that just exposes every
device on plain MQTT so you can wire it into anything. Matter didn't have an equivalent. Matterhorn
is that missing piece — a vendor-neutral bridge that speaks Matter on one side and MQTT/REST on the
other. Because Matter supports **multi-admin**, a bulb you already paired to Apple Home can be
*shared* to Matterhorn as well, so it lives in both worlds at once — no factory reset, no picking one
ecosystem.

## What you get

- **Commissioning** of Matter-over-IP devices (Wi-Fi, or Thread via a border router), including
  **multi-admin** sharing from an existing ecosystem.
- **MQTT**: retained per-device state, a `bridge/devices` discovery topic, and `/set` control —
  the Zigbee2MQTT topic shape.
- **REST**: an OpenAPI-described API (`GET/PATCH /api/devices/...`) with Swagger UI.
- **Web dashboard**: live device cards over Server-Sent Events, with controls generated from each
  device's capabilities — on/off, brightness, **color temperature**, an **RGB colour wheel**,
  sensor readouts (occupancy, temperature, humidity, battery), and a **Wi-Fi / Thread** transport
  badge.

## How it works

```
Matter device ──(Matter/IP)── python-matter-server ──(WebSocket)── Matterhorn ──┬── MQTT broker
                                                                                ├── REST API
                                                                                └── Web dashboard (SSE)
```

Matterhorn does **not** speak Matter to devices directly. It drives a
[python-matter-server](https://github.com/home-assistant-libs/python-matter-server) instance (the
same controller Home Assistant uses), which does the commissioning and low-level Matter work.

That controller must run on a **Linux host on your LAN with host networking** — Matter commissioning
needs mDNS + IPv6 link-local access to the device, which Docker Desktop on Windows/macOS can't
provide. A **Raspberry Pi** (64-bit OS, IPv6 enabled) is ideal. Matterhorn itself can run anywhere
that can reach that host.

## Running it

### Quick look, no hardware

Boots Matterhorn with an in-memory fake controller and two seeded demo devices, plus an MQTT broker:

```bash
docker compose up --build
```

| What | Where |
|---|---|
| Web dashboard | http://localhost:16090/ |
| Swagger UI | http://localhost:16090/swagger |
| REST API | http://localhost:16090 (e.g. `GET /api/devices`) |
| EMQX dashboard | http://localhost:16083 — login `admin` / `public` |
| MQTT broker | `localhost:16883`, base topic `matterhorn` |

### With real devices

**1. Run the Matter controller on your Linux host / Pi:**

```bash
# on the Pi (64-bit OS, IPv6 on):
docker compose -f docker-compose.matter-server.yml up -d
```

**2. Point Matterhorn at it.** Copy the example env and set the controller's address:

```bash
cp .env.example .env
# edit .env:
#   CONTROLLER_KIND=python-matter-server
#   CONTROLLER_WS_URL=ws://<pi-ip>:5580/ws
#   DEV_SEED=false
```

`.env` is gitignored, so your local setup never lands in the repo. Flip `CONTROLLER_KIND=fake` any
time to drop back to the demo stack.

**3. Start Matterhorn** (same command; it now uses your `.env`):

```bash
docker compose up --build
```

## Commissioning a device

Open the dashboard and use the **Commission** field:

- **A brand-new device** — enter its setup code (the `MT:…` QR string or the 11-digit manual code).
- **A device already in Apple Home** (multi-admin) — in the Home app, open the accessory →
  **Turn On Pairing Mode**, and enter the code it shows you. The device joins Matterhorn's fabric
  *in addition to* Apple Home; both control it independently.

The device appears on the dashboard once the controller finishes interviewing it (~30–60s).

## Controlling a device — MQTT or REST

Both surfaces are equivalent; the retained `matterhorn/<device>` topic updates either way.

```bash
# MQTT
mqttx pub -h localhost -p 16883 -t 'matterhorn/<device>/set' -m '{"state":"ON","brightness":180}'
mqttx pub -h localhost -p 16883 -t 'matterhorn/<device>/set' -m '{"hue":0,"saturation":254}'   # red

# REST — PATCH a partial state change, GET the current state
curl -X PATCH http://localhost:16090/api/devices/<device> \
  -H 'content-type: application/json' -d '{"state":"ON"}'
curl http://localhost:16090/api/devices/<device>
```

Colour is expressed the way Matter models it: `hue` and `saturation` (0–254) drive the colour
wheel, `color_temp` (mireds) is separate, and `brightness` is always its own axis.

## Configuration

Set via `.env` (Docker) or environment variables. The compose file maps the short names on the left
to the app's settings on the right.

| `.env` (Docker) | App env | Purpose | Default |
|---|---|---|---|
| `CONTROLLER_KIND` | `Controller__Kind` | `fake` or `python-matter-server` | `fake` (compose) |
| `CONTROLLER_WS_URL` | `Controller__WsUrl` | Matter controller WebSocket URL | `ws://localhost:5580/ws` |
| `DEV_SEED` | `DevSeed` | seed demo devices (fake controller only) | `true` (compose) |
| — | `Mqtt__Host` / `Mqtt__Port` | MQTT broker | `localhost` / `1883` |
| — | `Mqtt__BaseTopic` | base topic | `matterhorn` |
| — | `Rest__ApiKey` | if set, `/api/*` requires it via `X-Api-Key` | unset (open) |

The API key, when set, guards only `/api/*` — the dashboard has a field for it; Swagger and the raw
contract stay public.

## REST API (contract-first)

The API is generated from an authored OpenAPI document —
[`contracts/matterhorn.openapi.yaml`](contracts/matterhorn.openapi.yaml) is the source of truth. On
build, NSwag generates the ASP.NET controller base + DTOs; `Api/MatterhornController.cs` implements
them against the internal model. To change the API: edit the YAML, rebuild, implement any new
operations. Browse it live at `/swagger`.

## Development

```bash
dotnet build                                   # build (regenerates the API from the contract)
dotnet test src/Matterhorn.Test                # run the test suite
```

For iterating on the app without Docker you can run it directly against a controller:

```bash
Controller__Kind=python-matter-server Controller__WsUrl=ws://<pi-ip>:5580/ws \
  dotnet run --project src/Matterhorn        # REST + dashboard on http://localhost:5006
```
