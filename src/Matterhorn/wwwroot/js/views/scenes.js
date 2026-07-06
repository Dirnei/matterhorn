// js/views/scenes.js — scene cards: recall, kebab rename/delete, and the capture-scene modal.
import { esc, cssId, toast } from '../ui.js';
import { api } from '../api.js';
import * as store from '../store.js';

let scenesGrid = null;

async function load(){
  let list; try { list = await (await api('/api/scenes')).json(); } catch(e){ return; }
  store.scenes.clear(); list.forEach(s=>store.scenes.set(s.friendly_name,s));
  render();
}

function render(){
  scenesGrid.innerHTML='';
  const list=[...store.scenes.values()].sort((a,b)=>a.friendly_name.localeCompare(b.friendly_name));
  document.getElementById('scenesEmpty').hidden = list.length>0;
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
    const btn = ev.currentTarget;
    if (sceneMenu.classList.contains('open') && sceneMenuBtn===btn){ closeSceneMenu(true); return; }
    openSceneMenu(btn, s.friendly_name);
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
const sceneMenu = document.createElement('div');
sceneMenu.className = 'menu';
sceneMenu.innerHTML = `<button data-act="rename">✎ Rename</button><button data-act="delete" class="danger">⌫ Delete…</button>`;
document.body.appendChild(sceneMenu);
let sceneMenuTarget = null, sceneMenuBtn = null;

function openSceneMenu(btn, name){
  sceneMenuTarget = name;
  sceneMenuBtn?.setAttribute('aria-expanded','false');
  sceneMenuBtn = btn; btn.setAttribute('aria-expanded','true');
  sceneMenu.classList.add('open');
  const r = btn.getBoundingClientRect();
  sceneMenu.style.top  = (window.scrollY + r.bottom + 4) + 'px';
  sceneMenu.style.left = (window.scrollX + r.right - sceneMenu.offsetWidth) + 'px';
  sceneMenu.querySelector('button').focus();
}
function closeSceneMenu(returnFocus){
  if (!sceneMenu.classList.contains('open')) return;
  sceneMenu.classList.remove('open'); sceneMenuTarget = null;
  sceneMenuBtn?.setAttribute('aria-expanded','false');
  if (returnFocus) sceneMenuBtn?.focus();
}
sceneMenu.addEventListener('click', e=>{
  const act = e.target.closest('button')?.dataset.act; if(!act) return;
  const name = sceneMenuTarget, origin = sceneMenuBtn; closeSceneMenu();
  if (act==='rename') startSceneRename(name);
  else if (act==='delete') openSceneDelete(name, origin);
});
sceneMenu.addEventListener('keydown', e=>{
  const items = [...sceneMenu.querySelectorAll('button')];
  const i = items.indexOf(document.activeElement);
  if (e.key==='ArrowDown'){ e.preventDefault(); items[(i+1)%items.length].focus(); }
  else if (e.key==='ArrowUp'){ e.preventDefault(); items[(i-1+items.length)%items.length].focus(); }
  else if (e.key==='Escape'){ e.preventDefault(); closeSceneMenu(true); }
});
document.addEventListener('click', e=>{
  if (sceneMenu.classList.contains('open') && !sceneMenu.contains(e.target)) closeSceneMenu();
});
document.addEventListener('keydown', e=>{ if (e.key==='Escape'){ closeSceneMenu(true); closeSceneDelete(true); closeCapture(true); } });

function startSceneRename(name){
  const el = document.getElementById('scn-'+cssId(name)); if(!el) return;
  const nameEl = el.querySelector('.name'); if(!nameEl || nameEl.querySelector('input')) return;
  const input = document.createElement('input');
  input.className = 'name-edit'; input.value = name; input.setAttribute('aria-label','New scene name');
  nameEl.replaceChildren(input); input.focus(); input.select();
  let done = false;
  const restore = ()=>{ nameEl.textContent = name; };          // SSE 'scenes' will re-render with the real name
  const cancel  = ()=>{ if(done) return; done = true; restore(); };
  const commit  = async ()=>{
    if(done) return; const to = input.value.trim();
    if(!to || to===name){ done = true; restore(); return; }
    done = true;
    try {
      const r = await api('/api/scenes/'+encodeURIComponent(name)+'/rename', { method:'POST', body:JSON.stringify({to}) });
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

// ---- scene delete confirmation modal ----
const sceneBackdrop = document.createElement('div');
sceneBackdrop.className = 'modal-backdrop';
sceneBackdrop.innerHTML =
  `<div class="modal" role="dialog" aria-modal="true" aria-labelledby="sceneDeleteTitle">
     <h3 id="sceneDeleteTitle">Delete scene?</h3>
     <p id="sceneDeleteBody"></p>
     <div class="actions"><button data-act="cancel">Cancel</button><button data-act="confirm" class="danger">Delete</button></div>
   </div>`;
document.body.appendChild(sceneBackdrop);
let sceneDeleteTarget = null, sceneDeleteBtn = null;

function openSceneDelete(name, origin){
  sceneDeleteTarget = name; sceneDeleteBtn = origin || null;
  sceneBackdrop.querySelector('#sceneDeleteTitle').textContent = 'Delete “'+name+'”?';
  sceneBackdrop.querySelector('#sceneDeleteBody').textContent =
    'The scene and its snapshot will be removed. Member devices are not affected.';
  sceneBackdrop.classList.add('open');
  sceneBackdrop.querySelector('[data-act="confirm"]').focus();
}
function closeSceneDelete(returnFocus){
  if (!sceneBackdrop.classList.contains('open')) return;
  sceneBackdrop.classList.remove('open'); sceneDeleteTarget = null;
  if (returnFocus) sceneDeleteBtn?.focus();
  sceneDeleteBtn = null;
}
sceneBackdrop.addEventListener('click', e=>{
  if (e.target===sceneBackdrop){ closeSceneDelete(true); return; }
  const act = e.target.closest('button')?.dataset.act; if(!act) return;
  if (act==='cancel'){ closeSceneDelete(true); return; }
  if (act==='confirm'){ const name = sceneDeleteTarget; closeSceneDelete(); doDeleteScene(name); }
});
sceneBackdrop.addEventListener('keydown', e=>{
  if (!sceneBackdrop.classList.contains('open') || e.key!=='Tab') return;
  const btns = [...sceneBackdrop.querySelectorAll('button')];
  const first = btns[0], last = btns[btns.length-1];
  if (e.shiftKey && document.activeElement===first){ e.preventDefault(); last.focus(); }
  else if (!e.shiftKey && document.activeElement===last){ e.preventDefault(); first.focus(); }
});

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
document.getElementById('captureSceneBtn').addEventListener('click', openCapture);

export function mount(container){
  scenesGrid = container || document.getElementById('scenesGrid');
  return load();
}

export function unmount(){
  scenesGrid = null;
}

export function onFrame(msg){
  if (msg.type==='scenes'){ load(); }
}
