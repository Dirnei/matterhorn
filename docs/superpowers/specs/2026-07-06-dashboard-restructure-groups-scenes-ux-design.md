# Dashboard restructure + groups/scenes UX — design

## Problem

The dashboard is a single `wwwroot/index.html` that has grown to **1168 lines / 63 KB**. It now
carries device cards, groups, scenes, the capture modal, commissioning, and the station-log dock —
with **three near-duplicate copies** of the menu / inline-rename / delete-confirm logic (one each for
devices, groups, scenes). It is a god-file.

The groups/scenes UX added on top is weak:

- **Group creation is name-first** — you type a name into an empty box, create an empty group, then
  add members one at a time. Backwards.
- **The group card reads poorly** and doesn't scale.
- **The device picker doesn't scale** — a real fabric can reach 30 lights + 50 sensors; a flat
  checklist is unusable at that size.

This is a **frontend-only** restructure + UX pass. No REST/MQTT/actor changes.

## Decisions (locked during brainstorming)

1. **Single-page, tabbed IA** — one served `index.html` with a `Devices · Groups · Scenes` nav and
   hash routing; one persistent SSE connection and the log dock survive tab switches. Vanilla JS, no
   framework, no build step (the app serves static files from `wwwroot`).
2. **Group card = compact strip** — a single row (glyph · name · device count · master on/off ·
   kebab); member management expands inline. On/off only for now.
3. **Shared creation modal** for both "New group" and "Capture scene": name field + live search +
   a **Controllable / All** toggle (defaults to controllable-only) + a scrollable device checklist +
   an *N selected* counter.
4. **No emoji anywhere** — icons are CSS/SVG or text. Use the real Matterhorn theme
   (enzian/loden/alpenglow, station cards, mono coordinates); visual craft via the frontend-design
   skill.
5. **ES modules split by concern** — a thin shell `index.html` + `app.css` + focused JS modules,
   with the triplicated UI logic collapsed into one shared module.

## Architecture — single-page with hash routing

`index.html` is reduced to a **shell**: the header (wordmark, link/status, API-key, commission
control), a **top nav** (`Devices · Groups · Scenes`), a `<main id="view">` container, the
station-log dock, and a modal root. It contains **no view-specific markup or logic**.

```
index.html (shell)
  header · nav tabs · <main id="view"> · #log-dock · #modal-root
        │
   app.js  ── hash router: #/devices (default) | #/groups | #/scenes
        │      mounts the active view into #view; keeps the dock + SSE alive
        ├── views/devices.js   render + control device "station" cards
        ├── views/groups.js    render compact-strip group cards
        └── views/scenes.js    render scene cards
```

- **Routing:** `app.js` listens to `hashchange`, resolves the route to a view module, calls its
  `mount(container)` (and `unmount()` on leave). Unknown/empty hash → `#/devices`.
- **One SSE connection**, owned by `sse.js`, lives for the whole session. It dispatches frames to
  (a) the currently-mounted view and (b) the always-on log dock. Switching tabs never reconnects.
- **The station-log dock** is part of the shell, not any view — it persists across tabs exactly as
  today.

## Module layout (ES modules, no build)

```
wwwroot/
  index.html          shell only
  app.css             all styles (extracted from the inline <style>)
  iro.min.js          (unchanged — colour wheel for device cards)
  js/
    app.js            boot: reads API key, opens SSE, starts the router, mounts the dock
    api.js            fetch helpers + API-key header/query; typed calls per resource
    sse.js            EventSource lifecycle + a subscribe(type, handler) dispatcher
    ui.js             shared UI: toast, esc/cssId, kebabMenu(), confirmModal(),
                      inlineRename(), and the createPickerModal() (see below)
    store.js          in-memory device list + state cache, updated from SSE; exposes
                      helpers like isControllable(device) (has a settable expose)
    views/
      devices.js      mount/unmount; device station cards (today's control()/colorControl())
      groups.js       mount/unmount; compact-strip group cards + New-group action
      scenes.js       mount/unmount; scene cards + Capture-scene action
```

- Loaded via `<script type="module" src="js/app.js">`; modules `import` each other natively. Same
  origin, so no CORS/CSP issues (this is the app's own dashboard, not a sandboxed artifact).
- **`ui.js` dedupes the triplication.** `kebabMenu`, `confirmModal`, and `inlineRename` become
  entity-agnostic helpers taking `{ label, onRename, onDelete, … }`, replacing the separate
  `openMenu/openGroupMenu/openSceneMenu`, `startRename/startGroupRename/startSceneRename`, and
  `openUnpair/openGroupDelete/openSceneDelete` families that exist today.

## Group card — compact strip

Each group renders as one row inside the Groups view:

```
┌──────────────────────────────────────────────────┐
│ [◧]  living_room                    (on/off)   ⋮   │
│      ceiling · lamp · strip                        │
└──────────────────────────────────────────────────┘
```

- **Glyph** (CSS/SVG, not emoji), **name**, **device-count subline** (member friendly names, mono).
- **Master switch** on the right → `PATCH /api/groups/{name}` `{state:ON|OFF}`; its shown state
  tracks the group's SSE `state` echo.
- **Kebab** → rename (`POST …/rename`) / delete (`DELETE`) via the shared `ui.js` helpers.
- Clicking the row **expands** member management: the member list with per-member remove and an
  "add device" affordance (which opens the shared picker filtered to non-members).
- On/off master control only. Brightness/colour group control is explicitly out of scope here.

## Shared creation / picker modal

One reusable `createPickerModal(opts)` in `ui.js`, used by New-group and Capture-scene:

```
opts = { title, submitLabel, note?, initialName?, onSubmit(name, deviceKeys[]) }
```

Layout:
- **Name** input (top) — pre-filled with a suggestion (`group_N` / `scene_N`), editable.
- **Search** input — live case-insensitive substring filter on device friendly name (CSS/SVG search
  icon).
- **Controllable / All** segmented toggle — **defaults to Controllable**: only devices where
  `store.isControllable(d)` is true (any expose with the Z2M set bit, `access & 2` — lights, plugs).
  "All" reveals every device (sensors included). This is what makes 80 devices browsable.
- **Scrollable checklist** — one row per matching device: checkbox, friendly name, type tag, live
  state dot; a bottom fade signals overflow.
- **Footer** — an *N selected* counter + Cancel / submit button.
- **Scene variant** passes `note: "current state of the selected devices will be captured"`.

`onSubmit` calls the existing REST: group → `PUT /api/groups/{name}` `{members:[…]}`; scene →
`PUT /api/scenes/{name}` `{devices:[…]}`. **No new endpoints.** "Controllable" is derived entirely
client-side from `exposes`, so there is **no backend change of any kind** in this work.

## Data flow

- `store.js` holds the device list + latest per-device state, seeded from `GET /api/devices` +
  `GET /api/devices/{name}` and kept live by SSE `state`/`devices` frames. Views and the picker read
  from it rather than re-fetching.
- `sse.js` exposes `on(type, handler)`; `app.js` wires `devices`/`state` → store + active view,
  `groups`/`scenes` → active view refetch, `log` → dock. Group `state` echoes (device = group name)
  update the group strip.
- Escaping: every user/device/group/scene string rendered into the DOM goes through `esc()` (or
  `textContent` for modal titles), preserving today's XSS safety.

## Migration & sequencing

Two-phase so each step is independently reviewable:

1. **Extract to modules, behaviour-preserving.** Move the inline `<style>` to `app.css`; carve the
   inline `<script>` into the module layout above; introduce the shell + nav + hash router; port the
   three existing views as-is (device cards, existing group cards, existing scene cards) and collapse
   the triplicated menu/rename/delete into `ui.js`. Outcome: identical behaviour, new structure,
   no god-file. Verified by a full manual walk of today's features.
2. **UX redesign on the new structure.** Replace the group card with the compact strip; replace the
   two name-first creation paths with the shared searchable picker modal; strip all emoji; apply the
   frontend-design polish.

## Testing & verification

- **No backend changes** → the .NET test suite is unaffected and stays green.
- Frontend is verified as established in this repo: `node --check` on each extracted module (catches
  syntax/parse errors) plus a **manual browser walk** against the running Docker stack (the human
  drives — commission is live; create/toggle/rename/delete a group, capture/recall/rename/delete a
  scene, tab between views, confirm the log dock + SSE survive tab switches, confirm search +
  controllable filter on a large device list).
- Behaviour parity after Phase 1 is the acceptance gate before Phase 2 begins.

## Out of scope (YAGNI)

- **No framework, no bundler/build step** — native ES modules only.
- **No backend/REST/MQTT/OpenAPI changes** — reuses every existing endpoint and frame.
- **Group brightness/colour control** — master on/off only; richer group control is a separate
  feature.
- **The "group master switch shows unchecked until the first SSE echo" limitation** — inherent to
  the write-only + optimistic-echo group design; unchanged here.
- **No new device-side capabilities** — this is purely dashboard IA + UX + code organization.
