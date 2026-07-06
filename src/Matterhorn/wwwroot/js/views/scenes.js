// js/views/scenes.js — scene cards: recall, kebab rename/delete, and the capture-scene modal.
import { esc, cssId, toast, kebabMenu, confirmModal, inlineRename } from '../ui.js';
import { api } from '../api.js';
import * as store from '../store.js';

let scenesGrid = null, emptyEl = null;
let mounted = false;

// Cache update (store.scenes) is unconditional — only the DOM rebuild is guarded on `mounted`.
async function load(){
  let list; try { list = await (await api('/api/scenes')).json(); } catch(e){ return; }
  store.scenes.clear(); list.forEach(s=>store.scenes.set(s.friendly_name,s));
  if (mounted) render();
}

function render(){
  if (!mounted) return;
  scenesGrid.innerHTML='';
  const list=[...store.scenes.values()].sort((a,b)=>a.friendly_name.localeCompare(b.friendly_name));
  emptyEl.hidden = list.length>0;
  for (const s of list) scenesGrid.appendChild(sceneCard(s));
}

function sceneCard(s){
  const el=document.createElement('section');
  el.className='station scene'; el.id='scn-'+cssId(s.friendly_name);
  const members=s.members||[];
  el.innerHTML =
    `<div class="st-head">
       <div><div class="name">${esc(s.friendly_name)}</div>
         <div class="coords">${members.length} member${members.length===1?'':'s'}<span class="pill">scene</span></div></div>
       <div class="st-tools">
         <button class="go" data-act="recall">Recall</button>
         <button class="kebab" aria-label="Scene actions" aria-haspopup="true" aria-expanded="false">⋮</button>
       </div>
     </div>
     <div class="rows">
       <div class="row chips">${members.length
          ? members.map(m=>`<span class="chip">${esc(m)}</span>`).join('')
          : '<span class="legend">no members</span>'}</div>
     </div>`;
  el.querySelector('[data-act="recall"]').addEventListener('click', ()=>doRecallScene(s.friendly_name));
  el.querySelector('.kebab').addEventListener('click', ev=>{
    ev.stopPropagation();
    sceneMenu.open(ev.currentTarget, s.friendly_name);
  });
  return el;
}

async function doRecallScene(name){
  try {
    const r = await api('/api/scenes/'+encodeURIComponent(name)+'/recall', { method:'POST' });
    if (r.status===404) toast('Scene not found');
    else if (!r.ok) toast('Recall failed');
    else toast('Recalled “'+name+'”');
  } catch(e){ if(e.message!=='401') toast('Recall failed'); }
}

// ---- scene kebab menu (rename / delete) ----
const sceneDeleteModal = confirmModal({
  title: name => `Delete “${name}”?`,
  body: 'The scene and its snapshot will be removed. Member devices are not affected.',
  confirmLabel: 'Delete',
  danger: true,
  onConfirm: doDeleteScene,
});

function startSceneRename(name){
  const el = document.getElementById('scn-'+cssId(name)); if(!el) return;
  const nameEl = el.querySelector('.name'); if(!nameEl) return;
  inlineRename(nameEl, name, async (to)=>{
    try {
      const r = await api('/api/scenes/'+encodeURIComponent(name)+'/rename', { method:'POST', body:JSON.stringify({to}) });
      if (r.status===409) toast('That name is already taken');
      else if (r.status===400) toast('That name can’t be used');
      else if (!r.ok) toast('Rename failed');
      else { toast('Renamed'); load(); }
    } catch(e){ if(e.message!=='401') toast('Rename failed'); }
  }, 'New scene name');
}

const sceneMenu = kebabMenu([
  { label:'✎ Rename', onClick: name => startSceneRename(name) },
  { label:'⌫ Delete…', danger:true, onClick: (name, origin) => sceneDeleteModal.open(origin, name) },
]);

document.addEventListener('keydown', e=>{ if (e.key==='Escape') closeCapture(true); });

async function doDeleteScene(name){
  try {
    const r = await api('/api/scenes/'+encodeURIComponent(name), { method:'DELETE' });
    if (r.status===404) toast('Scene not found');
    else if (!r.ok) toast('Delete failed');
    else { toast('Deleting…'); load(); }
  } catch(e){ if(e.message!=='401') toast('Delete failed'); }
}

// ---- capture-scene modal (name + device multi-select, default all checked) ----
const captureBackdrop = document.createElement('div');
captureBackdrop.className = 'modal-backdrop';
captureBackdrop.innerHTML =
  `<div class="modal" role="dialog" aria-modal="true" aria-labelledby="captureTitle">
     <h3 id="captureTitle">Capture scene</h3>
     <form id="captureForm">
       <input id="captureName" class="name-edit" placeholder="scene name" autocomplete="off" />
       <div class="capture-members" id="captureMembers"></div>
       <div class="actions"><button type="button" data-act="cancel">Cancel</button><button class="go" type="submit">Capture</button></div>
     </form>
   </div>`;
document.body.appendChild(captureBackdrop);

function openCapture(){
  const name=document.getElementById('captureName'); name.value='';
  const members=document.getElementById('captureMembers');
  const list=[...store.devices.keys()].sort((a,b)=>a.localeCompare(b));
  members.innerHTML = list.length
    ? list.map(n=>`<label><input type="checkbox" value="${esc(n)}" checked> ${esc(n)}</label>`).join('')
    : '<span class="legend">no devices to capture</span>';
  captureBackdrop.classList.add('open');
  name.focus();
}
function closeCapture(returnFocus){
  if (!captureBackdrop.classList.contains('open')) return;
  captureBackdrop.classList.remove('open');
  if (returnFocus) document.getElementById('captureSceneBtn')?.focus();
}
captureBackdrop.addEventListener('click', e=>{
  if (e.target===captureBackdrop){ closeCapture(true); return; }
  if (e.target.closest('button')?.dataset.act==='cancel'){ closeCapture(true); }
});
captureBackdrop.addEventListener('keydown', e=>{
  if (!captureBackdrop.classList.contains('open') || e.key!=='Tab') return;
  const els = [...captureBackdrop.querySelectorAll('input, button')];
  const first = els[0], last = els[els.length-1];
  if (e.shiftKey && document.activeElement===first){ e.preventDefault(); last.focus(); }
  else if (!e.shiftKey && document.activeElement===last){ e.preventDefault(); first.focus(); }
});
document.getElementById('captureForm').addEventListener('submit', async ev=>{
  ev.preventDefault();
  const name=document.getElementById('captureName').value.trim(); if(!name) return;
  const devices=[...document.querySelectorAll('#captureMembers input[type=checkbox]:checked')].map(c=>c.value);
  try {
    const r = await api('/api/scenes/'+encodeURIComponent(name), { method:'PUT', body:JSON.stringify({devices}) });
    if (!r.ok) toast('Could not capture scene');
    else { toast('Scene captured'); closeCapture(); load(); }
  } catch(e){ if(e.message!=='401') toast('Could not capture scene'); }
});

export function mount(container){
  container.innerHTML =
    `<div class="section-head">
       <span class="legend">Scenes</span>
       <button class="go" type="button" id="captureSceneBtn">Capture scene</button>
     </div>
     <div class="scenes-grid" id="scenesGrid"></div>
     <div class="empty" id="scenesEmpty" hidden>No scenes yet. Capture one above.</div>`;
  scenesGrid = container.querySelector('#scenesGrid');
  emptyEl = container.querySelector('#scenesEmpty');
  container.querySelector('#captureSceneBtn').addEventListener('click', openCapture);
  mounted = true;
  return load();
}

export function unmount(){
  mounted = false;
  sceneMenu.close();     // drop this view's open kebab dropdown, if any
  closeCapture(false);   // and its capture-scene modal, if open
  scenesGrid = null; emptyEl = null;
}

export function onFrame(msg){
  // Cache update (store.scenes) is unconditional; render() (called from load) guards DOM writes on `mounted`.
  if (msg.type==='scenes'){ return load(); }
}
