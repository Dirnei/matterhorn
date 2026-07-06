// js/store.js — in-memory caches (updated by views/SSE) + DOM-free pure helpers.
export const devices = new Map();      // friendly_name -> descriptor
export const deviceState = new Map();  // friendly_name -> state object
export const groups = new Map();       // friendly_name -> { friendly_name, members }
export const scenes = new Map();       // friendly_name -> { friendly_name, members }

export function isControllable(d) {
  return Array.isArray(d?.exposes) && d.exposes.some(e => (e.access & 2) === 2);
}

export function filterDevices(list, query, controllableOnly) {
  const q = (query || '').trim().toLowerCase();
  return list.filter(d =>
    (!controllableOnly || isControllable(d)) &&
    (q === '' || d.friendly_name.toLowerCase().includes(q)));
}

export function suggestName(prefix, existingNames) {
  const taken = existingNames instanceof Set ? existingNames : new Set(existingNames);
  for (let i = 1; ; i++) { const n = `${prefix}_${i}`; if (!taken.has(n)) return n; }
}
