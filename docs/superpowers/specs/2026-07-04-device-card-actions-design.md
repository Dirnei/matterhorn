# Device Card Actions (Rename + Unpair) — Design

Date: 2026-07-04
Status: Approved (design), pending implementation plan

## Goal

Make per-device actions on the dashboard discoverable and coherent: replace the
hidden name-click rename with an explicit menu, and add the ability to un-pair
(decommission) a device. Bring the card header into one intentional layout rather
than bolting controls on.

## Background

- **Rename** already works end-to-end (`POST /api/devices/{name}/rename`), but the
  only UI affordance is clicking the device name — not discoverable.
- **Un-pair backend already exists.** `RemoveRequest` → `MatterGatewayActor.OnRemove`
  → `IMatterController.RemoveNode(nodeId)` → matterjs-server `remove_node` → the
  `node_removed` event → `OnNodeRemoved` drops the device from `_byName`/`_byKey`,
  stops its actor, prunes any persisted name override, and emits `device_leave` +
  republishes `bridge/devices` (which drives the SSE `devices` refresh). It is wired
  to MQTT (`bridge/request/remove`) but **not** to REST and **not** to the dashboard.
- The card header currently shows: name (click-to-rename), device type + node/ep
  coords, an optional transport pill, and a reachability "bench" indicator.

## Scope

In scope: a REST unpair endpoint, a per-card `⋮` action menu (Rename, Unpair),
inline rename-in-place, an unpair confirmation modal, and header/menu/modal styling
that fits the existing theme.

Out of scope: the matterjs-server fabric-label ("Matter Test") change — that is
Pi/matter.js configuration, not Matterhorn code, tracked separately. No unrelated
restyling beyond the device card and its new menu/modal.

## Design

### 1. Un-pair semantics

Un-pair means **decommission from the fabric**: `RemoveNode` removes the device from
matterjs-server's Matter fabric entirely. The bulb leaves the fabric and must be
re-commissioned with its physical setup code to return — irreversible from the
dashboard. This is the existing backend behavior; the UI must treat it as destructive.

### 2. Backend: unpair over REST

Removal is asynchronous: `RemoveNode` starts it, and the device only actually leaves
when the `node_removed` event arrives. So REST accepts and returns immediately, and
the device disappears from the list via the existing SSE `devices` refresh when
`node_removed` lands — no polling.

- **Contract:** add `DELETE /api/devices/{name}` (operationId `removeDevice`), responses
  **202** (decommission started) and **404** (no such device).
- **Gateway:** extend `OnRemove` to reply to `Sender` with a new
  `RemoveAccepted(bool Found)` record — `Found` is `_byName.ContainsKey(name)` checked
  synchronously before the async `RemoveNode` call. This mirrors how `OnRename` replies
  to `Sender` while also driving its async/MQTT side. The existing MQTT
  `bridge/response/remove` behavior is unchanged (still fires on `RemoveDone`); the new
  `Sender` reply is harmless on the MQTT path (Told with `NoSender` → dead letters),
  exactly like rename.
- **Controller override:** `RemoveDevice(name)` `Ask`s the gateway a `RemoveRequest`
  (with a generated transaction id, like `Commission`), maps `RemoveAccepted.Found` →
  `Accepted()` (202) or `NotFound()` (404).

`RemoveAccepted` is a new record in `Bridge/BridgeMessages.cs`. `OnRemove` keeps its
current async `RemoveNode` → `RemoveDone` → MQTT-response chain for the found case.

### 3. Dashboard: the ⋮ action menu

- A **`⋮` button** in each card header opens a small popover menu anchored to the
  button: **✎ Rename** and **⌫ Unpair…** (the unpair item styled destructive/red).
- Only one menu open at a time; clicking elsewhere or pressing Esc closes it. The menu
  is keyboard-reachable (focusable button, Enter/Space to open, arrow/Tab within).
- Menus and the modal are built from the existing vanilla-JS patterns in `index.html`
  (no framework, no new dependency); the grid is rebuilt from scratch on each `render()`,
  so any open menu/modal state is transient and does not need to survive a re-render.

### 4. Rename: inline edit-in-place

- Choosing **Rename** turns the name element into a text input pre-filled with the
  current name. **Enter** submits, **Esc** cancels, blur cancels.
- Submit calls the existing `POST /api/devices/{name}/rename` via the existing `api(...)`
  helper; the same status handling as today (409 "already taken", 400 "can't be used",
  other → "failed"). The SSE `devices` event refreshes the card to the slugified name.
- This replaces the current `prompt()`-based rename and the click-on-name handler.

### 5. Unpair: confirmation modal

- Choosing **Unpair…** opens a small modal that names the device and warns it will be
  removed from the fabric and must be re-paired with its setup code to return.
  Buttons: **Cancel** and **Unpair** (destructive).
- Confirm calls `DELETE /api/devices/{name}` via `api(...)`. On **202**: close the modal,
  toast (e.g. "Unpairing…"), and the card disappears when the `node_removed`-driven SSE
  `devices` refresh arrives. On **404**: toast "Device not found" (already gone) and refresh.
  Other/non-ok: toast "Unpair failed".
- Esc / Cancel / backdrop click dismisses without acting. Focus moves into the modal on
  open and the modal traps focus while open.

### 6. Visual coherence (frontend-design)

The header now carries name, coords, transport pill, reachability bench, and the new
`⋮`. During implementation, use the **frontend-design skill** to make the header, the
popover menu, and the confirmation modal read as one intentional system in the existing
Bavarian-blue theme (light/dark aware): consistent spacing and alignment in the header,
menu/modal surfaces and elevation, the destructive-action color, and visible focus
states. Scope is limited to the device card and its menu/modal — no unrelated restyling.

### 7. Error handling

- REST: unknown device → 404; the async decommission itself, if it fails downstream,
  surfaces on the existing MQTT `bridge/response/remove` as today (REST has already
  returned 202 by then — consistent with `commission`).
- Dashboard: all network calls go through `api(...)` (which handles 401) and are wrapped
  so a failure shows a toast rather than a broken card. A device removed out from under
  an open menu/modal (via MQTT or another client) simply disappears on the next SSE
  refresh; acting on an already-gone device yields a 404 toast.

## Testing

- `MatterhornControllerTests`: `DELETE /api/devices/{name}` → 202 when the gateway
  replies `RemoveAccepted(true)`, 404 when `RemoveAccepted(false)`, asserting a
  `RemoveRequest` with the right name reaches the gateway probe.
- `MatterGatewayActorTests`: `RemoveRequest` for a known device replies
  `RemoveAccepted(Found: true)` and invokes `RemoveNode` (via `FakeMatterController.Removed`);
  for an unknown device replies `RemoveAccepted(Found: false)` and does not invoke
  `RemoveNode`. (Existing `NodeRemoved_drops_every_endpoint_of_the_node` and the
  override-prune test still cover the post-`node_removed` cleanup.)
- Dashboard: manual end-to-end on the docker demo stack — inline rename a device;
  open the menu and unpair one bulb, confirm it leaves the list; then (real decommission)
  re-commission it with its setup code to confirm it returns.

## Files touched (anticipated)

- `contracts/matterhorn.openapi.yaml` — `DELETE /api/devices/{name}`.
- `src/Matterhorn/Api/MatterhornController.cs` — `RemoveDevice` override.
- `src/Matterhorn/Bridge/BridgeMessages.cs` — `RemoveAccepted`.
- `src/Matterhorn/Bridge/MatterGatewayActor.cs` — `OnRemove` replies `RemoveAccepted`.
- `src/Matterhorn/wwwroot/index.html` — `⋮` menu, inline rename, unpair modal, styling.
- Tests as listed above.
