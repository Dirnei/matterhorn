# Dashboard Restructure + Groups/Scenes UX Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Break the 1168-line `wwwroot/index.html` god-file into a thin shell + ES modules with a single-page tabbed IA, then replace the weak group card and name-first creation flow with a compact-strip card and a shared searchable device-picker modal — frontend only, no backend changes.

**Architecture:** One served `index.html` shell (header, `Devices · Groups · Scenes` nav, `#view` container, log dock, modal root) + `app.css` + native ES modules under `js/` (no build step, no framework). `app.js` is a hash router that mounts one view module at a time; `sse.js` owns a single persistent EventSource; `store.js` holds the device/group/scene caches; `ui.js` holds shared, entity-agnostic UI helpers (toast, kebab menu, confirm modal, inline rename, and the picker modal) that replace today's three near-duplicate copies.

**Tech Stack:** Vanilla JS ES modules (browser-native), served as static files by ASP.NET `UseStaticFiles`; `iro.min.js` (colour wheel); Node v24 for `node --check` (syntax) and `node --test` (pure-logic unit tests). No npm/package.json.

## Global Constraints

- **No framework, no bundler, no build step** — native ES modules only (`<script type="module">`, `import`/`export`).
- **No backend/REST/MQTT/OpenAPI/actor changes** — reuse every existing endpoint and SSE frame. The .NET test suite must stay green and untouched.
- **No emoji anywhere** in shipped UI — icons are CSS or inline SVG or text.
- **Real Matterhorn theme** — reuse the existing CSS custom properties and "station" visual language (enzian `--enzian`, loden `--loden`, alpenglow `--alpenglow`, mono coordinates). Do not restyle unrelated elements.
- **XSS safety preserved** — every user/device/group/scene string entering the DOM goes through `esc()`; modal titles/bodies use `textContent`.
- **"Controllable" = a device with at least one settable expose** (`exposes.some(e => (e.access & 2) === 2)`). Derived client-side; never a backend call.
- **Behavior parity is the Phase-1 gate** — after each extraction task the dashboard must behave exactly as before (same features, same interactions).
- **Verification per task:** `node --check js/<module>.js` on every JS file touched (must print nothing / exit 0); `node --test test/dashboard/` for pure-logic tasks; plus the task's explicit **manual browser walk** against the running Docker stack (`docker compose up -d --build`, http://localhost:16090). The human drives the browser; state that clearly in the report.
- **Commit after every task.** Single-line conventional messages (`refactor:`, `feat:`). No `Co-Authored-By` trailer. No `git push`. Stage only the files the task changed, by explicit path.
- **Static-serving note:** everything under `wwwroot/` is web-served and shipped in the Docker image. Keep Node **test** files OUT of `wwwroot` — they live in `test/dashboard/`.

---

## File map (target)

```
src/Matterhorn/wwwroot/
  index.html          shell: header · nav tabs · <main id="view"> · #log-dock · #modal-root
  app.css             all styles (moved from the inline <style>)
  iro.min.js          unchanged
  js/
    app.js            boot: read API key, open SSE, mount dock, start hash router
    api.js            apiKey get/set + headers + api(path,opts) + resource helpers
    store.js          device/group/scene caches + pure helpers (isControllable, filterDevices, suggestName)
    sse.js            EventSource lifecycle + on(type, handler) dispatch
    ui.js             toast, esc, cssId, fmt, kebabMenu, confirmModal, inlineRename, createPickerModal
    dock.js           station-log dock (shell-level, always mounted)
    commission.js     header commission form + progress modal (works on any tab)
    views/
      devices.js      mount/unmount + device station cards
      groups.js       mount/unmount + compact-strip group cards
      scenes.js       mount/unmount + scene cards
test/dashboard/
    store.test.mjs    node --test for isControllable / filterDevices / suggestName
```

Current `index.html` reference (line numbers as of this plan; locate by name if shifted):
styles `10–207`; script `283–1165`. Function inventory: utils `toast/esc/cssId/fmt/headers/api/patch/patchGroup`; device `loadAll/render/station/colorControl/control/readoutRow/applyState` + `devMenu/openMenu/closeMenu/startRename/backdrop/openUnpair/closeUnpair/doUnpair`; groups `loadGroups/renderGroups/groupCard/applyGroupState` + `groupMenu.../startGroupRename/groupBackdrop.../doDeleteGroup`; scenes `loadScenes/renderScenes/sceneCard/doRecallScene` + `sceneMenu.../startSceneRename/sceneBackdrop.../doDeleteScene` + `captureBackdrop/openCapture/closeCapture` + captureForm/newGroupForm handlers; SSE `connect/es`; commission `commModal/COMM_QUIPS/commQuip/openComm/closeComm/commOutcome/commOnLog`; dock `dock/dockView/dockRender/dockPush` + dock handlers/resize; boot `connect();loadAll();` + ridge animation + keyInput handler.

---

# Phase 1 — Extract to modules (behavior-preserving)

The dashboard must work identically after **every** Phase-1 task.

### Task 1: Extract CSS to `app.css`

**Files:**
- Create: `src/Matterhorn/wwwroot/app.css`
- Modify: `src/Matterhorn/wwwroot/index.html` (remove inline `<style>`, add `<link>`)

**Interfaces:** Produces `app.css` (all dashboard styles). No JS impact.

- [ ] **Step 1: Move the styles**

Cut the entire contents **between** `<style>` and `</style>` (currently lines ~11–206) out of `index.html` into a new `src/Matterhorn/wwwroot/app.css` (CSS only — do not include the `<style>` tags).

- [ ] **Step 2: Link it**

In `index.html` `<head>`, replace the now-empty `<style>…</style>` with:

```html
<link rel="stylesheet" href="app.css" />
```

- [ ] **Step 3: Verify parity (manual)**

Run: `docker compose up -d --build` then open http://localhost:16090
Expected: dashboard looks pixel-identical to before (same theme, cards, dock). Confirm light/dark both still work (OS theme toggle).

- [ ] **Step 4: Commit**

```bash
git add src/Matterhorn/wwwroot/app.css src/Matterhorn/wwwroot/index.html
git commit -m "refactor: extract dashboard CSS to app.css"
```

---

### Task 2: Move the inline script into `js/app.js` (single module, verbatim)

**Files:**
- Create: `src/Matterhorn/wwwroot/js/app.js`
- Modify: `src/Matterhorn/wwwroot/index.html`

**Interfaces:** Produces `js/app.js` containing the entire current script, unchanged, loaded as a module.

- [ ] **Step 1: Move the script**

Cut the entire contents **between** `<script>` (the one at line ~283, NOT the `iro.min.js` tag) and its `</script>` into a new file `src/Matterhorn/wwwroot/js/app.js` (JS only, no `<script>` tags).

- [ ] **Step 2: Reference it as a module**

In `index.html`, keep `<script src="iro.min.js"></script>`, and replace the emptied inline `<script>…</script>` with:

```html
<script type="module" src="js/app.js"></script>
```

> Note: `iro` is a global from `iro.min.js` (a classic script). ES modules can still read globals (`window.iro`), so `colorControl` keeps working. Leave the `iro.min.js` tag as a classic script before the module.

- [ ] **Step 3: Verify syntax + parity**

Run: `node --check src/Matterhorn/wwwroot/js/app.js` → expect exit 0, no output.
Run: `docker compose up -d --build`, open http://localhost:16090 → expect fully identical behavior (device cards, groups, scenes, commission, log dock, SSE live updates).

- [ ] **Step 4: Commit**

```bash
git add src/Matterhorn/wwwroot/js/app.js src/Matterhorn/wwwroot/index.html
git commit -m "refactor: move dashboard script into js/app.js module"
```

---

### Task 3: Extract `ui.js`, `api.js`, `store.js`, `sse.js` (+ pure-logic tests)

Pull the shared primitives out of `app.js` into focused modules. `app.js` imports them. Behavior identical.

**Files:**
- Create: `src/Matterhorn/wwwroot/js/ui.js`, `js/api.js`, `js/store.js`, `js/sse.js`
- Create: `test/dashboard/store.test.mjs`
- Modify: `src/Matterhorn/wwwroot/js/app.js`

**Interfaces:**
- Produces `ui.js`: `export function toast(msg)`, `export const esc = s => …`, `export const cssId = s => …`, `export const fmt = v => …`.
- Produces `api.js`: `export function getApiKey()`, `export function setApiKey(k)`, `export async function api(path, opts)` (adds `X-Api-Key` header when a key is set; throws on 401 after toasting), `export function apiKeyQuery()` (returns `?api_key=…` or `''`, for SSE URL).
- Produces `store.js`: `export const devices = new Map()` (friendly_name → descriptor), `export const deviceState = new Map()` (friendly_name → state obj), `export const groups = new Map()`, `export const scenes = new Map()`, and pure helpers `export function isControllable(descriptor)`, `export function filterDevices(list, query, controllableOnly)`, `export function suggestName(prefix, existingNames)`.
- Produces `sse.js`: `export function connectSse(onFrame)` where `onFrame(msg)` is called per parsed frame; manages the `EventSource` and reconnect-on-key-change via a `reconnect()` export.

- [ ] **Step 1: Write the pure-helper tests (RED)**

Create `test/dashboard/store.test.mjs`:

```js
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { isControllable, filterDevices, suggestName } from '../../src/Matterhorn/wwwroot/js/store.js';

const light = { friendly_name: 'lamp', device_type: 'Dimmable Light',
  exposes: [{ property: 'state', access: 7 }, { property: 'brightness', access: 7 }] };
const sensor = { friendly_name: 'temp1', device_type: 'Temperature Sensor',
  exposes: [{ property: 'temperature', access: 5 }] }; // access 5 = published|get, no set bit

test('isControllable: light with a settable expose is controllable', () => {
  assert.equal(isControllable(light), true);
});
test('isControllable: read-only sensor is not controllable', () => {
  assert.equal(isControllable(sensor), false);
});
test('filterDevices: substring match on friendly_name, case-insensitive', () => {
  const out = filterDevices([light, sensor], 'LAM', false);
  assert.deepEqual(out.map(d => d.friendly_name), ['lamp']);
});
test('filterDevices: controllableOnly drops sensors', () => {
  const out = filterDevices([light, sensor], '', true);
  assert.deepEqual(out.map(d => d.friendly_name), ['lamp']);
});
test('suggestName: first free numbered name', () => {
  assert.equal(suggestName('group', new Set(['group_1'])), 'group_2');
  assert.equal(suggestName('scene', new Set()), 'scene_1');
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `node --test test/dashboard/`
Expected: FAIL — cannot import from `store.js` (module/exports don't exist yet).

- [ ] **Step 3: Create `store.js` with the caches + pure helpers**

```js
// js/store.js — in-memory caches (updated by views/SSE) + DOM-free pure helpers.
export const devices = new Map();      // friendly_name -> descriptor
export const deviceState = new Map();  // friendly_name -> state object
export const groups = new Map();       // friendly_name -> { friendly_name, members }
export const scenes = new Map();       // friendly_name -> { friendly_name, members }

export function isControllable(d) {
  return Array.isArray(d?.exposes) && d.exposes.some(e => (e.access & 2) === 2);
}

export function filterDevices(list, query, controllableOnly) {
  const q = (query || '').trim().toLowerCase();
  return list.filter(d =>
    (!controllableOnly || isControllable(d)) &&
    (q === '' || d.friendly_name.toLowerCase().includes(q)));
}

export function suggestName(prefix, existingNames) {
  const taken = existingNames instanceof Set ? existingNames : new Set(existingNames);
  for (let i = 1; ; i++) { const n = `${prefix}_${i}`; if (!taken.has(n)) return n; }
}
```

- [ ] **Step 4: Run tests to verify green**

Run: `node --test test/dashboard/`
Expected: PASS (5/5).

- [ ] **Step 5: Create `ui.js`, `api.js`, `sse.js` by moving code out of `app.js`**

Move from `app.js` into **`ui.js`** and add `export`: `toast`, `esc`, `cssId`, `fmt`.
Move into **`api.js`** and add `export`: the apiKey handling (`apiKey` var → `getApiKey()`/`setApiKey()` backed by `localStorage.mh_key`), `headers`, `api`, plus `apiKeyQuery()` returning `getApiKey() ? '?api_key='+encodeURIComponent(getApiKey()) : ''`. (Leave `patch`/`patchGroup` in `app.js` for now — they move to views in Task 4.)
Move into **`sse.js`** and add `export`: the `EventSource` logic from `connect()`/`es`, refactored to `connectSse(onFrame)` — it builds the URL `'/api/events'+apiKeyQuery()`, sets `link` live/dead classes via a small callback or by importing the shell later (for now keep the `link` element lookup by id inside sse.js), and calls `onFrame(JSON.parse(ev.data))` in `onmessage`. Export a `reconnect()` that closes and reopens.

In `app.js`, add imports at top:

```js
import { toast, esc, cssId, fmt } from './ui.js';
import { getApiKey, setApiKey, api, apiKeyQuery } from './api.js';
import * as store from './store.js';
import { connectSse, reconnect } from './sse.js';
```

and delete the moved declarations from `app.js`, wiring the remaining code to the imported names (e.g. the boot `connect()` becomes `connectSse(onFrame)` where `onFrame` is the existing dispatch logic; the key-input handler calls `setApiKey(...)` + `reconnect()` + `loadAll()`).

- [ ] **Step 6: Verify syntax + parity**

Run: `node --check` on each of `js/app.js js/ui.js js/api.js js/store.js js/sse.js` (all exit 0).
Run: `node --test test/dashboard/` (still 5/5).
Run: rebuild + manual walk → identical behavior, including API-key entry re-subscribing SSE and live state updates.

- [ ] **Step 7: Commit**

```bash
git add src/Matterhorn/wwwroot/js/ test/dashboard/store.test.mjs
git commit -m "refactor: split dashboard into ui/api/store/sse modules"
```

---

### Task 4: Extract view modules + `dock.js` + `commission.js`

Move the device/group/scene/dock/commission code into their own modules. Still one page showing all sections (no router yet).

**Files:**
- Create: `js/views/devices.js`, `js/views/groups.js`, `js/views/scenes.js`, `js/dock.js`, `js/commission.js`
- Modify: `js/app.js`

**Interfaces:**
- Each view exports `export function mount(container)` and `export function unmount()` and `export function onFrame(msg)` (handles the SSE frames it cares about). For now `mount` renders into the existing grid element the view owns.
- `dock.js`: `export function mountDock()`, `export function onFrame(msg)` (handles `log`).
- `commission.js`: `export function mountCommission()`, `export function onFrame(msg)` (handles commission-related `log` lines for the progress modal).

- [ ] **Step 1: Move device code → `views/devices.js`**

Move `loadAll` (rename its device part to `mount`/`load`), `render`, `station`, `colorControl`, `control`, `readoutRow`, `applyState`, plus the device menu/rename/unpair (`devMenu/openMenu/closeMenu/startRename/backdrop/openUnpair/closeUnpair/doUnpair`), `SETTABLE`, `HSMAX`, `pickers`, and `patch`. Import `{ esc, cssId, fmt, toast }` from `ui.js`, `{ api }` from `api.js`, `store`. Export `mount(container)`, `unmount()`, `onFrame(msg)` (handles `state` → `applyState`, `devices` → reload).

- [ ] **Step 2: Move group code → `views/groups.js`**

Move `loadGroups/renderGroups/groupCard/applyGroupState/patchGroup` + group menu/rename/delete. Export `mount/unmount/onFrame` (`groups` → reload; `state` where device is a group → `applyGroupState`). Also move the `newGroupForm` submit handler here (it will be replaced in Phase 2).

- [ ] **Step 3: Move scene code → `views/scenes.js`**

Move `loadScenes/renderScenes/sceneCard/doRecallScene` + scene menu/rename/delete + `captureBackdrop/openCapture/closeCapture` + the `captureForm` + `captureSceneBtn` handlers. Export `mount/unmount/onFrame` (`scenes` → reload).

- [ ] **Step 4: Move dock → `dock.js`, commission → `commission.js`**

Dock: move `dock/dockView/dockRender/dockPush` + dock event handlers + resize + `mh_dock_h` restore. Export `mountDock()` and `onFrame(msg)` (`log` → `dockPush`; on connect, hydrate). Commission: move `commModal/COMM_QUIPS/commQuip/openComm/closeComm/commOutcome/commOnLog` + commissionForm handler. Export `mountCommission()` and `onFrame(msg)`.

- [ ] **Step 5: Rewire `app.js` as the composition root (no router yet)**

`app.js` now: imports the views + dock + commission; on boot mounts all three views into their existing grids, `mountDock()`, `mountCommission()`, opens SSE with a single `onFrame` that fans each frame to every module's `onFrame`; keeps the ridge animation + keyInput handler. Behavior identical (all sections visible).

```js
import * as devices from './views/devices.js';
import * as groups from './views/groups.js';
import * as scenes from './views/scenes.js';
import * as dock from './dock.js';
import * as commission from './commission.js';
import { connectSse } from './sse.js';

const modules = [devices, groups, scenes, dock, commission];
function onFrame(msg){ for (const m of modules) m.onFrame?.(msg); }
// mount existing grids, then:
connectSse(onFrame);
```

- [ ] **Step 6: Verify syntax + parity**

Run: `node --check` on every new/modified JS file (all exit 0). `node --test test/dashboard/` (5/5).
Manual walk: every existing feature works — device control, colour wheel, rename/unpair, group create/toggle/rename/delete, scene capture/recall/rename/delete, commission modal, log dock, live SSE.

- [ ] **Step 7: Commit**

```bash
git add src/Matterhorn/wwwroot/js/
git commit -m "refactor: split dashboard into view/dock/commission modules"
```

---

### Task 5: Dedupe menu / inline-rename / confirm-modal into `ui.js`

Replace the three near-identical copies (device/group/scene) with one parameterized set of helpers.

**Files:**
- Modify: `js/ui.js`, `js/views/devices.js`, `js/views/groups.js`, `js/views/scenes.js`

**Interfaces:** Produces in `ui.js`:
- `export function kebabMenu(items)` — `items: [{ label, danger?, onClick }]`; returns `{ open(anchorBtn), close(returnFocus) }`, handling body-level positioning, outside-click close, arrow-key nav, aria (as today's device menu does).
- `export function confirmModal({ title, body, confirmLabel, danger, onConfirm })` — returns `{ open(originBtn) }`; focus-trap + Esc as today's unpair modal.
- `export function inlineRename(nameEl, currentName, onCommit)` — swaps the name element for an input, commit on Enter/blur, cancel on Esc; `onCommit(newName)` returns a Promise; restores text after.

- [ ] **Step 1: Implement the three helpers in `ui.js`**

Lift the device implementations (`openMenu/closeMenu` + `devMenu`, `openUnpair/closeUnpair` + `backdrop`, `startRename`) as the canonical versions, generalized to take their config via parameters (labels, handlers) instead of hard-coded device actions. Keep the exact a11y behavior (focus into menu, arrow nav, Esc, return focus).

- [ ] **Step 2: Rewire the three views to use them**

In `devices.js`, replace the device menu/rename/unpair code with `kebabMenu([{label:'Rename',onClick:…},{label:'Unpair…',danger:true,onClick:…}])`, `inlineRename(...)`, `confirmModal({...})`. Do the same in `groups.js` (Rename/Delete) and `scenes.js` (Rename/Delete), deleting each view's private copies.

- [ ] **Step 3: Verify syntax + parity**

Run: `node --check` on `ui.js` + the three views. Manual walk: the kebab menu, inline rename (Enter commits, Esc cancels, focus returns to kebab), and delete/unpair confirm modal all work identically for **devices, groups, and scenes**.

- [ ] **Step 4: Commit**

```bash
git add src/Matterhorn/wwwroot/js/
git commit -m "refactor: unify menu/rename/confirm helpers in ui.js"
```

---

### Task 6: Shell + tab nav + hash router

Turn the single scrolling page into tabbed views mounted by a hash router. Dock + commission + SSE stay shell-level.

**Files:**
- Modify: `src/Matterhorn/wwwroot/index.html`, `js/app.js`, `app.css`
- Modify: `js/views/devices.js`, `js/views/groups.js`, `js/views/scenes.js` (mount into the shared `#view` container instead of their own grids)

**Interfaces:** Produces the router in `app.js`; each view's `mount(container)` renders its grid into the passed container and `unmount()` clears it.

- [ ] **Step 1: Reduce `index.html` to the shell**

`<main>` becomes:

```html
<nav class="tabs" id="tabs">
  <a href="#/devices" data-tab="devices">Devices</a>
  <a href="#/groups" data-tab="groups">Groups</a>
  <a href="#/scenes" data-tab="scenes">Scenes</a>
</nav>
<main id="view"></main>
<div id="modal-root"></div>
```

Keep the header (wordmark, link status, api key, commission form) and the log dock markup. Remove the three separate `#grid`/`#groupsGrid`/`#scenesGrid` section wrappers (the views now build their own grid inside `#view`).

- [ ] **Step 2: Add tab styling to `app.css`**

Add a `.tabs` bar (horizontal, enzian underline on the active tab, mono-ish label) consistent with the header/theme. Active tab gets `.active`.

- [ ] **Step 3: Router in `app.js`**

```js
const views = { devices, groups, scenes };
let current = null;
function route(){
  const name = (location.hash.replace(/^#\//,'') || 'devices');
  const view = views[name] || views.devices;
  if (current && current !== view) current.unmount?.();
  current = view;
  document.querySelectorAll('#tabs a').forEach(a => a.classList.toggle('active', a.dataset.tab === name));
  view.mount(document.getElementById('view'));
}
window.addEventListener('hashchange', route);
// boot: mountDock(); mountCommission(); connectSse(onFrame); route();
```

`onFrame` still fans to ALL view modules (so a background view's cache stays warm) **plus** dock + commission; only the mounted view is in the DOM. Ensure each view's `onFrame` no-ops safely when not mounted (guard DOM writes with a "mounted" flag).

- [ ] **Step 4: Views mount into `#view`**

Update each view's `mount(container)` to create its grid inside `container` and `unmount()` to clear it and drop any open menus/modals it owns.

- [ ] **Step 5: Verify syntax + parity**

Run: `node --check` all touched JS. Manual walk: tabs switch views via nav and via URL hash; default `#/devices`; the **log dock and SSE stay alive across tab switches** (no reconnect, no dock reset); live state updates land on whichever tab is showing.

- [ ] **Step 6: Commit**

```bash
git add src/Matterhorn/wwwroot/
git commit -m "feat: single-page tabbed dashboard with hash routing"
```

**Phase 1 complete: same features, modular structure, tabbed IA.**

---

# Phase 2 — Groups/scenes UX redesign

### Task 7: Group card → compact strip + inline member management

**Files:**
- Modify: `js/views/groups.js`, `app.css`

**Interfaces:** Consumes `store.groups`, `store.devices`, `api`, `ui.confirmModal/kebabMenu/inlineRename`. Produces the new `groupCard(g)` renderer.

- [ ] **Step 1: Replace `groupCard` with the compact strip**

Rewrite `groupCard(g)` to render one row: a CSS/SVG glyph, `g.friendly_name`, a member-count subline (`g.members.join(' · ')`, `esc`'d), a master on/off switch (checkbox) on the right → `PATCH /api/groups/{name}` `{state: checked?'ON':'OFF'}`, and a kebab (`kebabMenu` with Rename/Delete). Clicking the row (not the switch/kebab) toggles an **expanded** region: the member list with a per-member remove (`DELETE /api/groups/{name}/members/{device}`) and an "Add device" button that opens the shared picker (Task 8) filtered to non-members, adding via `PUT /api/groups/{name}/members/{device}`.

- [ ] **Step 2: Style the strip in `app.css`**

Add `.group-strip` (row layout), `.group-strip .expand` (collapsible member area), reusing switch/chip styles already in the sheet. No emoji — the glyph is a small inline SVG or a CSS shape.

- [ ] **Step 3: Verify syntax + manual**

Run: `node --check js/views/groups.js`. Manual: group renders as a strip; master switch fans out (both members react on real devices); row-expand shows members; add/remove member works and re-renders; rename/delete via kebab work.

- [ ] **Step 4: Commit**

```bash
git add src/Matterhorn/wwwroot/js/views/groups.js src/Matterhorn/wwwroot/app.css
git commit -m "feat: compact-strip group card with inline member management"
```

---

### Task 8: Shared searchable picker modal + wire New-group

**Files:**
- Modify: `js/ui.js`, `js/views/groups.js`, `app.css`
- Modify: `test/dashboard/store.test.mjs` (already covers filterDevices/suggestName from Task 3 — no change needed unless extending)

**Interfaces:** Produces `ui.createPickerModal(opts)` where
`opts = { title, submitLabel, note?, initialName, preselected?: Set<string>, onSubmit(name, deviceFriendlyNames[]) }`.
It renders into `#modal-root`: a name input (pre-filled `initialName`), a search input, a `Controllable | All` segmented toggle (default Controllable), a scrollable checklist built from `store.devices` via `store.filterDevices(list, query, controllableOnly)`, and a selected-count footer with Cancel / submit. Returns `{ open() }`.

- [ ] **Step 1: Implement `createPickerModal` in `ui.js`**

Full component (name + search + toggle + list + footer), theme-styled, `esc()` on every device name, `textContent` for the title, focus-trap + Esc-close (reuse the confirm-modal trap pattern). Search input filters live via `store.filterDevices`; the toggle flips `controllableOnly` and re-renders the list; each row toggles selection; footer shows `${selected.size} selected`; submit calls `onSubmit(nameInput.value.trim(), [...selected])` then closes.

```js
export function createPickerModal(opts){
  // build DOM under #modal-root; state: controllableOnly=true, selected=new Set(opts.preselected||[])
  // renderList(): const all=[...store.devices.values()];
  //   for (const d of store.filterDevices(all, searchEl.value, controllableOnly)) { row w/ checkbox... }
  // returns { open(){ nameEl.value=opts.initialName; renderList(); show(); nameEl.focus(); } }
}
```

- [ ] **Step 2: Add picker styles to `app.css`**

`.picker-modal` (name field, `.picker-search`, `.picker-seg` segmented toggle, `.picker-list` with `max-height` + scroll + bottom fade, `.picker-item` rows, `.picker-count`). Match the theme; no emoji (search icon = inline SVG).

- [ ] **Step 3: Wire "New group" to the picker**

In `groups.js`, replace the old `newGroupForm` name-first flow with a "New group" button that opens:

```js
createPickerModal({
  title: 'New group', submitLabel: 'Create', initialName: store.suggestName('group', new Set(store.groups.keys())),
  onSubmit: async (name, members) => {
    await api('/api/groups/'+encodeURIComponent(name), { method:'PUT', body: JSON.stringify({ members }) });
    // 409 -> toast('name taken'); 400 -> toast('bad name'); success -> SSE 'groups' refreshes the view
  }
}).open();
```

- [ ] **Step 4: Verify pure logic + manual**

Run: `node --test test/dashboard/` (filterDevices/suggestName still green). `node --check js/ui.js js/views/groups.js`.
Manual (needs a decent device count — use the real Pi's devices or add demo devices): open New-group; the list defaults to controllable-only; typing in search filters live; "All" reveals sensors; select a few; the name is pre-suggested and editable; Create makes the group; the strip appears.

- [ ] **Step 5: Commit**

```bash
git add src/Matterhorn/wwwroot/js/ui.js src/Matterhorn/wwwroot/js/views/groups.js src/Matterhorn/wwwroot/app.css
git commit -m "feat: shared searchable device-picker modal; new-group uses it"
```

---

### Task 9: Capture-scene via the shared picker; remove old modals

**Files:**
- Modify: `js/views/scenes.js`
- Modify: `index.html` (remove leftover capture/new-group markup if any), `app.css` (drop dead styles)

**Interfaces:** Consumes `ui.createPickerModal`, `store.suggestName`, `api`.

- [ ] **Step 1: Wire "Capture scene" to the picker**

Replace the old `openCapture`/`captureForm` flow with:

```js
createPickerModal({
  title: 'Capture scene', submitLabel: 'Capture',
  note: 'The current state of the selected devices will be captured.',
  initialName: store.suggestName('scene', new Set(store.scenes.keys())),
  onSubmit: async (name, devices) => {
    await api('/api/scenes/'+encodeURIComponent(name), { method:'PUT', body: JSON.stringify({ devices }) });
  }
}).open();
```

Render the `note` (when present) under the name field in the picker (add support in `createPickerModal` if not already there).

- [ ] **Step 2: Delete dead code/markup**

Remove the old capture-modal DOM builder and any orphaned `captureBackdrop`/`newGroupForm` markup + their now-unused CSS.

- [ ] **Step 3: Verify + manual**

Run: `node --check js/views/scenes.js`. Manual: Capture-scene opens the same picker (with the note), captures the selected devices' current state; recall restores it (this exercises the earlier scene-snapshot fix on real devices).

- [ ] **Step 4: Commit**

```bash
git add src/Matterhorn/wwwroot/js/views/scenes.js src/Matterhorn/wwwroot/index.html src/Matterhorn/wwwroot/app.css
git commit -m "feat: capture-scene uses the shared picker; remove old modals"
```

---

### Task 10: Emoji purge + frontend-design polish

**Files:**
- Modify: `src/Matterhorn/wwwroot/` (index.html, app.css, js/**)

- [ ] **Step 1: Purge emoji**

Search and replace every emoji in shipped UI with CSS/SVG/text:

Run: `grep -rnP "[\x{1F000}-\x{1FAFF}\x{2190}-\x{27BF}\x{2B00}-\x{2BFF}\x{FE0F}]" src/Matterhorn/wwwroot` — replace each hit (search icon, group glyph, kebab `⋮` is fine as a text glyph but avoid emoji variants, any status icons) with an inline SVG or a CSS shape. (`⋮`/`×`/`✓` as plain characters are acceptable; emoji pictographs are not.)

- [ ] **Step 2: Polish pass (apply the frontend-design skill)**

With the structure stable, do a focused visual pass on the new surfaces — the group strip, the picker modal, the tab bar — for spacing, hierarchy, hover/focus states, and dark/light parity, staying within the Matterhorn theme tokens. Keep it tasteful, not templated.

- [ ] **Step 3: Verify**

Run: `grep` from Step 1 returns nothing. `node --check` all JS. Manual: full walk on both light and dark; tab bar, group strip, and picker look polished and consistent; no layout breakage at narrow widths (the page body must not scroll horizontally).

- [ ] **Step 4: Commit**

```bash
git add src/Matterhorn/wwwroot/
git commit -m "feat: remove emoji and polish groups/scenes dashboard surfaces"
```

---

## Self-Review notes (author checklist — resolved)

- **Spec coverage:** IA/tabs+router (Task 6); compact-strip card (Task 7); shared searchable picker w/ controllable-default (Task 8) + scenes (Task 9); no-emoji + theme polish (Task 10); ES-module split ui/api/store/sse/dock/commission/views (Tasks 2–5); controllable-derived-client-side + no backend change (Tasks 3, 8); dedup of triplicated helpers (Task 5); two-phase extract-then-redesign sequencing (Phase 1 vs 2). All spec sections mapped.
- **Testing honesty:** DOM/view/integration work is verified by `node --check` (syntax) + explicit manual browser walks (the repo has no DOM test harness and the spec fixed verification as node-check + manual). Genuinely pure logic (`isControllable`, `filterDevices`, `suggestName`) gets real `node --test` unit tests (Task 3), which is where a logic bug would actually hide.
- **Placeholder scan:** new components (router, ui helpers, picker) carry full code or exact export/behavior contracts; extraction tasks give precise move-lists (source functions named) + import wiring rather than re-pasting ~1000 lines of unchanged code — concrete, not vague.
- **Type/name consistency:** `mount(container)`/`unmount()`/`onFrame(msg)` view contract is uniform across Tasks 4, 6; `createPickerModal(opts)` signature and `onSubmit(name, deviceNames[])` match between Tasks 8 and 9; `store` helper names match the Task-3 tests and their Task-8 usage.
- **Risk note for executor:** Phase 1 Task 3–4 is the delicate part — converting shared module-level state (`defs`/`states`/`pickers`/`apiKey`/`es`) into explicit module exports/imports. Do it in the stated order, re-running the manual parity walk after each task; do not proceed to Phase 2 until parity holds.
```
