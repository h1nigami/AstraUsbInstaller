import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { boot, jsUrl, wait, click, flush } from './env.mjs';

const { server, w } = boot({ token: 'KEY' });
const GB = 1024 ** 3;
const state = (extra = {}) => ({
  at: '2026-10-22T12:00:00', station: 'BC-10 · Склад', os: 'Windows', version: '2.0', copying: 1, done: 0, failed: 0, free: 5,
  networkUp: true, ftpEnabled: false, ftpState: '', archiveLabel: 'D:\\архив', archiveTotalBytes: 100 * GB, archiveFreeBytes: 50 * GB,
  trouble: '', bays: [{ slot: 0, state: 'Свободный', percent: 0, employee: '', camera: '', files: '' }], ...extra,
});
server.on('GET /api/state', () => state());
server.on('GET /api/search', { limit: 500, converter: false, rows: [] });
server.on('GET /api/devices', { devices: [], staff: [] });
server.on('GET /api/staff', []);
server.on('GET /api/events', []);
server.on('GET /api/settings', () => 500);

await import(jsUrl('app'));
await wait(80);
const { $, $$ } = await import(jsUrl('util'));
const ui = await import(jsUrl('ui'));
const { store } = await import(jsUrl('store'));

const go = async hash => { location.hash = hash; await wait(80); };
const states = () => server.calls.filter(c => c.key === 'GET /api/state').length;

beforeEach(() => { ui.closeSheet(true); $('#layer').innerHTML = ''; $('#toasts').innerHTML = ''; });

test('с сохранённым ключом панель открывается сразу на обзоре', () => {
  assert.equal($('#login').hidden, true);
  assert.equal($('#panel').hidden, false);
  assert.equal(store.section, 'overview');
  assert.equal($('#desk').textContent, 'Обзор');
  assert.equal($$('#sideNav button').length, 7);
  assert.equal($$('#tabs button').length, 4);
  assert.ok($('#tabs button[data-id=overview]').classList.contains('on'));
  assert.equal(server.calls[0].headers['X-Token'], 'KEY');
});

test('шапка показывает станцию, точку, время и службы', () => {
  assert.equal($('#station').textContent, 'BC-10 · Склад');
  assert.equal($('#place').textContent, 'Склад');
  assert.match($('#clock').textContent, /Windows/);
  assert.equal($('#sidePath').textContent, 'D:\\архив');
  assert.equal($$('#pills .pill').length, 4);
});

test('опрос приносит состояние в открытый раздел', async () => {
  await wait(900);
  assert.equal($('[data-k=copying]').textContent, '1');
  assert.equal(store.state.station, 'BC-10 · Склад');
});

test('переход по разделам: меню, заголовок, направление и повтор не перезагружают', async () => {
  await go('bays');
  assert.equal(store.section, 'bays');
  assert.equal($('#desk').textContent, 'Отсеки');
  assert.ok($('#view').classList.contains('fwd'));
  assert.ok($('#sideNav button[data-id=bays]').classList.contains('on'));
  assert.equal($$('.bay').length, 1);

  await go('overview');
  assert.ok($('#view').classList.contains('back'));

  const html = $('#view').innerHTML;
  location.hash = 'overview';
  await wait(30);
  assert.equal($('#view').innerHTML, html);
});

test('все разделы открываются и подсвечивают «Ещё», если их нет в нижней полосе', async () => {
  for (const id of ['archive', 'devices', 'staff', 'log', 'settings']) {
    await go(id);
    assert.equal(store.section, id);
  }
  assert.ok($('#tabs button[data-id=more]').classList.contains('on'));
  assert.match($('#view').textContent, /Настройки не открылись/);

  await go('archive');
  assert.ok($('#tabs button[data-id=archive]').classList.contains('on'));
});

test('неизвестный раздел ведёт на обзор', async () => {
  await go('нет-такого');
  assert.equal(store.section, 'overview');
});

test('нижняя полоса: ползунок встаёт под выбранный пункт', async () => {
  const on = $('#tabs button.on');
  Object.defineProperty(on, 'offsetWidth', { value: 80, configurable: true });
  Object.defineProperty(on, 'offsetLeft', { value: 110, configurable: true });

  await go('bays');
  await go('overview');
  window.dispatchEvent(new w.Event('resize'));

  assert.equal($('#sideNav .thumb').style.transform, 'translateY(0px)');
});

test('«Ещё» открывает меню остальных разделов и выход', async () => {
  click($('#tabs [data-act=more]'));
  assert.deepEqual($$('.menu .btn').map(b => b.textContent.trim()), ['Камеры', 'Сотрудники', 'Журнал', 'Настройки', 'Выйти']);

  click($('[data-act=menuGo][data-to=log]'));
  await wait(80);
  assert.equal(store.section, 'log');
  assert.equal(ui.sheetNow, null);
});

test('«Выйти» из меню спрашивает подтверждение и забывает ключ', async () => {
  click($('#tabs [data-act=more]'));
  click($('[data-act=menuOut]'));
  await flush();
  assert.match($('.sheet h3').textContent, /Выйти из панели/);

  click($('.sheet [data-act=yes]'));
  await wait(30);

  assert.equal(sessionStorage.getItem('token'), null);
});

test('отказ от выхода оставляет ключ', async () => {
  sessionStorage.setItem('token', 'KEY');
  click($('#sideNav ~ .side-foot [data-act=logout]'));
  await flush();

  click($('.sheet [data-act=close]'));
  await wait(30);

  assert.equal(sessionStorage.getItem('token'), 'KEY');
});

test('перезапуск станции подтверждается и отправляется', async () => {
  server.on('POST /api/restart', {});
  await go('overview');

  click($('[data-act=restart]'));
  await flush();
  assert.match($('.sheet h3').textContent, /Перезапустить станцию/);
  click($('.sheet [data-act=yes]'));
  await wait(40);

  assert.ok(server.last('POST /api/restart'));
  assert.match($('.toast.ok').textContent, /Станция перезапускается/);
});

test('отказ от перезапуска ничего не отправляет', async () => {
  server.calls.length = 0;
  click($('[data-act=restart]'));
  await flush();
  click($('.sheet [data-act=close]'));
  await wait(30);

  assert.equal(server.calls.filter(c => c.key === 'POST /api/restart').length, 0);
});

test('кнопка «Обновить» запрашивает состояние и сообщает об этом', async () => {
  const before = states();

  click($('[data-act=refresh]'));
  await wait(40);

  assert.equal(states(), before + 1);
  assert.match($('.toast.ok').textContent, /Состояние обновлено/);
});

test('станция не отвечает: после двух промахов появляется полоса, потом исчезает', async () => {
  server.on('GET /api/state', () => new Error('нет сети'));
  await store.tick();
  assert.ok(!$('#offline').classList.contains('on'));
  await store.tick();
  assert.ok($('#offline').classList.contains('on'));

  click($('[data-act=refresh]'));
  await wait(40);
  assert.match($('.toast.bad').textContent, /Станция не отвечает/);

  server.on('GET /api/state', () => state());
  await store.tick();
  assert.ok(!$('#offline').classList.contains('on'));
});

test('возврат на вкладку сразу обновляет состояние', async () => {
  const before = states();
  Object.defineProperty(document, 'hidden', { value: false, configurable: true });

  document.dispatchEvent(new w.Event('visibilitychange'));
  await wait(40);
  assert.equal(states(), before + 1);

  Object.defineProperty(document, 'hidden', { value: true, configurable: true });
  document.dispatchEvent(new w.Event('visibilitychange'));
  await wait(40);
  assert.equal(states(), before + 1);
});

test('кнопка волной отвечает на нажатие', async () => {
  const b = $('[data-act=refresh]');
  b.getBoundingClientRect = () => ({ left: 10, top: 10, width: 100, height: 40 });

  b.dispatchEvent(new w.MouseEvent('pointerdown', { bubbles: true, clientX: 50, clientY: 30 }));
  assert.equal($$('.ripple', b).length, 1);
  assert.match($('.ripple', b).style.cssText, /width: 200px/);
  await wait(650);
  assert.equal($$('.ripple', b).length, 0);

  document.body.dispatchEvent(new w.MouseEvent('pointerdown', { bubbles: true }));
});

test('клик по элементу без действия ничего не делает', () => {
  const stray = document.createElement('button');
  stray.dataset.act = 'нет-такого';
  document.body.append(stray);

  click(stray);
  click(document.body);
});
