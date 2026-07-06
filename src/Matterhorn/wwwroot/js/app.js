// js/app.js — composition root: mounts the views + dock + commission modal, wires the single
// SSE connection to fan frames to every module, and keeps the page-level chrome (ridge, api key).
import { getApiKey, setApiKey } from './api.js';
import { connectSse, reconnect } from './sse.js';
import * as devices from './views/devices.js';
import * as groups from './views/groups.js';
import * as scenes from './views/scenes.js';
import * as dock from './dock.js';
import * as commission from './commission.js';

const keyInput = document.getElementById('apikey');
keyInput.value = getApiKey();

const modules = [devices, groups, scenes, dock, commission];
function onFrame(msg){ for (const m of modules) m.onFrame?.(msg); }

keyInput.addEventListener('change',()=>{
  setApiKey(keyInput.value.trim()); reconnect();
  devices.mount(); groups.mount(); scenes.mount();
});

// draw the ridgeline once on load
const ridge=document.querySelector('.ridge .draw');
if (ridge && !matchMedia('(prefers-reduced-motion: reduce)').matches){
  const len=ridge.getTotalLength();
  ridge.style.strokeDasharray=len; ridge.style.strokeDashoffset=len;
  ridge.animate([{strokeDashoffset:len},{strokeDashoffset:0}],{duration:1400,easing:'ease-out',fill:'forwards'});
}

async function boot(){
  await devices.mount();   // groups/scenes rendering reads store.devices for member pickers
  groups.mount();
  scenes.mount();
  dock.mountDock();
  commission.mountCommission();
  connectSse(onFrame);
}
boot();
