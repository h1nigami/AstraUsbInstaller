import { test } from 'node:test';
import assert from 'node:assert/strict';
import { boot, jsUrl, wait, flush } from './env.mjs';

const { server, w } = boot();
server.on('GET /api/state', () => ({
  at: '2026-10-22T12:00:00', station: 'BC-10', os: '', version: '', copying: 0, done: 0, failed: 0, free: 1, networkUp: true,
  ftpEnabled: false, ftpState: '', archiveLabel: '', archiveTotalBytes: 0, archiveFreeBytes: 0, trouble: '', bays: [],
}));
server.on('GET /api/search', { limit: 500, converter: false, rows: [] });

await import(jsUrl('app'));
const { $ } = await import(jsUrl('util'));
const { store } = await import(jsUrl('store'));

const submit = async () => {
  $('#loginForm').dispatchEvent(new w.Event('submit', { bubbles: true, cancelable: true }));
  await wait(60);
};

test('без ключа показывается вход, а смена адреса панель не открывает', async () => {
  assert.equal($('#login').hidden, false);
  assert.equal($('#panel').hidden, true);

  location.hash = 'archive';
  await flush();

  assert.equal(store.section, '');
  assert.equal($('#panel').hidden, true);
});

test('неверный пароль: сообщение и встряска карточки', async () => {
  server.on('POST /api/login', 401);

  await submit();

  assert.equal($('#loginError').textContent, 'учётная запись или пароль не подошли');
  assert.ok($('#loginForm').classList.contains('shake'));
  assert.equal($('#panel').hidden, true);
  assert.ok(!$('#loginForm button').classList.contains('busy'));
});

test('слишком много попыток: просят подождать минуту', async () => {
  server.on('POST /api/login', 429);

  await submit();

  assert.equal($('#loginError').textContent, 'слишком много попыток, подождите минуту');
});

test('станция не отвечает: об этом сказано', async () => {
  server.on('POST /api/login', () => new Error('нет сети'));

  await submit();

  assert.equal($('#loginError').textContent, 'станция не отвечает');
});

test('успешный вход запоминает ключ, открывает панель и сохраняет адрес раздела', async () => {
  server.on('POST /api/login', { token: 'NEW' });
  location.hash = 'archive';
  await flush();

  await submit();
  await wait(100);

  assert.equal(sessionStorage.getItem('token'), 'NEW');
  assert.equal($('#login').hidden, true);
  assert.equal($('#panel').hidden, false);
  assert.equal(store.section, 'archive');
  assert.equal(server.last('GET /api/state').headers['X-Token'], 'NEW');
  assert.equal($('#loginError').textContent, '');
});
