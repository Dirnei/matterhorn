// js/api.js — API key storage + the fetch wrapper shared by every REST call.
import { toast } from './ui.js';

let apiKey = localStorage.getItem('mh_key') || '';

export function getApiKey(){ return apiKey; }
export function setApiKey(k){ apiKey = k; localStorage.setItem('mh_key', apiKey); }

function headers(extra){ return Object.assign(apiKey?{'X-Api-Key':apiKey}:{}, extra||{}); }

export async function api(path, opts){
  const r = await fetch(path, Object.assign({ headers: headers(opts&&opts.body?{'Content-Type':'application/json'}:{}) }, opts));
  if (r.status===401){ toast('Unauthorized — check the API key'); throw new Error('401'); }
  return r;
}

export function apiKeyQuery(){ return getApiKey() ? '?api_key='+encodeURIComponent(getApiKey()) : ''; }
