// js/views/groups.js — group cards: master switch, member add/remove, kebab rename/delete, create form.
import { esc, cssId, toast, kebabMenu, confirmModal, inlineRename } from '../ui.js';
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
  const available=[...store.devices.keys()].filter(n=>!members.includes(n));
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
         <button class="kebab" aria-label="Group actions" aria-haspopup="true" aria-expanded="false">⋮</button>
       </div>
     </div>
     <div class="expand"${isOpen?'':' hidden'}>
       <div class="row chips">${members.length
          ? members.map(m=>`<span class="chip">${esc(m)}<button data-remove="${esc(m)}" aria-label="Remove ${esc(m)}">✕</button></span>`).join('')
          : '<span class="legend">no members</span>'}</div>
       <div class="row add-member">
         <select aria-label="Add device to group">
           <option value="">＋ add device…</option>
           ${available.map(n=>`<option value="${esc(n)}">${esc(n)}</option>`).join('')}
         </select>
       </div>
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
  el.querySelectorAll('.chip button').forEach(b=>b.addEventListener('click', async ()=>{
    const device=b.dataset.remove;
    try {
      const r=await api('/api/groups/'+encodeURIComponent(g.friendly_name)+'/members/'+encodeURIComponent(device),{method:'DELETE'});
      if(!r.ok) toast('Remove failed'); else load();
    } catch(e){ if(e.message!=='401') toast('Remove failed'); }
  }));
  const sel=el.querySelector('select');
  sel.addEventListener('change', async ()=>{
    const device=sel.value; if(!device) return;
    try {
      const r=await api('/api/groups/'+encodeURIComponent(g.friendly_name)+'/members/'+encodeURIComponent(device),{method:'PUT'});
      if(!r.ok) toast('Add failed'); else load();
    } catch(e){ if(e.message!=='401') toast('Add failed'); }
  });
  return el;
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
      else { toast('Renamed'); load(); }
    } catch(e){ if(e.message!=='401') toast('Rename failed'); }
  }, 'New group name');
}

const groupMenu = kebabMenu([
  { label:'✎ Rename', onClick: name => startGroupRename(name) },
  { label:'⌫ Delete…', danger:true, onClick: (name, origin) => groupDeleteModal.open(origin, name) },
]);

async function doDeleteGroup(name){
  try {
    const r = await api('/api/groups/'+encodeURIComponent(name), { method:'DELETE' });
    if (r.status===404) toast('Group not found');
    else if (!r.ok) toast('Delete failed');
    else { toast('Deleting…'); load(); }
  } catch(e){ if(e.message!=='401') toast('Delete failed'); }
}

export function mount(container){
  container.innerHTML =
    `<div class="section-head">
       <span class="legend">Groups</span>
       <form class="newgroup" id="newGroupForm">
         <input id="newGroupName" placeholder="group name" autocomplete="off" />
         <button class="go" type="submit">New group</button>
       </form>
     </div>
     <div class="groups-list" id="groupsGrid"></div>
     <div class="empty" id="groupsEmpty" hidden>No groups yet. Create one above.</div>`;
  groupsGrid = container.querySelector('#groupsGrid');
  emptyEl = container.querySelector('#groupsEmpty');
  container.querySelector('#newGroupForm').addEventListener('submit', async ev=>{
    ev.preventDefault();
    const input=container.querySelector('#newGroupName');
    const name=input.value.trim(); if(!name) return;
    try {
      const r = await api('/api/groups/'+encodeURIComponent(name), { method:'PUT', body:JSON.stringify({members:[]}) });
      if (r.status===409) toast('That name is already taken');
      else if (!r.ok) toast('Could not create group');
      else { toast('Group created'); input.value=''; load(); }
    } catch(e){ if(e.message!=='401') toast('Could not create group'); }
  });
  mounted = true;
  return load();
}

export function unmount(){
  mounted = false;
  groupMenu.close();     // drop this view's open kebab dropdown, if any
  groupsGrid = null; emptyEl = null;
}

export function onFrame(msg){
  // Cache update (store.groups) is unconditional; applyGroupState guards its own DOM writes on `mounted`.
  if (msg.type==='groups'){ return load(); }
  else if (msg.type==='devices'){ return load(); }  // Reload groups when devices change (e.g. after commissioning/renaming refreshes "add device" dropdown)
  else if (msg.type==='state'){ applyGroupState(msg.device); }
}
