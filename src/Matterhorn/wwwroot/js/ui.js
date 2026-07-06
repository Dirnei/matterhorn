// js/ui.js — small DOM-adjacent UI primitives shared across dashboard views.
export function toast(m){ const t=document.getElementById('toast'); t.textContent=m; t.classList.add('show'); setTimeout(()=>t.classList.remove('show'),2600); }

export const esc=s=>String(s??'').replace(/[&<>"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
export const cssId=s=>s.replace(/[^a-z0-9_-]/gi,'_');
export const fmt=v=>typeof v==='boolean'?(v?'yes':'no'):String(v);

// ---- kebab menu: one body-level dropdown per view, retargeted to whichever row's kebab was
// clicked (the card clips overflow, so the menu can't live inside the card). `open(anchorBtn, subject)`
// stores `subject` (e.g. the device/group/scene name) and hands it back to the clicked item's
// onClick, along with the anchor button (needed as the "origin" to focus-return a confirm modal to).
export function kebabMenu(items){
  const menu = document.createElement('div');
  menu.className = 'menu';
  menu.innerHTML = items.map((it,i)=>
    `<button data-i="${i}"${it.danger?' class="danger"':''}>${it.label}</button>`).join('');
  document.body.appendChild(menu);
  let anchor = null, subject = null;

  function open(anchorBtn, subj){
    if (menu.classList.contains('open') && anchor===anchorBtn){ close(true); return; }  // same kebab toggles closed
    anchor?.setAttribute('aria-expanded','false');      // reset a previously-open kebab when retargeting
    anchor = anchorBtn; subject = subj;
    anchorBtn.setAttribute('aria-expanded','true');
    menu.classList.add('open');                         // display first so offsetWidth is measurable
    const r = anchorBtn.getBoundingClientRect();
    menu.style.top  = (window.scrollY + r.bottom + 4) + 'px';
    menu.style.left = (window.scrollX + r.right - menu.offsetWidth) + 'px';
    menu.querySelector('button').focus();               // move focus into the menu (keyboard a11y)
  }
  function close(returnFocus){
    if (!menu.classList.contains('open')) return;
    menu.classList.remove('open'); subject = null;
    anchor?.setAttribute('aria-expanded','false');
    if (returnFocus) anchor?.focus();                    // return focus to the invoking kebab
  }
  menu.addEventListener('click', e=>{
    const i = e.target.closest('button')?.dataset.i; if (i==null) return;
    const origin = anchor, subj = subject; close();
    items[Number(i)].onClick(subj, origin);
  });
  menu.addEventListener('keydown', e=>{                  // arrow-key navigation within the open menu
    const btns = [...menu.querySelectorAll('button')];
    const i = btns.indexOf(document.activeElement);
    if (e.key==='ArrowDown'){ e.preventDefault(); btns[(i+1)%btns.length].focus(); }
    else if (e.key==='ArrowUp'){ e.preventDefault(); btns[(i-1+btns.length)%btns.length].focus(); }
    else if (e.key==='Escape'){ e.preventDefault(); close(true); }
  });
  document.addEventListener('click', e=>{
    if (menu.classList.contains('open') && !menu.contains(e.target)) close();
  });
  document.addEventListener('keydown', e=>{ if (e.key==='Escape') close(true); });

  return { open, close };
}

// ---- confirm modal: focus-trapped yes/no dialog, body-level (one instance per call site).
// `title`/`body` may be a string or a function of the `subject` passed to open() — the unpair/delete
// dialogs need the target's name in the heading, which isn't known until the row's action fires.
let confirmModalSeq = 0;

export function confirmModal({ title, body, confirmLabel='Confirm', danger=false, onConfirm }){
  const id = 'confirm-title-' + (++confirmModalSeq);
  const backdrop = document.createElement('div');
  backdrop.className = 'modal-backdrop';
  backdrop.innerHTML =
    `<div class="modal" role="dialog" aria-modal="true" aria-labelledby="${id}">
       <h3 id="${id}"></h3>
       <p></p>
       <div class="actions"><button data-act="cancel">Cancel</button><button data-act="confirm"${danger?' class="danger"':''}>${esc(confirmLabel)}</button></div>
     </div>`;
  document.body.appendChild(backdrop);
  const titleEl = backdrop.querySelector('h3'), bodyEl = backdrop.querySelector('p');
  let origin = null, subject = null;

  function open(originBtn, subj){
    origin = originBtn || null; subject = subj;
    titleEl.textContent = typeof title==='function' ? title(subj) : title;
    bodyEl.textContent  = typeof body==='function'  ? body(subj)  : body;
    backdrop.classList.add('open');
    backdrop.querySelector('[data-act="confirm"]').focus();
  }
  function close(returnFocus){
    if (!backdrop.classList.contains('open')) return;
    backdrop.classList.remove('open'); subject = null;
    if (returnFocus) origin?.focus();                    // return focus to the invoking kebab
    origin = null;
  }
  backdrop.addEventListener('click', e=>{
    if (e.target===backdrop){ close(true); return; }     // backdrop click → focus back to kebab
    const act = e.target.closest('button')?.dataset.act; if (!act) return;
    if (act==='cancel'){ close(true); return; }
    if (act==='confirm'){ const subj = subject; close(); onConfirm(subj); }
  });
  // keep focus inside the modal while it is open (simple two-button trap)
  backdrop.addEventListener('keydown', e=>{
    if (!backdrop.classList.contains('open') || e.key!=='Tab') return;
    const btns = [...backdrop.querySelectorAll('button')];
    const first = btns[0], last = btns[btns.length-1];
    if (e.shiftKey && document.activeElement===first){ e.preventDefault(); last.focus(); }
    else if (!e.shiftKey && document.activeElement===last){ e.preventDefault(); first.focus(); }
  });
  document.addEventListener('keydown', e=>{ if (e.key==='Escape') close(true); });

  return { open };
}

// ---- inline rename: swaps `nameEl`'s content for a text input in place, commits on Enter/blur,
// cancels on Esc. `onCommit(newName)` returns a Promise (its own success/failure toasting is the
// caller's job); the original text is always restored afterwards — callers that reload on success
// (SSE-driven views) will overwrite it, callers that rely on SSE alone just see the old name blink
// back until the push arrives.
export function inlineRename(nameEl, currentName, onCommit, ariaLabel='New name'){
  if (!nameEl || nameEl.querySelector('input')) return;
  const input = document.createElement('input');
  input.className = 'name-edit'; input.value = currentName; input.setAttribute('aria-label', ariaLabel);
  nameEl.replaceChildren(input); input.focus(); input.select();
  let done = false;
  const restore = ()=>{ nameEl.textContent = currentName; };
  const cancel  = ()=>{ if (done) return; done = true; restore(); };
  const commit  = async ()=>{
    if (done) return;
    const to = input.value.trim();
    if (!to || to===currentName){ done = true; restore(); return; }
    done = true;
    try { await onCommit(to); } finally { restore(); }
  };
  input.addEventListener('keydown', e=>{
    if (e.key==='Enter'){ e.preventDefault(); commit(); }
    else if (e.key==='Escape'){ e.preventDefault(); cancel(); }
  });
  input.addEventListener('blur', cancel);
}
