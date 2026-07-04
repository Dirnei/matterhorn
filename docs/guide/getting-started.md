# Getting started

## Quick look, no hardware

Boots Matterhorn with an in-memory fake controller and two seeded demo devices, plus an MQTT broker:

```bash
docker compose up --build
```

| What | Where |
|---|---|
| Web dashboard | `http://localhost:16090/` |
| Swagger UI | `http://localhost:16090/swagger` |
| REST API | `http://localhost:16090` (e.g. `GET /api/devices`) |
| EMQX dashboard | `http://localhost:16083` — login `admin` / `public` |
| MQTT broker | `localhost:16883`, base topic `matterhorn` |

## With real devices

**1. Run the Matter controller (matterjs-server) on your Linux host / Pi:**

```bash
# on the Pi (64-bit OS, IPv6 on):
docker compose -f docker-compose.matter-server.yml up -d
```

**2. Point Matterhorn at it.** Copy the example env and set the controller's address:

```bash
cp .env.example .env
# edit .env:
#   CONTROLLER_KIND=matterjs-server
#   CONTROLLER_WS_URL=ws://<pi-ip>:5580/ws
#   DEV_SEED=false
```

`.env` is gitignored, so your local setup never lands in the repo. Flip `CONTROLLER_KIND=fake` any
time to drop back to the demo stack.

**3. Start Matterhorn** (same command; it now uses your `.env`):

```bash
docker compose up --build
```
