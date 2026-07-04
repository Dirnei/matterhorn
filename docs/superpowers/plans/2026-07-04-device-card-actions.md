# Device Card Actions (Rename + Unpair) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give each dashboard device card a discoverable `⋮` action menu — Rename (inline edit) and Unpair (decommission from the fabric, with a confirmation modal) — backed by a new REST unpair endpoint.

**Architecture:** The gateway's remove flow already exists (`RemoveRequest` → `RemoveNode` → `node_removed` → cleanup). This adds a synchronous `RemoveAccepted(Found)` reply so a new `DELETE /api/devices/{name}` REST endpoint can return 202/404, and a vanilla-JS `⋮` menu + inline rename + confirmation modal in the dashboard. The card's rename stops being a hidden name-click.

**Tech Stack:** .NET 10, Akka.NET, xUnit + Akka.TestKit.Xunit2, NSwag (contract-first REST), vanilla JS dashboard (no framework).

## Global Constraints

- Target framework `net10.0`, `Nullable` enabled.
- `contracts/matterhorn.openapi.yaml` is the single source of truth for REST; the controller base regenerates from it into `obj/generated/ApiContract.g.cs` on every `dotnet build`. Never hand-edit generated code. For a status-only operation NSwag generates `Task<IActionResult>` — match the generated signature exactly.
- Vertical slices, ASP.NET-style namespaces; match the terse existing style of each file.
- Dashboard is dependency-free vanilla JS in one file (`src/Matterhorn/wwwroot/index.html`); reuse the existing `api(...)`, `toast(...)`, `esc(...)`, `cssId(...)` helpers. The `.station` card has `overflow:hidden`, so the menu popover and modal MUST be body-level elements (a dropdown inside the card would be clipped).
- Unpair is destructive/irreversible from the dashboard (decommission → must re-pair with the physical setup code). The UI must confirm before acting.
- Commits: conventional-commit style, **no `Co-Authored-By` / co-author trailer**.
- Run tests from the repo root: `dotnet test`.

---

### Task 1: Gateway replies `RemoveAccepted` to the caller

Give `OnRemove` a synchronous reply so a REST facade can distinguish "started" from "no such device", mirroring how `OnRename` replies to `Sender`.

**Files:**
- Modify: `src/Matterhorn/Bridge/BridgeMessages.cs`
- Modify: `src/Matterhorn/Bridge/MatterGatewayActor.cs` (`OnRemove`)
- Test: `src/Matterhorn.Test/Bridge/MatterGatewayActorTests.cs`

**Interfaces:**
- Produces: `record RemoveAccepted(bool Found)` in namespace `Matterhorn.Bridge`. Replied to a `RemoveRequest(FriendlyName, Transaction)`; `Found` is whether the device was known at request time. When found, `RemoveNode(nodeId)` is still invoked (async) exactly as before.
- Consumes: existing `RemoveRequest`, `FakeMatterController.Removed` (a `List<ulong>` of removed node ids).

- [ ] **Step 1: Write the failing tests**

Add to `src/Matterhorn.Test/Bridge/MatterGatewayActorTests.cs`:

```csharp
[Fact]
public void RemoveRequest_for_a_known_device_replies_found_and_calls_RemoveNode()
{
    var fake = new FakeMatterController();
    var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, new InMemoryMqttPublisher(), new MqttTopics("matterhorn")));
    fake.Emit(new NodeAdded(Light(5)));
    AwaitAssert(() => Assert.Single(gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices()).Result));

    var result = gw.Ask<RemoveAccepted>(new RemoveRequest("bulb_5_1", "tx1")).Result;

    Assert.True(result.Found);
    AwaitAssert(() => Assert.Contains(5UL, fake.Removed));
}

[Fact]
public void RemoveRequest_for_an_unknown_device_replies_not_found_and_skips_RemoveNode()
{
    var fake = new FakeMatterController();
    var gw = Sys.ActorOf(MatterGatewayActor.Props(fake, new InMemoryMqttPublisher(), new MqttTopics("matterhorn")));

    var result = gw.Ask<RemoveAccepted>(new RemoveRequest("ghost", "tx1")).Result;

    Assert.False(result.Found);
    Assert.Empty(fake.Removed);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~MatterGatewayActorTests.RemoveRequest"`
Expected: FAIL — `RemoveAccepted` does not exist.

- [ ] **Step 3: Add the message**

In `src/Matterhorn/Bridge/BridgeMessages.cs`, after the `RemoveRequest` record:

```csharp
/// <summary>Reply to a <see cref="RemoveRequest"/>: whether the device was known (drives REST 202 vs 404).</summary>
public record RemoveAccepted(bool Found);
```

- [ ] **Step 4: Reply from `OnRemove`**

In `src/Matterhorn/Bridge/MatterGatewayActor.cs`, replace the `OnRemove` method body:

```csharp
    private void OnRemove(RemoveRequest req)
    {
        var found = _byName.TryGetValue(req.FriendlyName, out var reg);
        Sender.Tell(new RemoveAccepted(found)); // REST Ask path; harmless when Told with NoSender (MQTT).
        if (found)
            _controller.RemoveNode(reg!.Info.NodeId, CancellationToken.None).ContinueWith(t =>
                new RemoveDone(req.Transaction, t.IsFaulted ? t.Exception!.GetBaseException().Message : null)).PipeTo(Self);
        else
            Self.Tell(new RemoveDone(req.Transaction, null));
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~MatterGatewayActorTests"`
Expected: PASS (existing + 2 new).

- [ ] **Step 6: Commit**

```bash
git add src/Matterhorn/Bridge/BridgeMessages.cs src/Matterhorn/Bridge/MatterGatewayActor.cs src/Matterhorn.Test/Bridge/MatterGatewayActorTests.cs
git commit -m "feat: gateway replies RemoveAccepted to a remove request"
```

---

### Task 2: REST `DELETE /api/devices/{name}` to unpair

**Files:**
- Modify: `contracts/matterhorn.openapi.yaml`
- Modify: `src/Matterhorn/Api/MatterhornController.cs`
- Test: `src/Matterhorn.Test/Api/MatterhornControllerTests.cs`

**Interfaces:**
- Consumes: `Bridge.RemoveRequest`, `Bridge.RemoveAccepted` (Task 1).
- Produces: `DELETE /api/devices/{name}` → 202 (decommission started) / 404 (unknown device).

- [ ] **Step 1: Add the contract operation**

In `contracts/matterhorn.openapi.yaml`, under the existing `/api/devices/{name}` path item, add a `delete:` sibling after the `patch:` block (before the `/api/devices/{name}/rename` path):

```yaml
    delete:
      operationId: removeDevice
      summary: Un-pair (decommission) a device from the fabric.
      tags: [devices]
      parameters:
        - $ref: '#/components/parameters/FriendlyName'
      responses:
        '202':
          description: Decommission started; the device leaves the fabric asynchronously.
        '404':
          description: No device with that friendly name.
```

- [ ] **Step 2: Regenerate + confirm the generated signature**

Run: `dotnet build src/Matterhorn/Matterhorn.csproj`
Expected: build FAILS — `MatterhornControllerBase.RemoveDevice` is abstract and unimplemented (confirms the contract regenerated). If it instead builds, open `src/Matterhorn/obj/generated/ApiContract.g.cs` and confirm an abstract `RemoveDevice(string name)` exists before continuing. The status-only DELETE generates `Task<IActionResult> RemoveDevice(string name)`.

- [ ] **Step 3: Write the failing tests**

Add to `src/Matterhorn.Test/Api/MatterhornControllerTests.cs`:

```csharp
[Fact]
public async Task Remove_device_returns_202_when_found()
{
    var probe = CreateTestProbe();
    var client = ClientWithGateway(probe.Ref);

    var task = client.DeleteAsync("/api/devices/bulb_5_1");

    var msg = probe.ExpectMsg<RemoveRequest>();
    Assert.Equal("bulb_5_1", msg.FriendlyName);
    probe.Reply(new RemoveAccepted(true));

    Assert.Equal(HttpStatusCode.Accepted, (await task).StatusCode);
}

[Fact]
public async Task Remove_missing_device_returns_404()
{
    var probe = CreateTestProbe();
    var client = ClientWithGateway(probe.Ref);

    var task = client.DeleteAsync("/api/devices/ghost");
    probe.ExpectMsg<RemoveRequest>();
    probe.Reply(new RemoveAccepted(false));

    Assert.Equal(HttpStatusCode.NotFound, (await task).StatusCode);
}
```

- [ ] **Step 4: Implement the override**

In `src/Matterhorn/Api/MatterhornController.cs`, add (after the `RenameDevice` override):

```csharp
    public override async Task<IActionResult> RemoveDevice(string name)
    {
        var tx = Guid.NewGuid().ToString("N");
        var result = await Gw.Ask<Bridge.RemoveAccepted>(new Bridge.RemoveRequest(name, tx), Timeout);
        return result.Found ? Accepted() : NotFound();
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~MatterhornControllerTests"`
Expected: PASS (existing + 2 new).

- [ ] **Step 6: Commit**

```bash
git add contracts/matterhorn.openapi.yaml src/Matterhorn/Api/MatterhornController.cs src/Matterhorn.Test/Api/MatterhornControllerTests.cs
git commit -m "feat: add REST DELETE /api/devices/{name} to unpair a device"
```

---

### Task 3: Dashboard `⋮` menu — inline rename + unpair modal

Replace the hidden name-click rename with a `⋮` menu offering **Rename** (inline edit) and **Unpair…** (confirmation modal → `DELETE`). Menu and modal are body-level (the card clips overflow). Use the frontend-design skill to make the header, menu, and modal read as one intentional system in the existing theme.

**Files:**
- Modify: `src/Matterhorn/wwwroot/index.html`

**Interfaces:**
- Consumes: `POST /api/devices/{name}/rename` (existing), `DELETE /api/devices/{name}` (Task 2); SSE `{"type":"devices"}` → existing `loadAll()` re-render.

- [ ] **Step 1: Invoke frontend-design for the visual pass**

Announce and use the **frontend-design skill** to guide the styling of the kebab button, the popover menu, and the confirmation modal within the existing Bavarian-blue theme (light/dark aware, visible focus states, a clear destructive color for Unpair). The baseline CSS in Step 2 is a functional starting point; apply frontend-design's guidance to elevate spacing, surfaces/elevation, and the destructive treatment. Keep scope to the device card and its menu/modal — no unrelated restyling.

- [ ] **Step 2: Add the CSS**

In `src/Matterhorn/wwwroot/index.html`, add these rules to the `<style>` block (after the existing `.toast` rules). Colors use the existing CSS variables so both themes are covered:

```css
  .st-tools { display:flex; align-items:center; gap:8px; }
  .kebab { background:transparent; border:none; padding:2px 7px; font-size:18px; line-height:1; color:var(--muted); border-radius:6px; }
  .kebab:hover { background:var(--surface2); color:var(--ink); }

  .menu { position:absolute; z-index:40; min-width:150px; background:var(--surface); border:1px solid var(--line);
          border-radius:9px; box-shadow:0 8px 24px rgba(0,0,0,.18); padding:5px; display:none; }
  .menu.open { display:block; }
  .menu button { display:flex; align-items:center; gap:9px; width:100%; text-align:left; background:transparent;
                 border:none; padding:8px 10px; border-radius:6px; color:var(--ink); }
  .menu button:hover { background:var(--surface2); }
  .menu button.danger { color:var(--alpenglow); }

  .name-edit { font-family:var(--mono); font-size:14px; padding:3px 6px; width:100%;
               border:1px solid var(--enzian); border-radius:6px; background:var(--surface2); color:var(--ink); }

  .modal-backdrop { position:fixed; inset:0; background:rgba(0,0,0,.45); z-index:50; display:none;
                    align-items:center; justify-content:center; padding:20px; }
  .modal-backdrop.open { display:flex; }
  .modal { background:var(--surface); border:1px solid var(--line); border-radius:14px; max-width:380px; width:100%;
           padding:20px; box-shadow:0 12px 40px rgba(0,0,0,.3); }
  .modal h3 { margin:0 0 8px; font-size:16px; }
  .modal p { margin:0 0 16px; color:var(--muted); }
  .modal .actions { display:flex; justify-content:flex-end; gap:10px; }
  .modal .danger { background:var(--alpenglow); border-color:transparent; color:#fff; font-weight:600; }
```

- [ ] **Step 3: Put a `⋮` button in the card header**

In the `station(d)` function, change the `.st-head` template so the reachability bench and a new kebab live in a `.st-tools` group, and drop the old `data-rename` name affordance. Replace the `el.innerHTML = ...` header block:

```javascript
  el.innerHTML =
    `<div class="st-head">
       <div><div class="name">${esc(d.friendly_name)}</div>
         <div class="coords">${esc(d.device_type)} · node ${esc(d.node_id)} / ep ${d.endpoint}${d.transport?`<span class="pill">${esc(d.transport)}</span>`:''}</div></div>
       <div class="st-tools">
         <span class="bench ${d.reachable?'up':''}" title="${d.reachable?'reachable':'unreachable'}"></span>
         <button class="kebab" aria-label="Device actions" aria-haspopup="true">⋮</button>
       </div>
     </div><div class="rows"></div>`;
```

Then, where the old rename listener was (`el.querySelector('[data-rename]').addEventListener('click', ()=>rename(d.friendly_name));`), replace it with the kebab listener:

```javascript
  el.querySelector('.kebab').addEventListener('click', ev=>{
    ev.stopPropagation();
    if (devMenu.classList.contains('open')) { closeMenu(); return; }
    openMenu(ev.currentTarget, d.friendly_name);
  });
```

- [ ] **Step 4: Replace the old `rename()` helper with menu, inline-rename, and modal logic**

Delete the existing `rename(name)` function (the `prompt()`-based one) and replace it with the following block (place it where `rename()` was, after `patch(...)`):

```javascript
// ---- per-device actions: a single body-level menu + modal (the card clips overflow) ----
const devMenu = document.createElement('div');
devMenu.className = 'menu';
devMenu.innerHTML = `<button data-act="rename">✎ Rename</button><button data-act="unpair" class="danger">⌫ Unpair…</button>`;
document.body.appendChild(devMenu);
let menuTarget = null;

function openMenu(btn, name){
  menuTarget = name;
  devMenu.classList.add('open');                       // display first so offsetWidth is measurable
  const r = btn.getBoundingClientRect();
  devMenu.style.top  = (window.scrollY + r.bottom + 4) + 'px';
  devMenu.style.left = (window.scrollX + r.right - devMenu.offsetWidth) + 'px';
}
function closeMenu(){ devMenu.classList.remove('open'); menuTarget = null; }

devMenu.addEventListener('click', e=>{
  const act = e.target.closest('button')?.dataset.act; if(!act) return;
  const name = menuTarget; closeMenu();
  if (act==='rename') startRename(name);
  else if (act==='unpair') openUnpair(name);
});
document.addEventListener('click', e=>{
  if (devMenu.classList.contains('open') && !devMenu.contains(e.target) && !e.target.classList.contains('kebab')) closeMenu();
});

function startRename(name){
  const el = document.getElementById('dev-'+cssId(name)); if(!el) return;
  const nameEl = el.querySelector('.name'); if(!nameEl || nameEl.querySelector('input')) return;
  const input = document.createElement('input');
  input.className = 'name-edit'; input.value = name; input.setAttribute('aria-label','New device name');
  nameEl.replaceChildren(input); input.focus(); input.select();
  let done = false;
  const restore = ()=>{ nameEl.textContent = name; };          // SSE 'devices' will re-render with the real name
  const cancel  = ()=>{ if(done) return; done = true; restore(); };
  const commit  = async ()=>{
    if(done) return; const to = input.value.trim();
    if(!to || to===name){ done = true; restore(); return; }
    done = true;
    try {
      const r = await api('/api/devices/'+encodeURIComponent(name)+'/rename', { method:'POST', body:JSON.stringify({to}) });
      if (r.status===409) toast('That name is already taken');
      else if (r.status===400) toast('That name can’t be used');
      else if (!r.ok) toast('Rename failed');
      else toast('Renamed');
    } catch(e){ if(e.message!=='401') toast('Rename failed'); }
    restore();
  };
  input.addEventListener('keydown', e=>{
    if (e.key==='Enter'){ e.preventDefault(); commit(); }
    else if (e.key==='Escape'){ e.preventDefault(); cancel(); }
  });
  input.addEventListener('blur', cancel);
}

// ---- unpair confirmation modal ----
const backdrop = document.createElement('div');
backdrop.className = 'modal-backdrop';
backdrop.innerHTML =
  `<div class="modal" role="dialog" aria-modal="true" aria-labelledby="unpairTitle">
     <h3 id="unpairTitle">Unpair device?</h3>
     <p id="unpairBody"></p>
     <div class="actions"><button data-act="cancel">Cancel</button><button data-act="confirm" class="danger">Unpair</button></div>
   </div>`;
document.body.appendChild(backdrop);
let unpairTarget = null;

function openUnpair(name){
  unpairTarget = name;
  backdrop.querySelector('#unpairTitle').textContent = 'Unpair “'+name+'”?';
  backdrop.querySelector('#unpairBody').textContent =
    'It will be removed from the Matter fabric and must be re-paired with its setup code to return.';
  backdrop.classList.add('open');
  backdrop.querySelector('[data-act="confirm"]').focus();
}
function closeUnpair(){ backdrop.classList.remove('open'); unpairTarget = null; }

backdrop.addEventListener('click', e=>{
  if (e.target===backdrop){ closeUnpair(); return; }          // backdrop click
  const act = e.target.closest('button')?.dataset.act; if(!act) return;
  if (act==='cancel'){ closeUnpair(); return; }
  if (act==='confirm'){ const name = unpairTarget; closeUnpair(); doUnpair(name); }
});
// keep focus inside the modal while it is open (simple two-button trap)
backdrop.addEventListener('keydown', e=>{
  if (!backdrop.classList.contains('open') || e.key!=='Tab') return;
  const btns = [...backdrop.querySelectorAll('button')];
  const first = btns[0], last = btns[btns.length-1];
  if (e.shiftKey && document.activeElement===first){ e.preventDefault(); last.focus(); }
  else if (!e.shiftKey && document.activeElement===last){ e.preventDefault(); first.focus(); }
});

async function doUnpair(name){
  try {
    const r = await api('/api/devices/'+encodeURIComponent(name), { method:'DELETE' });
    if (r.status===404) toast('Device not found');
    else if (!r.ok) toast('Unpair failed');
    else toast('Unpairing…');   // the card disappears when the node_removed-driven SSE 'devices' refresh arrives
  } catch(e){ if(e.message!=='401') toast('Unpair failed'); }
}

document.addEventListener('keydown', e=>{ if (e.key==='Escape'){ closeMenu(); closeUnpair(); } });
```

- [ ] **Step 5: Manual verification (no automated UI test)**

Per the project's run-via-docker rule, verify against the demo stack:

Run: `docker compose up -d --build`
Then, on **http://localhost:16090**:
1. Click a card's `⋮` → the menu opens below it with Rename and Unpair.
2. **Rename** → the name becomes an inline input; type a new name, press Enter → toast "Renamed", card re-renders with the slugified name. Press Esc on another rename → reverts, no change.
3. **Unpair…** → modal names the device and warns about re-pairing. Cancel/backdrop/Esc → nothing happens. Confirm → toast "Unpairing…" and the card disappears once `node_removed` lands.
4. Because unpair is a real decommission, re-commission that bulb with its setup code (dashboard "Commission" field) to confirm it returns — or use the fake demo stack where you can restart the stack to get the seeded devices back.

Run: `docker compose down` when finished.

- [ ] **Step 6: Commit**

```bash
git add src/Matterhorn/wwwroot/index.html
git commit -m "feat: device card action menu with inline rename and unpair"
```

---

## Self-Review

**Spec coverage:**
- §1 Un-pair = decommission → Tasks 1–2 wire the existing `RemoveNode` decommission to REST; Task 3 surfaces it. ✓
- §2 Backend REST unpair (DELETE, 202/404, `RemoveAccepted`, async accept) → Tasks 1 (`RemoveAccepted`) + 2 (DELETE endpoint + controller mapping). ✓
- §3 `⋮` menu (body-level, single-open, Esc/outside-click close, keyboard) → Task 3 Steps 3–4. ✓
- §4 Inline rename-in-place (Enter/Esc/blur, existing rename endpoint, SSE refresh) → Task 3 Step 4 `startRename`. ✓
- §5 Unpair confirmation modal (names device, warns re-pair, DELETE, 202/404/other handling, focus into modal + trap, Esc/Cancel/backdrop dismiss) → Task 3 Step 4 modal block. ✓
- §6 Visual coherence via frontend-design → Task 3 Step 1 + baseline CSS Step 2. ✓
- §7 Error handling (404, toasts via `api`) → Task 3 `doUnpair`/`startRename`; REST 404 → Task 2. ✓
- Testing section → gateway tests (Task 1), controller tests (Task 2), manual dashboard verify (Task 3). ✓

**Placeholder scan:** No TBD/TODO. Every code step has complete code; the frontend-design step is a real skill invocation with baseline CSS provided, not a placeholder.

**Type consistency:** `RemoveAccepted(bool Found)` and `RemoveRequest(FriendlyName, Transaction)` are used identically in Tasks 1 and 2. JS identifiers (`devMenu`, `openMenu`/`closeMenu`, `menuTarget`, `startRename`, `openUnpair`/`closeUnpair`/`doUnpair`, `unpairTarget`, `.kebab`, `.st-tools`, `.name-edit`, `.menu`, `.modal-backdrop`) are defined once and referenced consistently; the `.kebab` listener in Step 3 references `devMenu`/`openMenu`/`closeMenu` defined in Step 4 (same file, load-time order is fine since listeners fire on click, after the script has fully run).
