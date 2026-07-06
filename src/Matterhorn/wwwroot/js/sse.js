// js/sse.js — the dashboard's single EventSource connection + reconnect-on-key-change.
import { apiKeyQuery } from './api.js';

const link = document.getElementById('link');
let es = null;
let currentOnFrame = null;

function open(){
  if (es) es.close();
  es = new EventSource('/api/events'+apiKeyQuery());
  es.onopen = () => link.classList.add('live');
  es.onerror = () => link.classList.remove('live');
  es.onmessage = ev => {
    let m; try { m = JSON.parse(ev.data); } catch { return; }
    currentOnFrame?.(m);
  };
}

export function connectSse(onFrame){
  currentOnFrame = onFrame;
  open();
}

export function reconnect(){
  open();
}
