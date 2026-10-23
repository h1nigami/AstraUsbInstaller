import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { boot, jsUrl, wait, click, act, wire } from './env.mjs';

const { server } = boot();
const mod = await import(jsUrl('log'));
const { store } = await import(jsUrl('store'));
const ui = await import(jsUrl('ui'));
const { $, $$, ymd, daysAgo } = await import(jsUrl('util'));
wire(mod.actions);

const view = () => $('#view');
const events = [
  { at: '2026-10-22T10:00:00', kind: 'доступ', text: 'вход <admin>' },
  { at: '2026-10-22T10:05:00', kind: 'выгрузка', text: 'скачана запись' },
  { at: '2026-10-22T10:06:00', kind: 'доступ', text: 'выход' },
];
const lastQuery = () => server.calls.filter(c => c.key === 'GET /api/events').at(-1).url.searchParams;

beforeEach(() => { ui.closeSheet(true); $('#layer').innerHTML = ''; $('#toasts').innerHTML = ''; });

test('журнал не рисуется, если раздел уже закрыт', async () => {
  store.section = 'staff';
  server.on('GET /api/events', events);
  await mod.log.enter(view());
  await wait(20);
  assert.equal($$('.event').length, 0);
});

test('журнал за сегодня: события экранируются, виды становятся кнопками', async () => {
  store.section = 'log';
  await mod.log.enter(view());
  await wait(20);

  assert.equal($$('.event').length, 3);
  assert.match($('.event .text').innerHTML, /вход &lt;admin&gt;/);
  assert.equal(lastQuery().get('from'), ymd(new Date()));
  assert.equal(lastQuery().get('to'), ymd(new Date()));
  assert.deepEqual($$('#lk .chip').map(c => c.textContent), ['Все события', 'доступ', 'выгрузка']);
  assert.ok($('#lp .chip.on').textContent.includes('Сегодня'));
});

test('виды фильтруют уже полученное', async () => {
  click($$('#lk .chip')[1]);
  await wait(20);

  assert.equal($$('.event').length, 2);
  assert.ok($('#lk .chip.on').textContent.includes('доступ'));

  click($$('#lk .chip')[0]);
  await wait(20);
  assert.equal($$('.event').length, 3);
});

test('семь дней берут неделю', async () => {
  click($$('#lp .chip')[1]);
  await wait(20);

  assert.equal(lastQuery().get('from'), ymd(daysAgo(7)));
});

test('другой период спрашивает даты листом и применяет их', async () => {
  click($$('#lp .chip')[2]);
  assert.match($('.sheet h3').textContent, /Период журнала/);
  $('[name=from]').value = '2026-10-01';
  $('[name=to]').value = '2026-10-15';

  click(act('applyLog'));
  await wait(20);

  assert.equal(ui.sheetNow, null);
  assert.equal(lastQuery().get('from'), '2026-10-01');
  assert.equal(lastQuery().get('to'), '2026-10-15');
});

test('другой период с уже выбранными датами показывает их же', () => {
  click($$('#lp .chip')[2]);

  assert.equal($('[name=from]').value, '2026-10-01');
});

test('пустой журнал и сбой чтения объясняются', async () => {
  server.on('GET /api/events', []);
  click($$('#lp .chip')[0]);
  await wait(20);
  assert.match(view().textContent, /Событий нет/);
  assert.equal($$('#lk .chip').length, 0);

  server.on('GET /api/events', () => 500);
  click($$('#lp .chip')[0]);
  await wait(20);
  assert.match(view().textContent, /Событий нет/);
});

test('на пределе в 500 событий об этом сказано', async () => {
  server.on('GET /api/events', Array.from({ length: 500 }, (_, i) => ({ at: '2026-10-22T10:00:00', kind: 'доступ', text: 'e' + i })));
  click($$('#lp .chip')[0]);
  await wait(50);

  assert.match(view().textContent, /Показаны последние 500 событий/);
});

test('диагностика скачивается архивом', async () => {
  window.__clicked = [];
  server.on('GET /api/diagnostics', {});

  click(act('diag'));
  await wait(30);

  assert.equal(window.__clicked.at(-1).download, 'diagnostics.zip');
  assert.ok(server.last('GET /api/diagnostics').headers['X-Token'] !== undefined);
});

test('сбой сборки диагностики показан уведомлением', async () => {
  window.__clicked = [];
  server.on('GET /api/diagnostics', () => 500);

  click(act('diag'));
  await wait(30);

  assert.equal(window.__clicked.length, 0);
  assert.match($('.toast.bad').textContent, /Не удалось собрать диагностику/);
});
