// js/views/groups.js — group cards: master switch, member add/remove, kebab rename/delete, create form.
import { esc, cssId, toast, kebabMenu, confirmModal, inlineRename } from '../ui.js';
import { api } from '../api.js';
import * as store from '../store.js';

let groupsGrid = null;

async function load(){
  let list; try { list = await (await api('/api/groups')).json(); } catch(e){ return; }
  store.groups.clear(); list.forEach(g=>store.groups.set(g.friendly_name,g));
  render();
}

function render(){
  groupsGrid.innerHTML='';
  const list=[...store.groups.values()].sort((a,b)=>a.friendly_name.localeCompare(b.friendly_name));
  document.getElementById('groupsEmpty').hidden = list.length>0;
  for (const g of list) groupsGrid.appendChild(groupCard(g));
  list.forEach(g=>applyGroupState(g.friendly_name));
}

function groupCard(g){
  const el=document.createElement('section');
  el.className='station group'; el.id='grp-'+cssId(g.friendly_name);
  const members=g.members||[];
  const available=[...store.devices.keys()].filter(n=>!members.includes(n));
  el.innerHTML =
    `<div class="st-head">
       <div><div class="name">${esc(g.friendly_name)}</div>
         <div class="coords">${members.length} member${members.length===1?'':'s'}<span class="pill">group</span></div></div>
       <div class="st-tools">
         <span class="switch"><input type="checkbox" data-prop="state"><span class="slot"></span><span class="knob"></span></span>
         <button class="kebab" aria-label="Group actions" aria-haspopup="true" aria-expanded="false">⋮</button>
       </div>
     </div>
     <div class="rows">
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
  el.querySelector('input[type=checkbox]').addEventListener('change',ev=>
    patchGroup(g.friendly_name,{state:ev.target.checked?'ON':'OFF'}));
  el.querySelector('.kebab').addEventListener('click', ev=>{
    ev.stopPropagation();
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

function patchGroup(name, body){
  return api('/api/groups/'+encodeURIComponent(name), { method:'PATCH', body:JSON.stringify(body) })
    .catch(e=>{ if(e.message!=='401') toast('Set failed'); });
}

// Group state echoes arrive on the same SSE 'state' frame as devices (device === group name).
function applyGroupState(name){
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

document.getElementById('newGroupForm').addEventListener('submit', async ev=>{
  ev.preventDefault();
  const input=document.getElementById('newGroupName');
  const name=input.value.trim(); if(!name) return;
  try {
    const r = await api('/api/groups/'+encodeURIComponent(name), { method:'PUT', body:JSON.stringify({members:[]}) });
    if (r.status===409) toast('That name is already taken');
    else if (!r.ok) toast('Could not create group');
    else { toast('Group created'); input.value=''; load(); }
  } catch(e){ if(e.message!=='401') toast('Could not create group'); }
});

export function mount(container){
  groupsGrid = container || document.getElementById('groupsGrid');
  return load();
}

export function unmount(){
  groupsGrid = null;
}

export function onFrame(msg){
  if (msg.type==='groups'){ load(); }
  else if (msg.type==='devices'){ load(); }  // Reload groups when devices change (e.g. after commissioning/renaming refreshes "add device" dropdown)
  else if (msg.type==='state'){ applyGroupState(msg.device); }
}
