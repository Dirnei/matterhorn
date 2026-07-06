// js/ui.js — small DOM-adjacent UI primitives shared across dashboard views.
export function toast(m){ const t=document.getElementById('toast'); t.textContent=m; t.classList.add('show'); setTimeout(()=>t.classList.remove('show'),2600); }

export const esc=s=>String(s??'').replace(/[&<>"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
export const cssId=s=>s.replace(/[^a-z0-9_-]/gi,'_');
export const fmt=v=>typeof v==='boolean'?(v?'yes':'no'):String(v);
