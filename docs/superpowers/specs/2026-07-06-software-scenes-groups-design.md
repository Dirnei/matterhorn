# Software Scenes & Groups — design

## Problem

Matter has first-class **groups** (Groups cluster 0x0004 + Group Key Management 0x003F, delivered
as encrypted IPv6 groupcast) and **scenes** (Scenes Management cluster 0x0062, certifiable only as
of Matter 1.4.2). Both would let a user act on many devices at once.

Neither is reachable through our stack today. The upstream `matterjs-server` exposes **no
groups/scenes WebSocket command** — its surface is `start_listening` / `get_node` /
`read_attribute` / `write_attribute` / `device_command` / `commission_with_code` / `remove_node`.
Group/scene management is an open, unimplemented request upstream
([matterjs-server#635](https://github.com/matter-js/matterjs-server/issues/635)), and even the
manual path (`device_command` to the Groups cluster) is unicast — it would not give real
groupcast. Native scenes additionally sit on top of groups and on an optional device-side cluster
many consumer devices don't implement.

So native group/scene *commanding* is not feasible with the current upstream.

## What we're building

**Controller-side ("software") scenes and groups** — the same model Home Assistant and
Zigbee2MQTT use in practice:

- A **group** is a named set of devices with its own `/set` topic that **fans out** the requested
  state to each member (N unicast `SetDevice`s), plus a Z2M-style **optimistic echo** of the
  requested payload onto the group's retained state topic.
- A **scene** is a named **snapshot** of target property values across a set of devices. **Recall**
  fans those stored values back out. A scene is captured by snapshotting members' current state (the
  default) or by supplying explicit per-device values.

No new upstream dependency: everything is built from the existing `SetDevice` / endpoint-actor
fan-out. If `matterjs-server#635` ever lands real groupcast, the `IMatterController` seam is where a
native path would slot in without disturbing this model.

### Deliberate simplifications vs. native / Z2M

- **No real groupcast** — upstream can't do it; we fan out unicast. No visible simultaneity.
- **Groups are write + optimistic echo**, not aggregated state. After a set we echo the *requested*
  payload onto `matterhorn/<group>`; we do **not** compute an aggregate from members (dodges the
  "how do you average colors" problem). This matches Z2M's default optimistic behaviour.
- **Scenes are a Matterhorn concept**, global and controller-side — intentionally unlike Z2M's
  device/group-scoped `scene_store`/`scene_recall`.

## Decisions (locked during brainstorming)

1. **Scene capture** — snapshot current state by default; an explicit `state` object overrides.
2. **Membership identity** — persisted by stable `(nodeId, endpoint)` key (the same key
   `names.json` uses), so a rename never orphans membership and removal prunes cleanly. The
   MQTT/REST wire surface still speaks **friendly names**; stable-key is internal only.
3. **Groups** — write-only fan-out **plus** Z2M optimistic echo.
4. **Surfaces** — MQTT **and** REST **and** dashboard UI (a dashboard makes it hand-testable).
5. **Actors** — child-per-entity: one `GroupActor` per group, one `SceneActor` per scene, each
   under a supervisor. The gateway is **not** extended with scene/group state.

## Architecture

The gateway stays the **fabric root** (controller connection, device lifecycle, name↔key registry,
endpoint spawning) and gains only one new responsibility: emitting device-lifecycle events on the
`EventStream`. Scenes and groups are peers of the existing per-device `MatterEndpointActor`, not
more state inside the singleton.

```
MatterGatewayActor  ── fabric root (unchanged device duties)
   │  emits on EventStream: DeviceRegistered(key, friendlyName)  /  DeviceRemoved(key)
   ├── ep-{node}-{ep}   MatterEndpointActor        (existing child-per-entity)
   │
GroupsSupervisor ─── owns group store + name→entity routing; single writer
   └── group-{name}   GroupActor   — member keys, optimistic-echo state, fan-out
ScenesSupervisor ─── owns scene store + name→entity routing; single writer
   └── scene-{name}   SceneActor   — stored snapshot, recall + store behaviour
```

- **Fan-out routes by stable key through the gateway.** Entity actors hold member *keys* and tell
  the gateway `RouteSet(key, payload)` / `Ask` it `RouteGetState(key)`; the gateway looks the key up
  in its authoritative `_byKey` map and forwards to the endpoint actor. This reuses the one map that
  already owns endpoint refs, avoids coupling to actor-path/child naming, and is rename-safe (recall
  by key is correct even if a device was renamed between store and recall). The gateway never blocks:
  `RouteSet` is fire-and-forget, `RouteGetState` forwards.
- **Supervisors resolve names locally.** Each supervisor maintains a name↔key read-model from the
  gateway's `DeviceRegistered`/`DeviceRemoved` EventStream events, so it resolves incoming friendly
  names → stable keys (and renders bridge lists back to friendly names) without asking the gateway.
- **Supervisor = aggregate root / single writer.** Definition-changing commands (create, rename,
  delete, member add/remove, scene store) go **through** the supervisor: it updates the durable
  aggregate, persists, then informs the entity of its new definition. **Action** commands (group
  set, scene recall) go **straight to** the entity. Entities own behaviour + transient state (echo);
  the supervisor owns persisted truth.

### Responsibility split — why entities *and* supervisors

- A `GroupActor` earns its own actor: it holds live echo state, a member set, and fan-out
  behaviour.
- A `SceneActor` is more passive (a stored snapshot + a recall action), but per-entity was chosen
  deliberately for implementation simplicity and pattern consistency — each entity is
  self-contained and independently testable.
- The supervisors exist because the durable model needs a **single writer** per file and a
  **name→entity** front door; they are the "manager/proxy," the entities do the work.

## Data model

**Group** — name (slug), member set of stable keys, transient echo state (not persisted).
**Scene** — name (slug), stored map `stableKey → property payload`. Membership is implicit in the
map's keys.

Persistence reuses `JsonNameStore`'s flat `"{node}_{endpoint}"` key convention:

```jsonc
// groups.json
{ "living_room": { "members": ["5_1", "5_2"] } }

// scenes.json
{ "movie_night": {
    "5_1": { "state": "ON", "brightness": 40 },
    "5_2": { "state": "ON", "hue": 200, "saturation": 254 } } }
```

**Namespace rules**

- **Group names share the device namespace.** `matterhorn/<name>/set` must resolve to exactly one
  thing, so a group may not share a name with a device. Enforcement is **bidirectional**: group
  create/rename rejects a name that is a live device (`409`), and **device rename must also reject a
  name taken by a group** — today `DoRename` only checks `_byName` (devices), so it needs a
  cross-check against the group registry (the gateway can `Ask` `GroupsSupervisor`, or consult a
  shared name set). Commissioning's default name generation is collision-resistant already
  (`product-node-endpoint`) but the same guard applies if a generated name ever hits a group.
- **Scene names are their own namespace** (addressed only via `bridge/request/scene/*` /
  `/api/scenes/*`, never a bare topic), unique among scenes.
- Both are validated/slugified through the existing `FriendlyName.Slug`.

## MQTT surface (Z2M-shaped)

**Groups**

```
matterhorn/<group>/set                          fan out to members; echo requested payload to retained matterhorn/<group>
matterhorn/bridge/groups              (retained) list: name + member friendly_names   [peer of bridge/devices]
matterhorn/bridge/request/group/add             { friendly_name }
matterhorn/bridge/request/group/remove          { id }
matterhorn/bridge/request/group/rename          { from, to }
matterhorn/bridge/request/group/members/add     { group, device }
matterhorn/bridge/request/group/members/remove  { group, device }
matterhorn/bridge/response/group/<action>       { transaction, status, ... }
```

**Scenes**

```
matterhorn/bridge/scenes              (retained) list: name + members + target values
matterhorn/bridge/request/scene/store   { name, devices?: [...], state?: { device: {..} } }   // snapshot or explicit
matterhorn/bridge/request/scene/recall  { name }
matterhorn/bridge/request/scene/remove  { name }
matterhorn/bridge/request/scene/rename  { from, to }
matterhorn/bridge/response/scene/<action> { transaction, status, ... }
```

Recall is a fire-and-forget action — there is **no** scene state topic (scenes aren't stateful).

> Requires extending `MqttTopics.TryParseRequest` to accept **multi-segment actions**
> (`group/members/add`), which today it rejects (`!action.Contains('/')`). Small, contained change.

## REST surface (contract-first — edit `contracts/matterhorn.openapi.yaml` first)

Verb rule: **PUT** = create/replace a resource at a known URL (idempotent); **POST** = a
side-effecting action that isn't "put a resource here"; **PATCH** = partial state change; **DELETE**
= remove. Mirrors the existing device endpoints (device rename stays `POST …/rename`).

```
# Groups
GET    /api/groups                                list
PUT    /api/groups/{name}       {members?}         create-or-replace → 201/200 / 400 / 409 collides-with-device
DELETE /api/groups/{name}                          delete → 202/404
PATCH  /api/groups/{name}       SetRequest          fan out + echo → 202
PUT    /api/groups/{name}/members/{device}          add member (idempotent) → 200/201 / 404
DELETE /api/groups/{name}/members/{device}          remove member → 200/404
POST   /api/groups/{name}/rename    {to}            → 200/400/404/409

# Scenes
GET    /api/scenes                                list
PUT    /api/scenes/{name}       {devices?, state?}  store (create-or-replace snapshot) → 201/200
DELETE /api/scenes/{name}                          delete → 202/404
POST   /api/scenes/{name}/recall                    action → 202/404
POST   /api/scenes/{name}/rename    {to}            → 200/400/404/409
```

Reuses the existing `SetRequest` schema for group `PATCH`. New schemas: `Group`, `Scene`,
`GroupMembers` (PUT body), `StoreSceneRequest`. Group `PUT`'s only `409` is the device-name
collision (PUT is create-or-replace, so re-PUT of an existing group just replaces it).

## Data flow

**Group set** (`<group>/set` or `PATCH /api/groups/{name}`)
1. For MQTT `<group>/set` the gateway disambiguates (a set for a known group name is forwarded to
   `GroupsSupervisor` as `GroupSet`); REST `PATCH` addresses the supervisor directly. Either way →
   `GroupActor`.
2. `GroupActor` walks its member keys and tells the gateway `RouteSet(key, payload)` for each —
   fire-and-forget (an unknown key is a harmless no-op; stale members are already pruned).
3. Publishes optimistic echo: retained `matterhorn/<group>` = prior echo merged with the requested
   payload, plus a `DeviceStateChanged` event (the existing SSE `state` frame) for the group name.

**Scene recall** — identical fan-out; the payload per member comes from the stored map.
Fire-and-forget; `{transaction, status:"ok"}`.

**Scene store — snapshot** (the one async path): the `ScenesSupervisor` `Ask`s the gateway
`RouteGetState(key)` for each member **off-actor** (`Task.WhenAll` → `PipeTo(Self)`, the established
commission/remove pattern), keeps **only settable properties** (`state, brightness, color_temp, hue,
saturation` — so a sensor's read-only temperature never lands in a scene that recall couldn't
re-apply), builds the map, hands it to the `SceneActor`, persists, republishes `bridge/scenes`.

**Scene store — explicit** (`state:{...}` supplied): no `Ask`; map device names → keys and store
directly.

## Persistence & lifecycle pruning

- **`JsonGroupStore` / `JsonSceneStore`** copy `JsonNameStore`: flat JSON, write-to-temp +
  atomic `File.Move` swap, missing file = empty, write failure logged-not-fatal, configurable paths
  (`Storage:GroupsFile`, `Storage:ScenesFile`).
- Supervisors load their store on `PreStart` and spawn one entity actor per record.
- **Single writer.** All persistence goes through the supervisor; entities never touch disk.
- **Pruning.** The gateway emits `DeviceRemoved(key)` on the `EventStream` from its existing
  `OnNodeRemoved`. Supervisors subscribe; on removal they strip that key from every group's members
  and every scene's map, persist, inform affected entities, and republish the bridge lists.
  **Empty groups/scenes are kept** (created on purpose — matches Z2M).

## Dashboard UI

Same single `index.html`, same "station" visual language (kebab menus, modals, toasts, SSE); the
`frontend-design` skill guides the actual build during implementation.

- **Groups** — a "New group" action; each group renders as a station-style card with a master
  on/off switch that `PATCH`es the group (brightness/color deferred — see Out of scope), a member
  list with add/remove, and a kebab for rename/delete. The card's shown state is driven by the
  optimistic echo.
- **Scenes** — a "Capture scene" action (name + choose members, or snapshot all); each scene renders
  as a chip/card with a **Recall** button and a kebab (rename/delete).
- **SSE** gains `groups` and `scenes` list-changed frames (peers of the existing `devices` frame)
  and `state` echoes carrying group names; the client refetches its lists on a list-changed frame.

## Testing (Akka.TestKit + `FakeMatterController` + `InMemoryMqttPublisher`)

- **Entity actors** — `GroupActor` set → probe endpoints receive `ApplySet` + echo published;
  `SceneActor` recall → probes receive the stored payloads; snapshot-store → probes reply
  `GetState`, actor persists the expected settable-only map.
- **Stores** — round-trip + corrupt-file + missing-file (mirrors the `JsonNameStore` tests).
- **Supervisors** — create / rename / delete / device-name collision / prune-on-`DeviceRemoved`.
- **`MqttCommandRouter`** — new `group/*` and `scene/*` request topics route to the right messages;
  multi-segment action parsing.
- **REST** — the new endpoints' verbs + status codes.
- **Dashboard** — verified manually / visually against the fake controller and the live Govee bulb.

## Out of scope (YAGNI)

- **No real Matter groupcast / native scenes** — blocked upstream; revisit if
  `matterjs-server#635` lands.
- **No aggregated group state** — optimistic echo only; no averaging of member values.
- **No group brightness/color card** in v1 — master on/off first; richer group controls can follow
  once the surface is stable.
- **No scene state topic** — recall is an action, not a stateful entity.
- **No per-file-per-entity persistence** — one `groups.json` + one `scenes.json`, supervisor-owned.
- **No nested groups / groups-in-scenes** — scenes reference individual devices; groups and scenes
  are orthogonal.
