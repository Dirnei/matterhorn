// js/dock.js — Station Log dock: chronological stream (raw + activity-filtered), resizable, persisted height.
import { esc } from './ui.js';

// Raw is the whole chronological stream (both categories); Activity filters to milestones.
const dock={el:null,body:null,last:null,mode:'activity',paused:false,
  buf:{activity:[],all:[]},capAct:100,capAll:300};

const dockView=()=>dock.mode==='activity'?dock.buf.activity:dock.buf.all;

function dockRender(){
  dock.body.innerHTML=dockView().map(e=>
    `<div class="ln ${e.level}"><span class="t">${e.ts}</span> `+
    `<span class="k">${esc(e.kind)}</span> ${esc(e.msg)}`+
    `${e.device?` <span class="t">· ${esc(e.device)}</span>`:''}</div>`).join('');
  if(!dock.paused) dock.body.scrollTop=dock.body.scrollHeight;
}

function dockPush(e){
  dock.buf.all.push(e); if(dock.buf.all.length>dock.capAll) dock.buf.all.shift();
  if(e.category==='activity'){ dock.buf.activity.push(e); if(dock.buf.activity.length>dock.capAct) dock.buf.activity.shift(); }
  dock.last.textContent=`last: ${e.kind} · ${e.ts}`;
  // Raw view shows everything; Activity view only its own entries.
  if((dock.mode==='raw' || e.category==='activity') && !dock.paused) dockRender();
}

function dockToggleCollapse(){
  const c=dock.el.classList.toggle('collapsed');
  document.getElementById('dockToggle').textContent=c?'▲':'▼';
}

export function mountDock(){
  dock.el=document.getElementById('dock');
  dock.body=document.getElementById('dockBody');
  dock.last=document.getElementById('dockLast');

  document.getElementById('dockMode').addEventListener('click',ev=>{
    const m=ev.target.dataset.mode; if(!m) return;
    dock.mode=m;
    [...ev.currentTarget.children].forEach(s=>s.classList.toggle('on',s.dataset.mode===m));
    dockRender();
  });
  document.getElementById('dockPause').addEventListener('click',ev=>{
    dock.paused=!dock.paused; ev.target.classList.toggle('active',dock.paused);
    ev.target.textContent=dock.paused?'resume':'pause'; if(!dock.paused) dockRender();
  });
  document.getElementById('dockClear').addEventListener('click',()=>{
    if(dock.mode==='activity') dock.buf.activity=[]; else dock.buf.all=[];
    dockRender();
  });
  document.getElementById('dockHead').addEventListener('click',dockToggleCollapse);
  document.getElementById('dockToggle').addEventListener('click',dockToggleCollapse);

  // drag the top edge to resize the log body (grow upward); height persists across reloads
  const dockGrip=document.getElementById('dockGrip');
  const savedDockH=parseInt(localStorage.getItem('mh_dock_h'),10);
  if(savedDockH) dock.body.style.height=savedDockH+'px';
  let dockDrag=null;
  dockGrip.addEventListener('pointerdown',ev=>{
    ev.preventDefault();
    dockDrag={y:ev.clientY,h:dock.body.offsetHeight};
    dockGrip.setPointerCapture(ev.pointerId);
  });
  dockGrip.addEventListener('pointermove',ev=>{
    if(!dockDrag) return;
    const h=Math.min(Math.round(innerHeight*0.7),Math.max(80,dockDrag.h+(dockDrag.y-ev.clientY)));
    dock.body.style.height=h+'px';
  });
  dockGrip.addEventListener('pointerup',ev=>{
    if(!dockDrag) return;
    dockDrag=null; dockGrip.releasePointerCapture(ev.pointerId);
    localStorage.setItem('mh_dock_h',dock.body.offsetHeight);
  });
}

export function onFrame(msg){
  if (msg.type==='log') dockPush(msg);
}
