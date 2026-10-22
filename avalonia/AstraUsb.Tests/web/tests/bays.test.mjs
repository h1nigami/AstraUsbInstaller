import { test } from 'node:test';
import assert from 'node:assert/strict';
import { boot, jsUrl, wait, click, act } from './env.mjs';

const { server } = boot();
const { bays, actions } = await import(jsUrl('bays'));
const { store } = await import(jsUrl('store'));
const { $, $$ } = await import(jsUrl('util'));
const ui = await import(jsUrl('ui'));

const bay = (slot, state, percent = 0, employee = '', extra = {}) =>
  ({ slot, state, percent, employee, camera: '', files: '', ...extra });

test('обновление до построения страницы ничего не ломает', () => {
  document.querySelector('#view').innerHTML = '';
  bays.update({ bays: [bay(0, 'Свободный')] });
});

test('плитки строятся по числу отсеков и описывают состояние', async () => {
  store.state = null;
  await bays.enter($('#view'));
  bays.update({
    bays: [
      bay(0, 'Копирование', 37, 'Иван', { files: '12 из 40' }),
      bay(1, 'Готово', 100, 'Мария'),
      bay(2, 'Ошибка', 64, '', { camera: 'Охрана-1' }),
      bay(3, 'Свободный'),
      bay(4, 'Сканирование', 0, ''),
      bay(5, 'Только зарядка', 0, 'Пётр'),
      bay(6, 'Определение ID', 150, 'Анна'),
    ],
  });

  const tiles = $$('.bay');
  assert.equal(tiles.length, 7);
  assert.deepEqual(tiles.map(t => [...t.classList].filter(c => c !== 'bay')[0] ?? ''), ['work', 'done', 'bad', 'idle', 'work', '', 'work']);
  assert.equal($('.mid', tiles[0]).textContent, '37%');
  assert.equal(tiles[0].style.getPropertyValue('--p'), '0.37');
  assert.equal($('.name', tiles[0]).textContent, 'Иван');
  assert.equal($('.state', tiles[0]).textContent, 'Копирование · 12 из 40');
  assert.match($('.mid', tiles[1]).innerHTML, /<svg/);
  assert.equal($('.name', tiles[2]).textContent, 'Охрана-1');
  assert.equal($('.name', tiles[3]).textContent, 'Свободен');
  assert.equal($('.state', tiles[3]).textContent, '');
  assert.equal($('.mid', tiles[4]).textContent, '…');
  assert.equal($('.name', tiles[4]).textContent, 'Без имени');
  assert.equal(tiles[6].style.getPropertyValue('--p'), '1');
  assert.equal(tiles[5].dataset.state, 'Только зарядка');
});

test('при том же числе отсеков плитки обновляются на месте', () => {
  const first = $$('.bay')[0];

  bays.update({ bays: [bay(0, 'Готово', 100, 'Иван'), ...Array.from({ length: 6 }, (_, i) => bay(i + 1, 'Свободный'))] });

  assert.equal($$('.bay')[0], first);
  assert.ok(first.classList.contains('done'));
});

test('при другом числе отсеков плитки строятся заново', () => {
  const first = $$('.bay')[0];

  bays.update({ bays: [bay(0, 'Свободный'), bay(1, 'Свободный')] });

  assert.equal($$('.bay').length, 2);
  assert.notEqual($$('.bay')[0], first);
});

test('обзор открывается с уже известным снимком', async () => {
  store.state = { bays: [bay(0, 'Копирование', 10, 'Иван')] };
  await bays.enter($('#view'));

  assert.equal($$('.bay').length, 1);
});

test('нажатие на отсек открывает лист команд', () => {
  document.body.addEventListener('click', e => { const el = e.target.closest('[data-act]'); if (el && actions[el.dataset.act]) actions[el.dataset.act](el); });
  click($('.bay'));

  assert.match($('.sheet h3').textContent, /Отсек 1/);
  assert.equal($$('.sheet [data-act=bayDo]').length, 3);
  assert.match($('.sheet .sub').textContent, /Копирование/);
});

test('команда уходит станции, лист закрывается, состояние запрашивается заново', async () => {
  server.on('POST /api/bay/0/priority', {});
  let ticks = 0;
  store.tick = async () => { ticks++; };

  click($('[data-do=priority]'));
  await wait(30);

  assert.equal(server.last('POST /api/bay/0/priority') !== undefined, true);
  assert.equal(ui.sheetNow, null);
  assert.match($('.toast').textContent, /Команда передана/);
  await wait(2300);
  assert.equal(ticks, 1);
});

test('отказ станции оставляет лист открытым', async () => {
  ui.closeSheet(true);
  server.on('POST /api/bay/0/charge', () => ({ status: 400, body: { error: 'неизвестное действие' } }));
  click($('.bay'));

  click($('[data-do=charge]'));
  await wait(30);

  assert.ok(ui.sheetNow);
  assert.equal(act('bayDo') !== null, true);
});
