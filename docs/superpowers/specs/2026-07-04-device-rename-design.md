# Device Rename — Design

Date: 2026-07-04
Status: Approved (design), pending implementation plan

## Goal

Let a user rename a device (bulb) from the dashboard. The name must be editable at
runtime, kept at capability parity between REST and MQTT, and must survive both a
service restart and a device re-join.

## Background — how names work today

- At join time, `MatterGatewayActor.OnNodeAdded` derives a name via
  `FriendlyName.Default(productName, nodeId, endpoint)` (e.g. `demo_bulb_5_1`).
- That string is the key in `_byName`, the MQTT topic segment
  (`<base>/<name>/set|get`, retained state at `<base>/<name>`, availability at
  `<base>/<name>/availability`), the SSE `device` field, and the REST `friendly_name`.
- Nothing overrides or persists the name. It is recomputed on every join, so any change
  would reset on reconnect.
- `MqttCommandRouter` already parses `bridge/request/rename` but has no handler
  (`// "rename" is parsed but has no gateway handler yet (known follow-up).`).

## Scope

In scope: runtime rename of an existing (currently-known) device, over REST and MQTT,
persisted to a JSON file, with a dashboard affordance.

Out of scope (explicitly deferred): a pluggable persistence subsystem
(Akka.Persistence / SQLite / SQL / MongoDB). Rename persists a tiny name-override map;
a general persistence layer is a separate project to be taken up only when a second
thing needs persisting.

## Design

### 1. Core operation — one place, both facades forward to it

The write-path is already symmetric: `MqttCommandRouter` and `MatterhornController` are
thin facades that `Tell`/`Ask` the gateway. Putting rename in the gateway makes REST and
MQTT parity automatic.

New gateway message: `RenameRequest(string FromName, string ToName, string Transaction)`.
New result type: `RenameResult(bool Ok, string? Error)` (error codes: `not_found`,
`invalid_name`, `name_taken`).

Handler in `MatterGatewayActor`:

1. **Validate** `ToName` by running it through `FriendlyName.Slug`. If it slugs to empty
   → `invalid_name`. (Slugging guarantees a legal MQTT topic segment: no `/`, `+`, `#`,
   or whitespace.)
2. **Look up** `FromName` in `_byName`. Missing → `not_found`.
3. **Collision**: if the slugged `ToName` already exists in `_byName` and is not the same
   registration → `name_taken`.
4. **No-op**: if slugged `ToName == FromName`, return `Ok` without side effects.
5. **Migrate in-memory model**: update the `Registered.Descriptor` with
   `FriendlyName = ToName`, re-key `_byName` (remove old key, add new), keep the same
   `Registered` for `_byKey`. Tell the endpoint actor to migrate its topics (§2).
6. **Persist** the override to `names.json` (§3).
7. **Republish** `bridge/devices` (retained) via `PublishDevices()`, emit a
   `device_renamed` bridge event `{ from, to }`, and return `RenameResult` to the caller.

Attribute routing and reachability go through `_byKey` (nodeId/endpoint), which rename
never touches — so live device traffic is unaffected during and after a rename.

### 2. Endpoint actor topic migration

`MatterEndpointActor._name` becomes mutable. Add `Receive<Rename>(Rename msg)`:

- Clear stale retained topics: publish empty-retained to `<base>/<old>` and
  `<base>/<old>/availability` so the broker does not keep serving the old name.
- Set `_name = msg.NewName`.
- Republish current `_state` as retained to the new `<base>/<new>` (if any state exists)
  and publish availability `online` to the new availability topic.

The actor's Akka path stays `ep-{nodeId}-{endpoint}` (not name-derived), so no restart is
needed. `Rename` is a new record in `Devices/DeviceMessages.cs`.

### 3. Persistence — `names.json`

A small abstraction so the file I/O is isolated and testable:

```csharp
public interface INameStore
{
    IReadOnlyDictionary<(ulong NodeId, ushort Endpoint), string> Load();
    void Save(IReadOnlyDictionary<(ulong NodeId, ushort Endpoint), string> names);
}
```

- Default implementation `JsonNameStore` reads/writes a single JSON object keyed
  `"<nodeId>_<endpoint>"` → custom name. Example:

  ```json
  { "5_1": "living_room_lamp", "9_1": "desk_bulb" }
  ```

- **Load at startup.** In `OnNodeAdded`, if an override exists for `(nodeId, endpoint)`,
  use it in place of `FriendlyName.Default(...)`. This makes renames survive both restarts
  and device re-joins.
- **Save after every successful rename.** The file is a handful of entries; a synchronous
  write inside the handler is acceptable (sub-millisecond, no need for the PipeTo pattern
  used for slow controller calls).
- **Config**: new key `Storage:NamesFile`, default `data/names.json`, added to
  `MatterhornConfig`. Consistent with existing config, it is settable via the
  `Storage__NamesFile` environment variable. `docker-compose.yml` gets a `/data` volume
  mount so the file persists across container recreation.

**Identity key: `(nodeId, endpoint)`.** Stable across restarts and re-joins of the same
commissioning. A factory-reset + re-commission yields a new nodeId, so the custom name
would not follow that device — accepted trade-off for simplicity and consistency with how
`_byKey` already identifies devices.

### 4. Facades

- **MQTT** (`MqttCommandRouter.RouteRequest`): implement the existing `case "rename"`.
  Read `{ from, to, transaction }` from the payload, `Tell` a `RenameRequest`. The gateway
  publishes the outcome to `<base>/bridge/response/rename`
  `{ transaction, status, error }`, mirroring the commission/remove handlers
  (`OnRenameDone`).
- **REST**: add operation `POST /devices/{name}/rename` with body `{ "to": "<newName>" }`
  to `contracts/matterhorn.openapi.yaml`; regenerate the contract; implement the override
  in `MatterhornController`. It `Ask`s the gateway for a `RenameResult` and maps:
  `Ok` → 200; `not_found` → 404; `name_taken` → 409; `invalid_name` → 400.

Both facades converge on the single `RenameRequest`, guaranteeing capability parity.

### 5. Dashboard

Add a rename affordance to each device card (inline edit or small dialog). On submit it
calls `POST /devices/{name}/rename`. The existing SSE `DeviceListChanged` →
device-list refresh reflects the new name; no bespoke state handling needed. Keep the JS
minimal and in keeping with the existing dashboard code.

### 6. Error handling

- Invalid/empty name, unknown source, and name collision are returned as structured
  results (never exceptions): REST status codes above, MQTT `status: "error"` with an
  `error` field.
- Persistence write failure is logged; the in-memory rename still succeeds for the current
  session (surface as a warning rather than failing the operation, so a read-only data dir
  degrades gracefully). Revisit if this proves confusing.

## Testing

- `FriendlyNameTests`: add slug/validation cases for rename input (empty, punctuation,
  collisions-after-slug).
- Gateway rename tests: success; `not_found`; `name_taken`; no-op same-name; and that
  `_byKey` attribute routing still works after a rename.
- `MqttCommandRouterTests`: a `bridge/request/rename` payload routes to `RenameRequest`
  with the right from/to.
- `INameStore` round-trip test; and a join test asserting an override is applied in
  `OnNodeAdded` instead of the default name.
- REST: a `MatterhornControllerTests` case for 200/404/409/400 mapping.

## Files touched (anticipated)

- `Bridge/BridgeMessages.cs` — `RenameRequest`, `RenameResult`.
- `Bridge/MatterGatewayActor.cs` — handler, override-aware join, `device_renamed`,
  `bridge/response/rename`.
- `Devices/DeviceMessages.cs` — `Rename`.
- `Devices/MatterEndpointActor.cs` — mutable `_name`, `Rename` handler.
- `Persistence/INameStore.cs` + `JsonNameStore.cs` (new small slice).
- `Configuration/MatterhornConfig.cs` — `NamesFile`.
- `Mqtt/MqttCommandRouter.cs` — `case "rename"`.
- `contracts/matterhorn.openapi.yaml` + `Api/MatterhornController.cs` — REST endpoint.
- Dashboard assets — rename control.
- `docker-compose.yml` — `/data` volume.
- Tests as listed above.
