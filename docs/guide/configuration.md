# Configuration

Set via `.env` (Docker) or environment variables. The compose file maps the short names on the left
to the app's settings on the right.

| `.env` (Docker) | App env | Purpose | Default |
|---|---|---|---|
| `CONTROLLER_KIND` | `Controller__Kind` | `fake` or `matterjs-server` | `fake` (compose) |
| `CONTROLLER_WS_URL` | `Controller__WsUrl` | Matter controller WebSocket URL | `ws://localhost:5580/ws` |
| `DEV_SEED` | `DevSeed` | seed demo devices (fake controller only) | `true` (compose) |
| — | `Mqtt__Host` / `Mqtt__Port` | MQTT broker | `localhost` / `1883` |
| — | `Mqtt__BaseTopic` | base topic | `matterhorn` |
| — | `Rest__ApiKey` | if set, `/api/*` requires it via `X-Api-Key` | unset (open) |

The API key, when set, guards only `/api/*` — the dashboard has a field for it; Swagger and the raw
contract stay public.
