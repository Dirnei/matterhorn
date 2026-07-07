// js/views/devices.js — device stations grid: render, controls, color wheel, menu/rename/unpair.
import { esc, cssId, fmt, toast, kebabMenu, confirmModal, inlineRename, icon } from '../ui.js';
import { api } from '../api.js';
import * as store from '../store.js';

let grid = null, emptyEl = null;
let mounted = false;
const pickers = new Map();   // friendly_name -> { picker, dragging }
const HSMAX = 254;           // Matter hue/saturation max

async function patch(name, body){
  try { await api('/api/devices/'+encodeURIComponent(name), { method:'PATCH', body:JSON.stringify(body) }); }
  catch(e){ if(e.message!=='401') toast('Set failed'); }
}

// ---- per-device kebab menu (rename / unpair) ----
const unpairModal = confirmModal({
  title: name => `Unpair “${name}”?`,
  body: 'It will be removed from the Matter fabric and must be re-paired with its setup code to return.',
  confirmLabel: 'Unpair',
  danger: true,
  onConfirm: doUnpair,
});

function startRename(name){
  const el = document.getElementById('dev-'+cssId(name)); if(!el) return;
  const nameEl = el.querySelector('.name'); if(!nameEl) return;
  inlineRename(nameEl, name, async (to)=>{
    try {
      const r = await api('/api/devices/'+encodeURIComponent(name)+'/rename', { method:'POST', body:JSON.stringify({to}) });
      if (r.status===409) toast('That name is already taken');
      else if (r.status===400) toast('That name can’t be used');
      else if (!r.ok) toast('Rename failed');
      else toast('Renamed');
    } catch(e){ if(e.message!=='401') toast('Rename failed'); }
  }, 'New device name');
}

const menu = kebabMenu([
  { label:'Rename', icon: icon.rename, onClick: name => startRename(name) },
  { label:'Unpair…', icon: icon.trash, danger:true, onClick: (name, origin) => unpairModal.open(origin, name) },
]);

async function doUnpair(name){
  try {
    const r = await api('/api/devices/'+encodeURIComponent(name), { method:'DELETE' });
    if (r.status===404) toast('Device not found');
    else if (!r.ok) toast('Unpair failed');
    else toast('Unpairing…');   // the card disappears when the node_removed-driven SSE 'devices' refresh arrives
  } catch(e){ if(e.message!=='401') toast('Unpair failed'); }
}

// Cache update is unconditional (keeps store.devices/deviceState warm for other views' onFrame
// reads, e.g. groups' member picker) — only the DOM rebuild below is guarded on `mounted`.
async function load(){
  let list; try { list = await (await api('/api/devices')).json(); } catch(e){ return; }
  store.devices.clear(); list.forEach(d=>store.devices.set(d.friendly_name,d));
  await Promise.all(list.map(async d=>{
    try { store.deviceState.set(d.friendly_name, await (await api('/api/devices/'+encodeURIComponent(d.friendly_name))).json()); }
    catch { store.deviceState.set(d.friendly_name,{}); }
  }));
  if (mounted) render();
}

function render(){
  if (!mounted) return;
  grid.innerHTML=''; pickers.clear();   // grid is rebuilt from scratch; discard stale picker instances
  const list=[...store.devices.values()].sort((a,b)=>a.friendly_name.localeCompare(b.friendly_name));
  emptyEl.hidden = list.length>0;
  for (const d of list) grid.appendChild(station(d));
  list.forEach(d=>applyState(d.friendly_name));
}

function station(d){
  const el=document.createElement('section');
  el.className='station'; el.id='dev-'+cssId(d.friendly_name);
  const hasColor = d.exposes.some(e=>e.property==='hue') && d.exposes.some(e=>e.property==='saturation');
  const skip = new Set(hasColor ? ['hue','saturation'] : []);
  skip.add('transition');                                   // modifier, not a control
  const settable = d.exposes.filter(e=>(e.access&2) && !skip.has(e.property));
  const readonly = d.exposes.filter(e=>!(e.access&2) && !skip.has(e.property));
  el.innerHTML =
    `<div class="st-head">
       <div><div class="name">${esc(d.friendly_name)}</div>
         <div class="coords">${esc(d.device_type)} · node ${esc(d.node_id)} / ep ${esc(d.endpoint)}${d.transport?`<span class="pill">${esc(d.transport)}</span>`:''}</div></div>
       <div class="st-tools">
         <span class="bench ${d.reachable?'up':''}" title="${d.reachable?'reachable':'unreachable'}"></span>
         <button class="kebab" aria-label="Device actions" aria-haspopup="true" aria-expanded="false">${icon.kebab}</button>
       </div>
     </div><div class="rows"></div>`;
  const rows=el.querySelector('.rows');
  el.querySelector('.kebab').addEventListener('click', ev=>{
    ev.stopPropagation();
    menu.open(ev.currentTarget, d.friendly_name);
  });
  for (const e of settable) rows.appendChild(control(d.friendly_name,e));
  for (const e of readonly) rows.appendChild(readoutRow(e));
  if (hasColor) rows.prepend(colorControl(d.friendly_name));
  return el;
}

function colorControl(name){
  const wrap=document.createElement('div'); wrap.className='ctl';
  wrap.innerHTML=`<div class="row"><span class="legend">color</span></div><div class="picker"></div>
    <div class="row"><span class="legend">hue</span>
      <input class="entry" type="number" inputmode="numeric" min="0" max="${HSMAX}" data-prop="hue" aria-label="hue value"></div>
    <div class="row"><span class="legend">saturation</span>
      <input class="entry" type="number" inputmode="numeric" min="0" max="${HSMAX}" data-prop="saturation" aria-label="saturation value"></div>`;
  const mount=wrap.querySelector('.picker');
  const picker=new iro.ColorPicker(mount,{width:150,color:{h:0,s:0,v:100},layout:[{component:iro.ui.Wheel}]});
  const rec={picker,dragging:false};
  pickers.set(name,rec);
  picker.on('input:start',()=>rec.dragging=true);
  picker.on('input:end',()=>{
    rec.dragging=false;
    const {h,s}=picker.color.hsv;
    patch(name,{hue:Math.round(h*HSMAX/360), saturation:Math.round(s*HSMAX/100)});
  });
  // Raw HS entry: edit the exact Matter values (0–HSMAX). Commit on Enter or blur.
  const clamp=v=>Math.min(HSMAX,Math.max(0,Math.round(v)));
  for (const prop of ['hue','saturation']){
    const box=wrap.querySelector(`input.entry[data-prop="${prop}"]`);
    box.addEventListener('change',()=>{ if(box.value==='') return; const v=clamp(Number(box.value)); box.value=v; patch(name,{[prop]:v}); });
    box.addEventListener('keydown',ev=>{ if(ev.key==='Enter'){ ev.preventDefault(); box.blur(); } });
  }
  return wrap;
}

function control(name,e){
  const wrap=document.createElement('div');
  if (e.property==='identify'){
    wrap.className='row';
    wrap.innerHTML=`<span class="legend">identify</span><button class="btn-ident">blink</button>`;
    wrap.querySelector('button').addEventListener('click',()=>patch(name,{identify:10}));
    return wrap;
  } else if (e.type==='enum'){
    wrap.className='ctl';
    const label=esc(e.property.replace('_',' '));
    const btns=(e.values||[]).map(v=>`<button class="seg" data-prop="${e.property}" data-val="${esc(v)}">${esc(v.toLowerCase())}</button>`).join('');
    wrap.innerHTML=`<div class="row"><span class="legend">${label}</span></div><div class="segmented">${btns}</div>`;
    wrap.querySelectorAll('button.seg').forEach(b=>
      b.addEventListener('click',()=>patch(name,{[e.property]:b.dataset.val})));
  } else if (e.property==='state'){
    wrap.className='row';
    wrap.innerHTML=`<span class="legend">state</span>
      <span class="switch"><input type="checkbox" data-prop="state"><span class="slot"></span><span class="knob"></span></span>`;
    wrap.querySelector('input').addEventListener('change',ev=>patch(name,{state:ev.target.checked?'ON':'OFF'}));
  } else {
    wrap.className='ctl';
    const min=e.value_min??0, max=e.value_max??254;
    const label=esc(e.property.replace('_',' '));
    wrap.innerHTML=`<div class="row"><span class="legend">${label}</span>
        <input class="entry" type="number" inputmode="numeric" min="${min}" max="${max}" data-prop="${e.property}-val" aria-label="${label} value"></div>
      <input type="range" min="${min}" max="${max}" data-prop="${e.property}">`;
    const s=wrap.querySelector('input[type=range]');
    const box=wrap.querySelector('input[type=number]');
    const clamp=v=>Math.min(max,Math.max(min,Math.round(v)));
    // Suppress live updates only while actively dragging — a range input keeps focus after you
    // release it, so guarding on focus alone would freeze the slider until you clicked elsewhere.
    const startDrag=()=>s.dataset.dragging='1', endDrag=()=>delete s.dataset.dragging;
    s.addEventListener('pointerdown',startDrag);
    s.addEventListener('pointerup',endDrag);
    s.addEventListener('pointercancel',endDrag);
    s.addEventListener('input',()=>{ if(box!==document.activeElement) box.value=s.value; });
    s.addEventListener('change',()=>{ endDrag(); patch(name,{[e.property]:Number(s.value)}); });
    // Typed entry: mirror the slider live, commit (clamp + patch) on Enter or blur.
    box.addEventListener('input',()=>{ if(box.value!=='') s.value=clamp(Number(box.value)); });
    box.addEventListener('change',()=>{ if(box.value==='') return; const v=clamp(Number(box.value)); box.value=v; s.value=v; patch(name,{[e.property]:v}); });
    box.addEventListener('keydown',ev=>{ if(ev.key==='Enter'){ ev.preventDefault(); box.blur(); } });
  }
  return wrap;
}

function readoutRow(e){
  const row=document.createElement('div'); row.className='row';
  row.innerHTML=`<span class="legend">${esc(e.property)}</span>
    <span class="readout" data-prop="${e.property}">—<span class="u">${e.unit?' '+esc(e.unit):''}</span></span>`;
  return row;
}

function applyState(name){
  if (!mounted) return;
  const el=document.getElementById('dev-'+cssId(name)); if(!el) return;
  const s=store.deviceState.get(name)||{};
  for (const [prop,val] of Object.entries(s)){
    const t=el.querySelector(`input[type=checkbox][data-prop="${prop}"]`);
    if (t){ t.checked=String(val).toUpperCase()==='ON'; continue; }
    const sl=el.querySelector(`input[type=range][data-prop="${prop}"]`);
    if (sl){ if(sl.dataset.dragging!=='1'){ sl.value=val; const box=el.querySelector(`input[type=number][data-prop="${prop}-val"]`); if(box && box!==document.activeElement) box.value=val; } continue; }
    const segs=el.querySelectorAll(`button.seg[data-prop="${prop}"]`);
    if (segs.length){ segs.forEach(b=>b.classList.toggle('active', b.dataset.val.toUpperCase()===String(val).toUpperCase())); continue; }
    const ro=el.querySelector(`.readout[data-prop="${prop}"]`);
    if (ro) ro.firstChild.textContent=fmt(val);
  }
  // Color wheel: drive it from live hue/saturation (value pinned so the wheel shows pure hue+sat).
  const rec=pickers.get(name);
  if (rec && !rec.dragging && s.hue!=null && s.saturation!=null)
    rec.picker.color.hsv={ h:s.hue*360/HSMAX, s:s.saturation*100/HSMAX, v:100 };
  // Raw HS entry fields track live state (unless the user is editing one).
  for (const prop of ['hue','saturation']){
    if (s[prop]==null) continue;
    const box=el.querySelector(`input.entry[data-prop="${prop}"]`);
    if (box && box!==document.activeElement) box.value=s[prop];
  }
  // De-emphasize whichever color representation is not the live one (ColorMode).
  if (rec){
    const pk=el.querySelector('.picker');
    if (pk) pk.classList.toggle('inactive', s.color_mode==='ct');
    const ct=el.querySelector('input[type=range][data-prop="color_temp"]');
    if (ct) ct.closest('.ctl')?.classList.toggle('inactive', s.color_mode==='hs');
  }
}

export function mount(container){
  container.innerHTML =
    `<div class="grid" id="grid"></div>
     <div class="empty" id="empty" hidden>No stations on the fabric. Commission a device to begin.</div>`;
  grid = container.querySelector('#grid');
  emptyEl = container.querySelector('#empty');
  mounted = true;
  return load();
}

export function unmount(){
  mounted = false;
  menu.close();          // drop this view's open kebab dropdown, if any
  unpairModal.close();   // drop the unpair confirm modal, if any
  grid = null; emptyEl = null; pickers.clear();
}

export function onFrame(msg){
  // Cache update (store.deviceState) is unconditional; applyState guards its own DOM writes on `mounted`.
  if (msg.type==='state'){ store.deviceState.set(msg.device, msg.state); applyState(msg.device); }
  else if (msg.type==='devices'){ return load(); }
}
