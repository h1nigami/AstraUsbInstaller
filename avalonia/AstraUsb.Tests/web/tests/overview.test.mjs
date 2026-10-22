import { test } from 'node:test';
import assert from 'node:assert/strict';
import { boot, jsUrl, wait } from './env.mjs';

boot({ reducedMotion: true });
const { overview, services } = await import(jsUrl('overview'));
const { store } = await import(jsUrl('store'));
const { $, $$ } = await import(jsUrl('util'));

const GB = 1024 ** 3;
const state = (extra = {}) => ({
  copying: 2, done: 1, failed: 0, free: 7, trouble: '', archiveTotalBytes: 100 * GB, archiveFreeBytes: 60 * GB,
  archiveLabel: 'D:\\архив', version: '2.0.14', networkUp: true, ftpEnabled: true, ftpState: 'отправка идёт', ...extra,
});

test('обновление до построения страницы ничего не ломает', () => {
  document.querySelector('#view').innerHTML = '';
  overview.update(state());
});

test('обзор строится без снимка и заполняется, когда он приходит', async () => {
  store.state = null;
  await overview.enter($('#view'));
  assert.equal($$('.metric').length, 4);
  assert.equal($('#mFailed').hidden, true);

  overview.update(state());
  await wait(10);

  assert.equal($('[data-k=copying]').textContent, '2');
  assert.equal($('[data-k=done]').textContent, '1');
  assert.equal($('[data-k=free]').textContent, '7');
  assert.equal($('#mFailed').hidden, true);
  assert.equal($('#alert').hidden, true);
  assert.equal($('#vol').style.width, '40%');
  assert.ok(!$('#vol').classList.contains('low'));
  assert.match($('#volText').textContent, /свободно 60\.0 ГБ из 100\.0 ГБ/);
  assert.equal($('#volLabel').textContent, 'D:\\архив');
  assert.equal($('#ver').textContent, 'Версия: 2.0.14');
});

test('ошибки, тревога и заполненный том заметны', async () => {
  overview.update(state({ failed: 3, trouble: 'Нет места', archiveFreeBytes: 5 * GB }));
  await wait(10);

  assert.equal($('#mFailed').hidden, false);
  assert.equal($('[data-k=failed]').textContent, '3');
  assert.equal($('#alert').hidden, false);
  assert.equal($('#alertText').textContent, 'Нет места');
  assert.ok($('#vol').classList.contains('low'));
  assert.equal($('#vol').style.width, '95%');
});

test('том неизвестного размера показывает пустую полосу', () => {
  overview.update(state({ archiveTotalBytes: 0, archiveFreeBytes: 0, archiveLabel: '', version: '' }));

  assert.equal($('#vol').style.width, '0%');
  assert.equal($('#volLabel').textContent, 'архив');
  assert.equal($('#ver').textContent, 'Версия: неизвестна');
});

test('состояние служб не перерисовывается, пока не изменилось', () => {
  overview.update(state());
  const first = $('#services .service');
  overview.update(state());
  assert.equal($('#services .service'), first);

  overview.update(state({ networkUp: false, ftpEnabled: false }));
  assert.notEqual($('#services .service'), first);
  assert.equal($$('#services .dot.up').length, 2);
});

test('список служб описывает сеть и отправку', () => {
  const on = services(state());
  assert.deepEqual(on.map(x => x.k), ['Монитор USB', 'Локальная база', 'Сеть', 'Отправка']);
  assert.equal(on[2].v, 'доступна');
  assert.equal(on[3].v, 'отправка идёт');

  const off = services(state({ networkUp: false, ftpEnabled: false }));
  assert.equal(off[2].v, 'недоступна');
  assert.equal(off[3].v, 'выключена');
  assert.equal(off[3].up, false);
});

test('обзор подхватывает уже полученный снимок при открытии', async () => {
  store.state = state({ done: 9 });
  await overview.enter($('#view'));
  await wait(10);

  assert.equal($('[data-k=done]').textContent, '9');
});
