// js/views/groups.js — group cards: master switch, member add/remove, kebab rename/delete, create form.
import { esc, cssId, toast, kebabMenu, confirmModal, inlineRename, createPickerModal, icon } from '../ui.js';
import { api } from '../api.js';
import * as store from '../store.js';

let groupsGrid = null, emptyEl = null;
let mounted = false;
const expanded = new Set();   // friendly_names whose member-management region is expanded (survives re-render)

// Cache update (store.groups) is unconditional — only the DOM rebuild is guarded on `mounted`.
async function load(){
  let list; try { list = await (await api('/api/groups')).json(); } catch(e){ return; }
  store.groups.clear(); list.forEach(g=>store.groups.set(g.friendly_name,g));
  if (mounted) render();
}

function render(){
  if (!mounted) return;
  groupsGrid.innerHTML='';
  const list=[...store.groups.values()].sort((a,b)=>a.friendly_name.localeCompare(b.friendly_name));
  emptyEl.hidden = list.length>0;
  for (const g of list) groupsGrid.appendChild(groupCard(g));
  list.forEach(g=>applyGroupState(g.friendly_name));
}

function groupCard(g){
  const el=document.createElement('section');
  el.className='group-strip'; el.id='grp-'+cssId(g.friendly_name);
  const members=g.members||[];
  const isOpen=expanded.has(g.friendly_name);
  if (isOpen) el.classList.add('open');
  el.innerHTML =
    `<div class="gs-row" role="button" tabindex="0" aria-expanded="${isOpen}">
       <svg class="gs-glyph" viewBox="0 0 24 24" aria-hidden="true">
         <circle cx="9" cy="12" r="6" fill="none" stroke="currentColor" stroke-width="1.6"/>
         <circle cx="15" cy="12" r="6" fill="none" stroke="currentColor" stroke-width="1.6"/>
       </svg>
       <div class="gs-main">
         <div class="name">${esc(g.friendly_name)}</div>
         <div class="gs-sub">${members.length ? members.map(esc).join(' · ') : 'no devices'}</div>
       </div>
       <div class="gs-tools">
         <span class="switch"><input type="checkbox" data-prop="state"><span class="slot"></span><span class="knob"></span></span>
         <button class="kebab" aria-label="Group actions" aria-haspopup="true" aria-expanded="false">${icon.kebab}</button>
       </div>
       <span class="gs-chevron" aria-hidden="true">
         <svg viewBox="0 0 24 24" width="14" height="14"><path d="M7 10l5 5 5-5" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/></svg>
       </span>
     </div>
     <div class="expand"${isOpen?'':' hidden'}>
       <div class="exp-head legend">members</div>
       <div class="member-rows">${members.length
          ? members.map(m=>memberRow(m)).join('')
          : '<div class="member-empty legend">no members yet</div>'}</div>
       <button class="add-devices" type="button">
         <svg viewBox="0 0 14 14" width="13" height="13" aria-hidden="true"><path d="M7 3v8M3 7h8" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"/></svg>
         Add devices
       </button>
     </div>`;
  const row=el.querySelector('.gs-row');
  const tools=el.querySelector('.gs-tools');
  tools.addEventListener('click', ev=>ev.stopPropagation());   // switch + kebab never toggle the row
  row.addEventListener('click', ()=>toggleExpand(g.friendly_name, el, row));
  row.addEventListener('keydown', ev=>{
    if (ev.key==='Enter' || ev.key===' '){ ev.preventDefault(); toggleExpand(g.friendly_name, el, row); }
  });
  el.querySelector('input[type=checkbox]').addEventListener('change',ev=>
    patchGroup(g.friendly_name,{state:ev.target.checked?'ON':'OFF'}));
  el.querySelector('.kebab').addEventListener('click', ev=>{
    groupMenu.open(ev.currentTarget, g.friendly_name);
  });
  el.querySelectorAll('.m-remove').forEach(b=>b.addEventListener('click', async ()=>{
    const device=b.dataset.remove;
    try {
      const r=await api('/api/groups/'+encodeURIComponent(g.friendly_name)+'/members/'+encodeURIComponent(device),{method:'DELETE'});
      if(!r.ok) toast('Remove failed'); else load();
    } catch(e){ if(e.message!=='401') toast('Remove failed'); }
  }));
  el.querySelector('.add-devices').addEventListener('click', ev=>{
    addDevicesModal.open(ev.currentTarget, {
      exclude: new Set(g.members||[]),
      onSubmit: (_n, devices)=>addMembers(g.friendly_name, devices),
    });
  });
  return el;
}

// One member row inside the expanded group: live state dot + name + state text + remove.
// State is read from the warm device cache; the dot reflects the last render (refreshes on group reload).
function memberRow(m){
  const s=store.deviceState.get(m)||{};
  const has=s.state!=null;
  const on=String(s.state||'').toUpperCase()==='ON';
  const stateTxt = !has ? '' : (on ? (s.brightness!=null ? `on · ${Math.round(s.brightness/254*100)}%` : 'on') : 'off');
  return `<div class="member-row">
      <span class="m-dot${has?(on?' on':' off'):''}"></span>
      <span class="m-name">${esc(m)}</span>
      <span class="m-state">${stateTxt}</span>
      <button class="m-remove" data-remove="${esc(m)}" aria-label="Remove ${esc(m)}">${icon.close}</button>
    </div>`;
}

// Add the picked devices to the group (one PUT each); the SSE 'groups' frame + load() refresh the strip.
async function addMembers(name, devices){
  if (!devices.length) return;
  try {
    for (const d of devices){
      const r=await api('/api/groups/'+encodeURIComponent(name)+'/members/'+encodeURIComponent(d),{method:'PUT'});
      if(!r.ok){ toast('Add failed'); return; }
    }
    load();
  } catch(e){ if(e.message!=='401') toast('Add failed'); }
}

// Expanded/collapsed state is kept in module-level `expanded` (not per-DOM-node) so it survives
// the full-list `render()` rebuild that follows every SSE-driven `load()`.
function toggleExpand(name, el, row){
  const nowOpen=!expanded.has(name);
  if (nowOpen) expanded.add(name); else expanded.delete(name);
  el.classList.toggle('open', nowOpen);
  row.setAttribute('aria-expanded', String(nowOpen));
  el.querySelector('.expand').hidden = !nowOpen;
}

function patchGroup(name, body){
  return api('/api/groups/'+encodeURIComponent(name), { method:'PATCH', body:JSON.stringify(body) })
    .catch(e=>{ if(e.message!=='401') toast('Set failed'); });
}

// Group state echoes arrive on the same SSE 'state' frame as devices (device === group name).
function applyGroupState(name){
  if (!mounted) return;
  if (!store.groups.has(name)) return;
  const el=document.getElementById('grp-'+cssId(name)); if(!el) return;
  const s=store.deviceState.get(name)||{};
  const sw=el.querySelector('input[type=checkbox][data-prop="state"]');
  if (sw && s.state!=null) sw.checked=String(s.state).toUpperCase()==='ON';
}

// ---- group kebab menu (rename / delete) ----
const groupDeleteModal = confirmModal({
  title: name => `Delete “${name}”?`,
  body: 'The group and its member list will be removed. Member devices are not affected.',
  confirmLabel: 'Delete',
  danger: true,
  onConfirm: doDeleteGroup,
});

function startGroupRename(name){
  const el = document.getElementById('grp-'+cssId(name)); if(!el) return;
  const nameEl = el.querySelector('.name'); if(!nameEl) return;
  inlineRename(nameEl, name, async (to)=>{
    try {
      const r = await api('/api/groups/'+encodeURIComponent(name)+'/rename', { method:'POST', body:JSON.stringify({to}) });
      if (r.status===409) toast('That name is already taken');
      else if (r.status===400) toast('That name can’t be used');
      else if (!r.ok) toast('Rename failed');
      else {
        if (expanded.delete(name)) expanded.add(to);   // keep expand state under the new name
        toast('Renamed'); load();
      }
    } catch(e){ if(e.message!=='401') toast('Rename failed'); }
  }, 'New group name');
}

const groupMenu = kebabMenu([
  { label:'Rename', icon: icon.rename, onClick: name => startGroupRename(name) },
  { label:'Delete…', icon: icon.trash, danger:true, onClick: (name, origin) => groupDeleteModal.open(origin, name) },
]);

// ---- new-group picker (name + searchable device checklist, defaults to controllable devices) ----
const newGroupModal = createPickerModal({
  title: 'New group',
  submitLabel: 'Create',
  get initialName(){ return store.suggestName('group', new Set(store.groups.keys())); },
  onSubmit: (name, members) => createGroup(name, members),
});

// "Add devices" (from an expanded group) reuses the picker in add-mode: no name field, current
// members excluded; exclude + onSubmit are supplied per-open with the group's context.
const addDevicesModal = createPickerModal({ title: 'Add devices', submitLabel: 'Add', hideName: true });

async function doDeleteGroup(name){
  try {
    const r = await api('/api/groups/'+encodeURIComponent(name), { method:'DELETE' });
    if (r.status===404) toast('Group not found');
    else if (!r.ok) toast('Delete failed');
    else { expanded.delete(name); toast('Deleting…'); load(); }
  } catch(e){ if(e.message!=='401') toast('Delete failed'); }
}

async function createGroup(name, members){
  if (!name) return;
  try {
    const r = await api('/api/groups/'+encodeURIComponent(name), { method:'PUT', body:JSON.stringify({members}) });
    if (r.status===409) toast('That name is already taken');
    else if (r.status===400) toast('That name can’t be used');
    else if (!r.ok) toast('Could not create group');
    else toast('Group created');   // success path: SSE 'groups' frame refreshes the list
  } catch(e){ if(e.message!=='401') toast('Could not create group'); }
}

export function mount(container){
  container.innerHTML =
    `<div class="section-head">
       <span class="legend">Groups</span>
       <button class="go" id="newGroupBtn" type="button">New group</button>
     </div>
     <div class="groups-list" id="groupsGrid"></div>
     <div class="empty" id="groupsEmpty" hidden>No groups yet. Create one above.</div>`;
  groupsGrid = container.querySelector('#groupsGrid');
  emptyEl = container.querySelector('#groupsEmpty');
  container.querySelector('#newGroupBtn').addEventListener('click', ev=>{
    newGroupModal.open(ev.currentTarget);
  });
  mounted = true;
  return load();
}

export function unmount(){
  mounted = false;
  groupMenu.close();     // drop this view's open kebab dropdown, if any
  groupDeleteModal.close(); // drop the delete confirm modal, if any
  groupsGrid = null; emptyEl = null;
}

export function onFrame(msg){
  // Cache update (store.groups) is unconditional; applyGroupState guards its own DOM writes on `mounted`.
  if (msg.type==='groups'){ return load(); }
  else if (msg.type==='devices'){ return load(); }  // Reload groups when devices change (e.g. after commissioning/renaming refreshes "add device" dropdown)
  else if (msg.type==='state'){ applyGroupState(msg.device); }
}
