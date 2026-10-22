'use strict';

/* ======================================================================
   Веб-панель станции BestCam BC-10.
   Без сборщика и библиотек. Разделы строят страницу один раз и дальше
   меняют отдельные элементы на месте: так кольца, полосы и счётчики
   плавно доезжают до новых значений, а набранный в поле текст не пропадает.
   ====================================================================== */
import { $, $$, esc, ico, reduced } from './util.js';
import { api, post, token, setToken, logout } from './net.js';
import { toast, openSheet, closeSheet, confirmSheet, closeViewer, busy, placeThumbs } from './ui.js';
import { store } from './store.js';
import { overview, services } from './overview.js';
import { bays, actions as bayActions } from './bays.js';
import { archive, actions as archiveActions, clearSelection } from './archive.js';
import { devices, actions as deviceActions } from './devices.js';
import { staff, actions as staffActions } from './staff.js';
import { log, actions as logActions } from './log.js';
import { settings, actions as settingsActions } from './settings.js';

/* ======================================================================
   Разделы
   ====================================================================== */
const SECTIONS = [
  { id: 'overview', label: 'Обзор', icon: 'home' },
  { id: 'bays', label: 'Отсеки', icon: 'bays' },
  { id: 'archive', label: 'Архив', icon: 'archive' },
  { id: 'devices', label: 'Камеры', icon: 'camera' },
  { id: 'staff', label: 'Сотрудники', icon: 'people' },
  { id: 'log', label: 'Журнал', icon: 'log' },
  { id: 'settings', label: 'Настройки', icon: 'settings' },
];
const MAIN = ['overview', 'bays', 'archive'];
const order = SECTIONS.map(s => s.id);
let entering = 0;

/* Действия кнопок разделов собираются в одну таблицу: страница слушает один обработчик клика. */
const actions = {
  goto: el => { location.hash = el.dataset.to; },
  refresh: () => tick(true),
  closeViewer, close: () => closeSheet(),

  logout: async () => {
    const r = await confirmSheet({ title: 'Выйти из панели?', text: 'Вход забудется, и панель попросит пароль заново.', ok: 'Выйти' });
    if (r.ok) logout();
  },
  restart: async () => {
    const r = await confirmSheet({ title: 'Перезапустить станцию?', text: 'Программа закроется и поднимется заново. Незавершённые загрузки продолжатся после запуска.', ok: 'Перезапустить', danger: true });
    if (r.ok && (await post('/api/restart')).ok) toast('Станция перезапускается. Панель вернётся через минуту', 'ok');
  },
  more: () => {
    const rest = SECTIONS.filter(s => !MAIN.includes(s.id));
    openSheet(`<h3>Разделы</h3><div class="menu">${rest.map(s =>
      `<button class="btn" data-act="menuGo" data-to="${s.id}">${ico(s.icon)}${s.label}</button>`).join('')}
      <button class="btn soft" data-act="menuOut">${ico('logout')}Выйти</button></div>`);
  },
  menuGo: el => { closeSheet(); location.hash = el.dataset.to; },
  menuOut: () => { closeSheet(true); actions.logout(); },
};
Object.assign(actions, bayActions, archiveActions, deviceActions, staffActions, logActions, settingsActions);

document.addEventListener('click', e => {
  const el = e.target.closest('[data-act]');
  if (el && actions[el.dataset.act]) actions[el.dataset.act](el, e);
});

/* Волна от места нажатия на кнопке. */
document.addEventListener('pointerdown', e => {
  const b = e.target.closest('.btn');
  if (!b || reduced) return;
  const r = b.getBoundingClientRect(), d = Math.max(r.width, r.height) * 2;
  const w = document.createElement('span');
  w.className = 'ripple';
  w.style.cssText = `width:${d}px;height:${d}px;left:${e.clientX - r.left - d / 2}px;top:${e.clientY - r.top - d / 2}px`;
  b.append(w);
  setTimeout(() => w.remove(), 600);
});

/* ======================================================================
   Каркас: вход, меню, опрос
   ====================================================================== */
const sections = { overview, bays, archive, devices, staff, log, settings };

function drawNav() {
  const item = (s, act) => `<button data-act="${act}" data-to="${s.id}" data-id="${s.id}">${ico(s.icon)}<span>${s.label}</span></button>`;
  $('#sideNav').innerHTML = '<span class="thumb"></span>' + SECTIONS.map(s => item(s, 'goto')).join('');
  $('#tabs').innerHTML = '<span class="thumb"></span>'
    + SECTIONS.filter(s => MAIN.includes(s.id)).map(s => item(s, 'goto')).join('')
    + `<button data-act="more" data-id="more">${ico('more')}<span>Ещё</span></button>`;
}

function markNav() {
  if (!store.section) return;
  const tab = MAIN.includes(store.section) ? store.section : 'more';
  $$('#sideNav button').forEach(b => b.classList.toggle('on', b.dataset.id === store.section));
  $$('#tabs button').forEach(b => b.classList.toggle('on', b.dataset.id === tab));
  $('#desk').textContent = SECTIONS.find(s => s.id === store.section)?.label || '';

  const side = $('#sideNav .thumb');
  side.style.transform = `translateY(${order.indexOf(store.section) * 52}px)`;
  const on = $('#tabs button.on'), thumb = $('#tabs .thumb');
  if (on && on.offsetWidth) {
    thumb.style.width = on.offsetWidth + 'px';
    thumb.style.transform = `translateX(${on.offsetLeft - 10}px)`;
  }
}
addEventListener('resize', () => { markNav(); placeThumbs(); });

async function route() {
  if ($('#panel').hidden) return; // до входа каркаса ещё нет
  const id = order.includes(location.hash.slice(1)) ? location.hash.slice(1) : 'overview';
  if (id === store.section) return;
  const dir = order.indexOf(id) > order.indexOf(store.section) ? 'fwd' : 'back', first = store.section === '';
  store.section = id;
  closeSheet(true);
  clearSelection();
  markNav();
  scrollTo({ top: 0 });
  const v = $('#view');
  v.className = 'view';
  void v.offsetWidth; // перезапуск анимации появления
  v.className = 'view ' + (first ? '' : dir);
  const n = ++entering;
  await sections[id].enter(v);
  if (n !== entering) return;
}
addEventListener("hashchange", route);
store.tick = tick;

function paintHeader(s) {
  $('#station').textContent = s.station || 'BC-10';
  $('#place').textContent = (s.station || '').split('·').slice(1).join('·').trim();
  $('#clock').textContent = new Date(s.at).toLocaleTimeString('ru-RU') + (s.os ? ' · ' + s.os : '');
  $('#sidePath').textContent = s.archiveLabel || '';
  const html = services(s).map(x => `<span class="pill"><span class="dot ${x.up ? 'up' : ''}"></span>${esc(x.k)} · ${esc(x.v)}</span>`).join('');
  if ($('#pills').dataset.h !== html) { $('#pills').dataset.h = html; $('#pills').innerHTML = html; }
}

let misses = 0;
async function tick(manual) {
  const r = await api('/api/state', { quiet: true });
  if (!r.ok) {
    if (r.status !== 401 && ++misses >= 2) $('#offline').classList.add('on');
    if (manual) toast('Станция не отвечает', 'bad');
    return;
  }
  misses = 0;
  $('#offline').classList.remove('on');
  store.state = r.data;
  paintHeader(store.state);
  sections[store.section]?.update?.(store.state);
  if (manual) toast('Состояние обновлено', 'ok');
}

function start() {
  $('#login').hidden = true;
  $('#panel').hidden = false;
  drawNav();
  route().then(() => tick());
  setInterval(() => { if (!document.hidden) tick(); }, 3000);
  document.addEventListener('visibilitychange', () => { if (!document.hidden) tick(); });
}

$('#loginForm').addEventListener('submit', async e => {
  e.preventDefault();
  const card = e.currentTarget, err = $('#loginError');
  const btn = $('button', card);
  err.textContent = '';
  const res = await busy(btn, () => fetch('/api/login', { method: 'POST', body: new FormData(card) }).catch(() => null));
  if (res?.ok) { setToken((await res.json()).token); start(); return; }
  err.textContent = !res ? 'станция не отвечает'
    : res.status === 429 ? 'слишком много попыток, подождите минуту' : 'учётная запись или пароль не подошли';
  card.classList.remove('shake'); void card.offsetWidth; card.classList.add('shake');
});

if (token) start();
