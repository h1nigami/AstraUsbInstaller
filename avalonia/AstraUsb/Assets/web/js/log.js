// Журнал: события станции за период и диагностика.
import { $, esc, ico, ymd, daysAgo, empty, skeletons } from './util.js';
import { api, token } from './net.js';
import { toast, openSheet, closeSheet, sheetNow, busy, link, vals, field } from './ui.js';
import { store } from './store.js';

const L = { period: 'today', from: '', to: '', kind: '', rows: [] };
export const log = {
  async enter(v) {
    v.innerHTML = `
      <div class="toolbar"><div class="chips grow" id="lp" style="margin:0;padding:0"></div></div>
      <div class="toolbar"><div class="chips grow" id="lk" style="margin:0;padding:0"></div></div>
      <div class="toolbar"><span class="grow muted">Журнал станции и действий оператора</span>
        <button class="btn soft small" data-act="diag">${ico('download')}Диагностика</button></div>
      <div class="list" id="events"></div>`;
    logChips();
    loadLog(true);
  },
};
function logChips() {
  $('#lp').innerHTML = [['today', 'Сегодня'], ['week', '7 дней'], ['custom', 'Другой период']].map(([k, t]) =>
    `<button class="chip ${L.period === k ? 'on' : ''}" data-act="lperiod" data-v="${k}">${t}</button>`).join('');
  const kinds = [...new Set(L.rows.map(r => r.kind))];
  $('#lk').innerHTML = (kinds.length > 1 ? ['', ...kinds] : []).map(k =>
    `<button class="chip ${L.kind === k ? 'on' : ''}" data-act="lkind" data-v="${esc(k)}">${k ? esc(k) : 'Все события'}</button>`).join('');
}
async function loadLog(animate) {
  const box = $('#events');
  if (animate) box.innerHTML = skeletons(5);
  const today = ymd(new Date());
  const [from, to] = L.period === 'week' ? [ymd(daysAgo(7)), today] : L.period === 'custom' ? [L.from || today, L.to || today] : [today, today];
  const r = await api(`/api/events?from=${from}&to=${to}`);
  if (store.section !== 'log') return;
  L.rows = r.data || [];
  logChips();
  const rows = L.kind ? L.rows.filter(x => x.kind === L.kind) : L.rows;
  box.className = 'list stagger';
  box.innerHTML = rows.length === 0 ? empty('log', 'Событий нет', 'За этот период в журнале пусто.')
    : rows.map((e, i) => `<div class="event" style="--i:${i}"><div class="head"><span class="tag">${esc(e.kind)}</span>
        <time>${esc(new Date(e.at).toLocaleString('ru-RU'))}</time></div><div class="text">${esc(e.text)}</div></div>`).join('')
      + (L.rows.length >= 500 ? '<div class="count">Показаны последние 500 событий</div>' : '');
}


export const actions = {
  lperiod: el => {
    L.period = el.dataset.v;
    if (L.period === 'custom') {
      const today = ymd(new Date());
      openSheet(`<h3>Период журнала</h3><div class="row2">${field('С', 'from', L.from || today, 'type="date"')}${field('По', 'to', L.to || today, 'type="date"')}</div>
        <button class="btn primary block" data-act="applyLog">Показать</button>`);
      return;
    }
    loadLog(true);
  },
  applyLog: () => { const f = vals(sheetNow.sh); L.from = f.from; L.to = f.to; closeSheet(); loadLog(true); },
  lkind: el => { L.kind = el.dataset.v; loadLog(); },
  diag: async el => {
    const res = await busy(el, () => fetch('/api/diagnostics', { headers: { 'X-Token': token } }));
    if (!res.ok) { toast('Не удалось собрать диагностику', 'bad'); return; }
    const url = URL.createObjectURL(await res.blob());
    link(url, 'diagnostics.zip');
    setTimeout(() => URL.revokeObjectURL(url), 5000);
  },
};
