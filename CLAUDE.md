# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Matterhorn (`matter2mqtt`) is a neutral bridge that exposes Matter devices as a
Zigbee2MQTT-shaped **MQTT** surface, a **REST** API, and a small **web dashboard** (SSE). It does
**not** speak Matter to devices directly — it drives an upstream `matterjs-server` /
`python-matter-server` over a WebSocket, which does the actual commissioning and Matter I/O.

MQTT and REST are two projections of one internal actor model: anything you can see or do on one,
you can on the other. Keep them in sync when changing behaviour.

## Commands

```bash
dotnet build                     # build; regenerates API controllers/DTOs from the OpenAPI contract (see below)
dotnet test src/Matterhorn.Test  # run the full test suite (xUnit + Akka.TestKit)

# Run one test by name:
dotnet test src/Matterhorn.Test --filter "FullyQualifiedName~MqttCommandRouterTests"
```

Run the app for real via Docker (per project convention — do not background `dotnet run`):

```bash
docker compose up --build        # fake controller + demo devices + EMQX broker; dashboard http://localhost:16090
```

Controller selection for the compose stack lives in a gitignored `.env` (copy `.env.example`):
`CONTROLLER_KIND=fake` (in-memory demo) or `matterjs-server` pointed at a real controller's WS URL.

For quick app iteration against a real controller without Docker:

```bash
Controller__Kind=matterjs-server Controller__WsUrl=ws://<pi-ip>:5580/ws \
  dotnet run --project src/Matterhorn      # REST + dashboard on http://localhost:5006
```

## Solution layout

`Matterhorn.slnx` at the root; `src/Matterhorn` (the app) + `src/Matterhorn.Test`. Code is organised
in **vertical slices by concern** (`Bridge/`, `Devices/`, `Matter/`, `Mqtt/`, `Api/`, `Persistence/`),
with ASP.NET-style namespaces — not layered/hexagonal folders. .NET 10.

## Architecture — the data flow

```
Matter device ──(Matter/IP)── matterjs-server ──(WebSocket)── Matterhorn ──┬── MQTT
                                                                           ├── REST
                                                                           └── Dashboard (SSE)
```

Everything routes through an **Akka.NET actor model**. Two actor types:

- **`MatterGatewayActor`** (`Bridge/`) — singleton. Owns the controller connection, device
  lifecycle, and the `bridge/*` control-plane (commission/remove/rename). Holds the discovery
  model (`_byKey` / `_byName`), spawns one endpoint actor per device, and answers REST queries.
- **`MatterEndpointActor`** (`Devices/`) — one per logical device (node + endpoint). Holds
  retained device state, maps Matter attribute changes → Z2M properties, and translates `/set` →
  controller commands.

**Inbound event path:** the controller's event stream is pumped through an **Akka.Streams**
`IngestionPipeline` (bounded queue, backpressure) into the gateway's mailbox. This ordering
guarantee matters: `NodeAdded` is always processed before the attributes that follow it, and all
endpoint lookup stays single-threaded on the gateway.

**Two write paths, one model:** `Api/MatterhornController` (REST) and `Mqtt/MqttCommandRouter`
(MQTT) are mirror images — both translate an inbound request into the *same* gateway message
(`SetDevice`, `CommissionRequest`, `RemoveRequest`, `RenameRequest`). MQTT `Tell`s with `NoSender`;
REST uses `Ask`. When adding a control operation, wire up **both** routers.

Long controller calls (commission ~30–60s, remove) run **off the actor** and pipe their outcome
back to the gateway as a self-addressed message (`CommissionDone`/`RemoveDone`) via `PipeTo(Self)`,
so the gateway never blocks while serving queries.

## Key seams

- **`IMatterController`** (`Matter/`) — the only place upstream Matter I/O happens. Two impls:
  `MatterServerController` (live WebSocket) and `FakeMatterController` (in-memory, for dev/tests).
  Selected in `Program.cs` by `Controller:Kind`. `MatterServerProtocol` is the pure WS-JSON ⇄
  domain codec; keep wire-format knowledge behind it.
- **`IMqttPublisher`** (`Mqtt/`) — `HiveMqttPublisher` (live) vs `InMemoryMqttPublisher` (tests).
  `MqttBridgeService` (hosted service) owns broker connect/subscribe so an unreachable broker never
  blocks host startup; the gateway (re)publishes retained state on `MqttConnected`.
- **`INameStore`** (`Persistence/`) — friendly-name overrides. `JsonNameStore` persists to
  `Storage:NamesFile`; a write failure degrades to in-memory-only, never fatal.

## Contract-first API — important

The REST API is **generated from `contracts/matterhorn.openapi.yaml`**, which is the single source
of truth. On every build, NSwag (`src/Matterhorn/nswag.json`) regenerates abstract controllers +
DTOs into `obj/generated/ApiContract.g.cs` (not committed). `Api/MatterhornController` implements
that generated base and is the **only** place domain records ⇄ wire DTOs are mapped.

To change the REST surface: **edit the YAML first**, then rebuild, then implement the new abstract
method. Do not hand-edit generated code. DTOs carry snake_case wire names via `[JsonPropertyName]`
so the REST shape matches the MQTT projection.

## MQTT topic conventions

Zigbee2MQTT-shaped, under a configurable base topic (default `matterhorn`). `MqttTopics` is the
single builder/parser for the tree: `<base>/<name>` (retained state), `<base>/<name>/set`,
`<base>/<name>/availability`, `<base>/bridge/{state,devices,event}`, `<base>/bridge/request/<action>`.

## Conventions

- Do not commit on your own in 1:1 session only when your are told to. While working on plans you can 
  commit on your own, No git description. No `Co-Authored-By` trailer on commits. Never `git push`.
- Releases are automated via release-please; `Version` in the csproj is stamped by CI — don't bump
  it by hand.
