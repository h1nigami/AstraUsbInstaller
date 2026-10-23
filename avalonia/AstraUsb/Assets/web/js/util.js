// Мелкие помощники без зависимостей: выборка, экранирование, значки, форматы.

export const $ = (s, r = document) => r.querySelector(s);
export const $$ = (s, r = document) => [...r.querySelectorAll(s)];
export const esc = t => String(t ?? '').replace(/[&<>"']/g,
  c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
export const reduced = matchMedia('(prefers-reduced-motion: reduce)').matches;
export const sleep = ms => new Promise(r => setTimeout(r, ms));

/* ---------- значки ---------- */
export const ICONS = {
  home: 'M3 11l9-8 9 8M5 10v10h5v-6h4v6h5V10',
  bays: 'M4 4h7v7H4zM13 4h7v7h-7zM4 13h7v7H4zM13 13h7v7h-7z',
  archive: 'M3 5h18v4H3zM5 9v10h14V9M10 13h4',
  camera: 'M4 8h3l2-3h6l2 3h3v11H4zM12 17a3.5 3.5 0 100-7 3.5 3.5 0 000 7z',
  people: 'M16 20v-1a4 4 0 00-4-4H7a4 4 0 00-4 4v1M9.5 11a3.5 3.5 0 100-7 3.5 3.5 0 000 7zM21 20v-1a4 4 0 00-3-3.9M16 4.1a3.5 3.5 0 010 6.8',
  log: 'M5 4h14v16H5zM9 9h6M9 13h6M9 17h3',
  settings: 'M4 7h10M18 7h2M4 17h2M10 17h10M16 4v6M8 14v6',
  more: 'M5 12h.01M12 12h.01M19 12h.01',
  filter: 'M4 6h16M7 12h10M10 18h4',
  play: 'M8 5v14l11-7z',
  download: 'M12 4v11M7 11l5 5 5-5M5 20h14',
  shield: 'M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z',
  trash: 'M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13',
  check: 'M5 12l5 5 9-10',
  x: 'M6 6l12 12M18 6L6 18',
  plus: 'M12 5v14M5 12h14',
  minus: 'M5 12h14',
  convert: 'M4 8h13l-3-3M20 16H7l3 3',
  logout: 'M10 4H5v16h5M15 8l4 4-4 4M19 12H9',
  bolt: 'M13 3L5 14h6l-1 7 8-11h-6z',
  refresh: 'M20 11a8 8 0 10-2.3 5.7M20 4v7h-7',
  chevron: 'M9 6l6 6-6 6',
  alert: 'M12 4l9 16H3zM12 10v4M12 17h.01',
  server: 'M4 5h16v6H4zM4 13h16v6H4zM8 8h.01M8 16h.01',
  lock: 'M6 11h12v9H6zM8 11V8a4 4 0 118 0v3',
  box: 'M4 8l8-4 8 4v8l-8 4-8-4zM4 8l8 4 8-4M12 12v8',
  info: 'M12 21a9 9 0 100-18 9 9 0 000 18zM12 11v5M12 8h.01',
  select: 'M4 5h16M4 12h16M4 19h16',
};
export const ico = (n, cls = '') =>
  `<svg class="ico ${cls}" viewBox="0 0 24 24" aria-hidden="true"${n === 'more' ? ' style="stroke-width:3.6"' : ''}><path d="${ICONS[n]}"/></svg>`;

/* ---------- форматы ---------- */
export const gb = b => (b / 1024 / 1024 / 1024).toFixed(1) + ' ГБ';
export const size = b => b >= 1024 ** 3 ? gb(b) : b >= 1024 ** 2 ? (b / 1024 ** 2).toFixed(1) + ' МБ' : Math.max(1, Math.round(b / 1024)) + ' КБ';
export const when = t => new Date(t).toLocaleString('ru-RU', { day: '2-digit', month: '2-digit', year: '2-digit', hour: '2-digit', minute: '2-digit' });
export const ymd = d => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
export const daysAgo = n => { const d = new Date(); d.setDate(d.getDate() - n); return d; };
export const initials = s => (String(s || '?').trim().split(/\s+/).slice(0, 2).map(w => w[0]).join('') || '?').toUpperCase();

export const shown = new Map();
export function countTo(el, n) {
  const key = el.dataset.k, from = shown.get(key) ?? 0;
  shown.set(key, n);
  if (reduced || from === n) { el.textContent = n; return; }
  const t0 = performance.now();
  (function step(t) {
    const p = Math.min(1, (t - t0) / 700);
    el.textContent = Math.round(from + (n - from) * (1 - (1 - p) ** 3));
    if (p < 1) requestAnimationFrame(step);
  })(t0);
}

export const skeletons = n => `<div class="list">${'<div class="skeleton"></div>'.repeat(n)}</div>`;
export const empty = (icon, title, text) =>
  `<div class="empty">${ico(icon)}<b>${esc(title)}</b><span>${esc(text)}</span></div>`;

