// js/views/groups.js — group cards: master switch, member add/remove, kebab rename/delete, create form.
import { esc, cssId, toast } from '../ui.js';
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
    const btn = ev.currentTarget;
    if (groupMenu.classList.contains('open') && groupMenuBtn===btn){ closeGroupMenu(true); return; }
    openGroupMenu(btn, g.friendly_name);
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
const groupMenu = document.createElement('div');
groupMenu.className = 'menu';
groupMenu.innerHTML = `<button data-act="rename">✎ Rename</button><button data-act="delete" class="danger">⌫ Delete…</button>`;
document.body.appendChild(groupMenu);
let groupMenuTarget = null, groupMenuBtn = null;

function openGroupMenu(btn, name){
  groupMenuTarget = name;
  groupMenuBtn?.setAttribute('aria-expanded','false');
  groupMenuBtn = btn; btn.setAttribute('aria-expanded','true');
  groupMenu.classList.add('open');
  const r = btn.getBoundingClientRect();
  groupMenu.style.top  = (window.scrollY + r.bottom + 4) + 'px';
  groupMenu.style.left = (window.scrollX + r.right - groupMenu.offsetWidth) + 'px';
  groupMenu.querySelector('button').focus();
}
function closeGroupMenu(returnFocus){
  if (!groupMenu.classList.contains('open')) return;
  groupMenu.classList.remove('open'); groupMenuTarget = null;
  groupMenuBtn?.setAttribute('aria-expanded','false');
  if (returnFocus) groupMenuBtn?.focus();
}
groupMenu.addEventListener('click', e=>{
  const act = e.target.closest('button')?.dataset.act; if(!act) return;
  const name = groupMenuTarget, origin = groupMenuBtn; closeGroupMenu();
  if (act==='rename') startGroupRename(name);
  else if (act==='delete') openGroupDelete(name, origin);
});
groupMenu.addEventListener('keydown', e=>{
  const items = [...groupMenu.querySelectorAll('button')];
  const i = items.indexOf(document.activeElement);
  if (e.key==='ArrowDown'){ e.preventDefault(); items[(i+1)%items.length].focus(); }
  else if (e.key==='ArrowUp'){ e.preventDefault(); items[(i-1+items.length)%items.length].focus(); }
  else if (e.key==='Escape'){ e.preventDefault(); closeGroupMenu(true); }
});
document.addEventListener('click', e=>{
  if (groupMenu.classList.contains('open') && !groupMenu.contains(e.target)) closeGroupMenu();
});
document.addEventListener('keydown', e=>{ if (e.key==='Escape'){ closeGroupMenu(true); closeGroupDelete(true); } });

function startGroupRename(name){
  const el = document.getElementById('grp-'+cssId(name)); if(!el) return;
  const nameEl = el.querySelector('.name'); if(!nameEl || nameEl.querySelector('input')) return;
  const input = document.createElement('input');
  input.className = 'name-edit'; input.value = name; input.setAttribute('aria-label','New group name');
  nameEl.replaceChildren(input); input.focus(); input.select();
  let done = false;
  const restore = ()=>{ nameEl.textContent = name; };          // SSE 'groups' will re-render with the real name
  const cancel  = ()=>{ if(done) return; done = true; restore(); };
  const commit  = async ()=>{
    if(done) return; const to = input.value.trim();
    if(!to || to===name){ done = true; restore(); return; }
    done = true;
    try {
      const r = await api('/api/groups/'+encodeURIComponent(name)+'/rename', { method:'POST', body:JSON.stringify({to}) });
      if (r.status===409) toast('That name is already taken');
      else if (r.status===400) toast('That name can’t be used');
      else if (!r.ok) toast('Rename failed');
      else { toast('Renamed'); load(); }
    } catch(e){ if(e.message!=='401') toast('Rename failed'); }
    restore();
  };
  input.addEventListener('keydown', e=>{
    if (e.key==='Enter'){ e.preventDefault(); commit(); }
    else if (e.key==='Escape'){ e.preventDefault(); cancel(); }
  });
  input.addEventListener('blur', cancel);
}

// ---- group delete confirmation modal ----
const groupBackdrop = document.createElement('div');
groupBackdrop.className = 'modal-backdrop';
groupBackdrop.innerHTML =
  `<div class="modal" role="dialog" aria-modal="true" aria-labelledby="groupDeleteTitle">
     <h3 id="groupDeleteTitle">Delete group?</h3>
     <p id="groupDeleteBody"></p>
     <div class="actions"><button data-act="cancel">Cancel</button><button data-act="confirm" class="danger">Delete</button></div>
   </div>`;
document.body.appendChild(groupBackdrop);
let groupDeleteTarget = null, groupDeleteBtn = null;

function openGroupDelete(name, origin){
  groupDeleteTarget = name; groupDeleteBtn = origin || null;
  groupBackdrop.querySelector('#groupDeleteTitle').textContent = 'Delete “'+name+'”?';
  groupBackdrop.querySelector('#groupDeleteBody').textContent =
    'The group and its member list will be removed. Member devices are not affected.';
  groupBackdrop.classList.add('open');
  groupBackdrop.querySelector('[data-act="confirm"]').focus();
}
function closeGroupDelete(returnFocus){
  if (!groupBackdrop.classList.contains('open')) return;
  groupBackdrop.classList.remove('open'); groupDeleteTarget = null;
  if (returnFocus) groupDeleteBtn?.focus();
  groupDeleteBtn = null;
}
groupBackdrop.addEventListener('click', e=>{
  if (e.target===groupBackdrop){ closeGroupDelete(true); return; }
  const act = e.target.closest('button')?.dataset.act; if(!act) return;
  if (act==='cancel'){ closeGroupDelete(true); return; }
  if (act==='confirm'){ const name = groupDeleteTarget; closeGroupDelete(); doDeleteGroup(name); }
});
groupBackdrop.addEventListener('keydown', e=>{
  if (!groupBackdrop.classList.contains('open') || e.key!=='Tab') return;
  const btns = [...groupBackdrop.querySelectorAll('button')];
  const first = btns[0], last = btns[btns.length-1];
  if (e.shiftKey && document.activeElement===first){ e.preventDefault(); last.focus(); }
  else if (!e.shiftKey && document.activeElement===last){ e.preventDefault(); first.focus(); }
});

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
  else if (msg.type==='state'){ applyGroupState(msg.device); }
}
