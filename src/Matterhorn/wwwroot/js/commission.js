// js/commission.js — commissioning progress modal (spinner + alpine quips while we wait).
import { toast } from './ui.js';
import { api } from './api.js';

const commModal=document.createElement('div');
commModal.className='modal-backdrop';
commModal.innerHTML=
  `<div class="modal comm" role="dialog" aria-modal="true" aria-labelledby="commTitle" aria-live="polite">
     <div class="spinner" aria-hidden="true"></div>
     <h3 id="commTitle">Commissioning…</h3>
     <p id="commMsg" class="comm-msg"></p>
     <p class="comm-hint">Matter interviews can take 30–60 seconds.</p>
     <button class="comm-hide" type="button">hide — watch the log instead</button>
   </div>`;
document.body.appendChild(commModal);
const COMM_QUIPS=["Climbing the mountain…","Searching for the alphorn…","Looking at a cow…",
  "Waiting for good weather…","Warming up the raclette…","Asking the marmots for directions…",
  "Polishing the cowbells…","Yodeling to the device…","Following the hiking trail…","Brewing edelweiss tea…"];
let commQuipTimer=null, commGiveUp=null;

function commQuip(){
  const el=commModal.querySelector('#commMsg');
  let next; do { next=COMM_QUIPS[Math.floor(Math.random()*COMM_QUIPS.length)]; } while(next===el.textContent);
  el.style.opacity=0;
  setTimeout(()=>{ el.textContent=next; el.style.opacity=1; },200);
}
function openComm(){
  commModal.querySelector('.modal.comm').classList.remove('done');
  commModal.querySelector('#commTitle').textContent='Commissioning…';
  commModal.querySelector('#commMsg').textContent=COMM_QUIPS[0];
  commModal.classList.add('open');
  clearInterval(commQuipTimer); commQuipTimer=setInterval(commQuip,2800);
  clearTimeout(commGiveUp); commGiveUp=setTimeout(closeComm,120000); // never trap the user forever
}
function closeComm(){
  commModal.classList.remove('open');
  clearInterval(commQuipTimer); commQuipTimer=null;
  clearTimeout(commGiveUp); commGiveUp=null;
}
function commOutcome(text, ok){
  clearInterval(commQuipTimer); commQuipTimer=null; clearTimeout(commGiveUp); commGiveUp=null;
  commModal.querySelector('.modal.comm').classList.add('done');
  commModal.querySelector('#commTitle').textContent=ok?'Joined!':'Commission failed';
  const el=commModal.querySelector('#commMsg'); el.style.opacity=1; el.textContent=text;
  setTimeout(closeComm, ok?1600:3200);
}
// Close the modal on the real outcome from the Station Log stream.
function commOnLog(m){
  if(!commModal.classList.contains('open')) return;
  if(m.kind==='joined') commOutcome('✓ '+(m.device||'device')+' joined the fabric', true);
  else if(m.kind==='commission_failed') commOutcome('✗ '+(m.msg||'commissioning failed'), false);
}
commModal.querySelector('.comm-hide').addEventListener('click',closeComm);

export function mountCommission(){
  document.getElementById('commissionForm').addEventListener('submit',async ev=>{
    ev.preventDefault();
    const code=document.getElementById('code').value.trim(); if(!code) return;
    openComm();
    try { await api('/api/commission',{method:'POST',body:JSON.stringify({code})}); document.getElementById('code').value=''; }
    catch(e){ closeComm(); if(e.message!=='401') toast('Commission failed'); }
  });
}

export function onFrame(msg){
  if (msg.type==='log') commOnLog(msg);
}
