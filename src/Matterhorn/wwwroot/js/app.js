// js/app.js — composition root: mounts the dock + commission modal (always-on shell) and the
// tab router, wires the single SSE connection to fan frames to every module, and keeps the
// page-level chrome (ridge, api key). Only the routed view is ever in the DOM; the dock,
// commission modal and SSE connection stay alive across tab switches.
import { getApiKey, setApiKey } from './api.js';
import { connectSse, reconnect } from './sse.js';
import * as devices from './views/devices.js';
import * as groups from './views/groups.js';
import * as scenes from './views/scenes.js';
import * as dock from './dock.js';
import * as commission from './commission.js';

const keyInput = document.getElementById('apikey');
keyInput.value = getApiKey();

// Devices must precede groups: onFrame populates store.deviceState (via devices.onFrame) before groups.onFrame reads it in applyGroupState.
// This fans to every module regardless of which view is mounted — each view's onFrame keeps its
// store cache warm unconditionally and only guards its own DOM writes behind a `mounted` flag.
const modules = [devices, groups, scenes, dock, commission];
function onFrame(msg){ for (const m of modules) m.onFrame?.(msg); }

keyInput.addEventListener('change', async ()=>{
  setApiKey(keyInput.value.trim()); reconnect();
  // Refresh every view's cache under the new key; devices must precede groups/scenes (member pickers read store.devices).
  await devices.onFrame({type:'devices'});
  groups.onFrame({type:'groups'});
  scenes.onFrame({type:'scenes'});
});

// draw the ridgeline once on load
const ridge=document.querySelector('.ridge .draw');
if (ridge && !matchMedia('(prefers-reduced-motion: reduce)').matches){
  const len=ridge.getTotalLength();
  ridge.style.strokeDasharray=len; ridge.style.strokeDashoffset=len;
  ridge.animate([{strokeDashoffset:len},{strokeDashoffset:0}],{duration:1400,easing:'ease-out',fill:'forwards'});
}

// ---- hash router: exactly one view mounted at a time ----
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

async function boot(){
  dock.mountDock();
  commission.mountCommission();
  connectSse(onFrame);
  // Warm store.devices/deviceState even if the default/deep-linked tab isn't Devices — the
  // group cards' "add device" picker reads store.devices at render time.
  await devices.onFrame({type:'devices'});
  route();
}
boot();
