import { test } from 'node:test';
import assert from 'node:assert/strict';
import { boot, jsUrl, flush } from './env.mjs';

const { server } = boot();
const net = await import(jsUrl('net'));

test('ключ входа запоминается на время вкладки и уходит в заголовке', async () => {
  net.setToken('abc');
  assert.equal(sessionStorage.getItem('token'), 'abc');
  assert.equal(net.token, 'abc');
  server.on('GET /api/x', () => ({ v: 1 }));

  const r = await net.api('/api/x');

  assert.deepEqual([r.ok, r.status, r.data], [true, 200, { v: 1 }]);
  assert.equal(server.last('GET /api/x').headers['X-Token'], 'abc');
});

test('post отправляет JSON, пустое тело заменяется объектом', async () => {
  server.on('POST /api/p', ({ body }) => ({ got: body }));

  const a = await net.post('/api/p', { n: 1 });
  const b = await net.post('/api/p');

  assert.deepEqual(a.data.got, { n: 1 });
  assert.deepEqual(b.data.got, {});
  assert.equal(server.last('POST /api/p').headers['Content-Type'], 'application/json');
});

test('ошибка станции показывается уведомлением, если не просили тишины', async () => {
  server.on('POST /api/bad', () => ({ status: 400, body: { error: 'так нельзя' } }));
  server.on('POST /api/bare', () => 500);

  const loud = await net.post('/api/bad', {});
  assert.equal(loud.ok, false);
  assert.match(document.querySelector('.toast.bad').textContent, /так нельзя/);

  document.querySelector('#toasts').innerHTML = '';
  await net.post('/api/bad', {}, true);
  assert.equal(document.querySelectorAll('.toast').length, 0);

  await net.post('/api/bare', {});
  assert.match(document.querySelector('.toast').textContent, /Не удалось выполнить/);
});

test('нет связи: запрос не падает, а возвращает статус 0', async () => {
  server.on('GET /api/off', () => new Error('сеть'));

  const r = await net.api('/api/off');

  assert.deepEqual([r.ok, r.status, r.data], [false, 0, null]);
});

test('401 разлогинивает: ключ забыт, страница перезагружается', async () => {
  server.on('GET /api/secret', () => 401);
  net.setToken('old');

  const r = await net.api('/api/secret');
  await flush();

  assert.equal(r.status, 401);
  assert.equal(sessionStorage.getItem('token'), null);
  assert.equal(location.hash, '');
});

test('401 без ключа (вход ещё не выполнен) ничего не сбрасывает', async () => {
  server.on('GET /api/secret', () => 401);
  net.setToken('');
  sessionStorage.setItem('keep', '1');

  await net.api('/api/secret', { quiet: true });

  assert.equal(sessionStorage.getItem('keep'), '1');
});
