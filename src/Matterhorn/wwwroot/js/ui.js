// js/ui.js — small DOM-adjacent UI primitives shared across dashboard views.
import * as store from './store.js';

export function toast(m){ const t=document.getElementById('toast'); t.textContent=m; t.classList.add('show'); setTimeout(()=>t.classList.remove('show'),2600); }

export const esc=s=>String(s??'').replace(/[&<>"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
export const cssId=s=>s.replace(/[^a-z0-9_-]/gi,'_');
export const fmt=v=>typeof v==='boolean'?(v?'yes':'no'):String(v);

// ---- inline icon glyphs — a small, cohesive SVG language (currentColor, ~14-16px, 1.6 stroke)
// that replaces the ad-hoc text glyphs (kebab dots, pencil, delete) previously used for row actions.
export const icon = {
  kebab: '<svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true"><circle cx="12" cy="5" r="1.7" fill="currentColor"/><circle cx="12" cy="12" r="1.7" fill="currentColor"/><circle cx="12" cy="19" r="1.7" fill="currentColor"/></svg>',
  rename: '<svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true"><path d="M4 20l.8-3.9L16.2 4.7a1.5 1.5 0 0 1 2.1 0l1 1a1.5 1.5 0 0 1 0 2.1L7.9 19.2 4 20Z" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round" stroke-linecap="round"/><path d="M14.3 6.6l3.1 3.1" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/></svg>',
  trash: '<svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true"><path d="M5 7h14M9.5 7V5.2a1 1 0 0 1 1-1h3a1 1 0 0 1 1 1V7M10 11v6M14 11v6" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/><path d="M6.5 7l1 12.2a1 1 0 0 0 1 .8h7a1 1 0 0 0 1-.8L17.5 7" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round"/></svg>',
  close: '<svg viewBox="0 0 24 24" width="12" height="12" aria-hidden="true"><path d="M6 6l12 12M18 6L6 18" stroke="currentColor" stroke-width="2" stroke-linecap="round"/></svg>',
};

// ---- kebab menu: one body-level dropdown per view, retargeted to whichever row's kebab was
// clicked (the card clips overflow, so the menu can't live inside the card). `open(anchorBtn, subject)`
// stores `subject` (e.g. the device/group/scene name) and hands it back to the clicked item's
// onClick, along with the anchor button (needed as the "origin" to focus-return a confirm modal to).
export function kebabMenu(items){
  const menu = document.createElement('div');
  menu.className = 'menu';
  menu.innerHTML = items.map((it,i)=>
    `<button data-i="${i}"${it.danger?' class="danger"':''}>${it.icon||''}<span>${it.label}</span></button>`).join('');
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

  return { open, close };
}

// ---- picker modal: shared searchable device picker used to create groups and (next) capture
// scenes. `opts = { title, submitLabel, note?, initialName, preselected?: Set<string>, onSubmit(name, deviceFriendlyNames[]) }`.
// Renders into #modal-root (one instance per call site, like confirmModal/kebabMenu are one-per-menu).
let pickerModalSeq = 0;

export function createPickerModal(opts){
  const id = 'picker-title-' + (++pickerModalSeq);
  const backdrop = document.createElement('div');
  backdrop.className = 'modal-backdrop';
  backdrop.innerHTML =
    `<div class="modal picker-modal" role="dialog" aria-modal="true" aria-labelledby="${id}">
       <h3 id="${id}"></h3>
       <input class="picker-name" aria-label="Name" autocomplete="off" />
       ${opts.note ? '<p class="picker-note"></p>' : ''}
       <div class="picker-search-wrap">
         <svg class="picker-search-icon" viewBox="0 0 24 24" aria-hidden="true">
           <circle cx="10.5" cy="10.5" r="6.5" fill="none" stroke="currentColor" stroke-width="1.8"/>
           <line x1="15.3" y1="15.3" x2="20.5" y2="20.5" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/>
         </svg>
         <input class="picker-search" type="text" placeholder="search devices…" autocomplete="off" aria-label="Search devices" />
       </div>
       <div class="picker-seg" role="group" aria-label="Filter devices">
         <button type="button" data-seg="controllable" aria-pressed="true">Controllable</button>
         <button type="button" data-seg="all" aria-pressed="false">All</button>
       </div>
       <div class="picker-list-wrap"><div class="picker-list" role="listbox" aria-multiselectable="true"></div></div>
       <div class="picker-footer">
         <span class="picker-count"></span>
         <div class="actions">
           <button type="button" data-act="cancel">Cancel</button>
           <button type="button" data-act="submit" class="go"></button>
         </div>
       </div>
     </div>`;
  document.getElementById('modal-root').appendChild(backdrop);

  const titleEl = backdrop.querySelector('h3');
  const nameEl = backdrop.querySelector('.picker-name');
  const noteEl = backdrop.querySelector('.picker-note');
  const searchEl = backdrop.querySelector('.picker-search');
  const segBtns = [...backdrop.querySelectorAll('[data-seg]')];
  const listEl = backdrop.querySelector('.picker-list');
  const countEl = backdrop.querySelector('.picker-count');
  const submitBtn = backdrop.querySelector('[data-act="submit"]');
  submitBtn.textContent = opts.submitLabel;

  let controllableOnly = true;
  let selected = new Set();
  let origin = null;

  function setSeg(controllable){
    controllableOnly = controllable;
    segBtns.forEach(b=>{
      const active = (b.dataset.seg==='controllable')===controllable;
      b.classList.toggle('active', active);
      b.setAttribute('aria-pressed', String(active));
    });
  }

  function renderList(){
    const all = [...store.devices.values()].sort((a,b)=>a.friendly_name.localeCompare(b.friendly_name));
    const list = store.filterDevices(all, searchEl.value, controllableOnly);
    listEl.innerHTML = list.length ? list.map(d=>{
      const itemId = 'picker-item-' + cssId(d.friendly_name) + '-' + pickerModalSeq;
      const checked = selected.has(d.friendly_name) ? ' checked' : '';
      return `<label class="picker-item" for="${itemId}">
         <input type="checkbox" id="${itemId}" data-name="${esc(d.friendly_name)}"${checked} />
         <span class="pi-name">${esc(d.friendly_name)}</span>
         <span class="pi-type">${esc(d.device_type||'')}</span>
         <span class="pi-dot${d.reachable?' up':''}" title="${d.reachable?'reachable':'unreachable'}"></span>
       </label>`;
    }).join('') : '<div class="picker-empty">No matching devices</div>';
    listEl.querySelectorAll('input[type=checkbox]').forEach(cb=>{
      cb.addEventListener('change', ()=>{
        const n = cb.dataset.name;
        if (cb.checked) selected.add(n); else selected.delete(n);
        updateCount();
      });
    });
    updateCount();
  }
  function updateCount(){ countEl.innerHTML = `<b>${selected.size}</b> selected`; }

  searchEl.addEventListener('input', renderList);
  segBtns.forEach(b=>b.addEventListener('click', ()=>{ setSeg(b.dataset.seg==='controllable'); renderList(); }));

  function close(returnFocus){
    if (!backdrop.classList.contains('open')) return;
    backdrop.classList.remove('open');
    if (returnFocus) origin?.focus();
    origin = null;
  }
  backdrop.addEventListener('click', e=>{
    if (e.target===backdrop){ close(true); return; }
    const act = e.target.closest('button')?.dataset.act; if (!act) return;
    if (act==='cancel'){ close(true); return; }
    if (act==='submit'){
      const name = nameEl.value.trim();
      const members = [...selected];
      opts.onSubmit(name, members);
      close();
    }
  });
  // keep focus inside the modal while it is open (name/search/toggle/checklist/actions)
  backdrop.addEventListener('keydown', e=>{
    if (!backdrop.classList.contains('open') || e.key!=='Tab') return;
    const focusables = [...backdrop.querySelectorAll('input, button')];
    const first = focusables[0], last = focusables[focusables.length-1];
    if (e.shiftKey && document.activeElement===first){ e.preventDefault(); last.focus(); }
    else if (!e.shiftKey && document.activeElement===last){ e.preventDefault(); first.focus(); }
  });
  document.addEventListener('keydown', e=>{ if (e.key==='Escape') close(true); });

  function open(originBtn){
    origin = originBtn || null;
    titleEl.textContent = opts.title;
    if (noteEl) noteEl.textContent = opts.note;
    nameEl.value = opts.initialName || '';
    searchEl.value = '';
    selected = new Set(opts.preselected || []);
    setSeg(true);
    renderList();
    backdrop.classList.add('open');
    nameEl.focus();
  }

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
