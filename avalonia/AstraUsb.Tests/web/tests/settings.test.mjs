import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { boot, jsUrl, wait, click, act, wire, flush } from './env.mjs';

const { server } = boot();
const mod = await import(jsUrl('settings'));
const { store } = await import(jsUrl('store'));
const ui = await import(jsUrl('ui'));
const { $, $$ } = await import(jsUrl('util'));
wire(mod.actions);

const view = () => $('#view');
const config = (extra = {}) => ({
  version: 'версия 2.0.14',
  storage: { backupRoot: 'D:\\архив', minFreeGb: 50, stationNumber: 1, stationPlace: '', storageMode: 0,
    deleteVideoAfterCopy: false, keepDays: 0, alarmSound: true, voiceHints: false, onSystemDrive: false },
  bays: { count: 10, perRow: 3 },
  lockMinutes: 10,
  account: { name: 'admin', defaultPassword: false },
  ftp: { enabled: true, host: 'ftp.local', port: 21, user: 'u', folder: '/in', ssl: false, hasPassword: true, queue: 'в очереди 2' },
  sql: { enabled: false, host: '', port: 3306, database: '', user: '', hasPassword: false, error: null },
  web: { port: 8099, ssl: true },
  ...extra,
});
const open = id => click($(`#acc-${id} > button`));
const status = id => $(`#acc-${id} .status`);

beforeEach(() => { ui.closeSheet(true); $('#layer').innerHTML = ''; $('#toasts').innerHTML = ''; });

test('настройки не рисуются в чужом разделе и сообщают о сбое', async () => {
  server.on('GET /api/settings', config());
  store.section = 'log';
  await mod.settings.enter(view());
  assert.equal($$('.acc').length, 0);

  store.section = 'settings';
  server.on('GET /api/settings', () => 500);
  await mod.settings.enter(view());
  assert.match(view().textContent, /Настройки не открылись/);
});

test('разделы построены, версия и пути показаны, пароли не показаны', async () => {
  server.on('GET /api/settings', config());
  await mod.settings.enter(view());

  assert.deepEqual($$('.acc').map(a => a.id), ['acc-storage', 'acc-bays', 'acc-ftp', 'acc-sql', 'acc-access', 'acc-about']);
  assert.match($('#acc-storage small').textContent, /D:\\архив/);
  assert.equal($('#acc-storage .callout'), null);
  assert.equal($('#acc-ftp [name=password]').placeholder, 'не менять');
  assert.equal($('#acc-sql [name=password]').placeholder, '');
  assert.equal($('#acc-ftp [name=host]').value, 'ftp.local');
  assert.match($('#acc-ftp').textContent, /в очереди 2/);
  assert.match($('#acc-about').textContent, /порт 8099, SSL/);
  assert.equal($('#acc-sql .callout'), null);
});

test('предупреждения: системный диск, заводской пароль, неподдерживаемая база', async () => {
  server.on('GET /api/settings', config({
    storage: { ...config().storage, backupRoot: '', onSystemDrive: true },
    account: { name: 'admin', defaultPassword: true },
    sql: { ...config().sql, enabled: true, error: 'нет драйвера' },
    web: { port: 8080, ssl: false },
    version: '',
    ftp: { ...config().ftp, enabled: false },
  }));
  await mod.settings.enter(view());

  assert.match($('#acc-storage .callout').textContent, /системном диске/);
  assert.match($('#acc-storage small').textContent, /папка архива не выбрана/);
  assert.match($('#acc-access .callout').textContent, /заводским/);
  assert.match($('#acc-sql .callout').textContent, /нет драйвера/);
  assert.match($('#acc-ftp small').textContent, /выключена/);
  assert.match($('#acc-about small').textContent, /версия неизвестна/);
  assert.match($('#acc-about').textContent, /порт 8080(?!, SSL)/);
  server.on('GET /api/settings', config());
  await mod.settings.enter(view());
});

test('раздел раскрывается и сворачивается', async () => {
  open('storage');
  assert.ok($('#acc-storage').classList.contains('open'));
  assert.equal($('#acc-storage > button').getAttribute('aria-expanded'), 'true');
  await wait(40);
  open('storage');
  assert.ok(!$('#acc-storage').classList.contains('open'));
  assert.equal($('#acc-storage > button').getAttribute('aria-expanded'), 'false');
});

test('хранилище отправляется с типами и подтверждается', async () => {
  server.on('POST /api/settings/storage', {});
  $('#acc-storage [name=minFreeGb]').value = '70';
  $('#acc-storage [name=storageMode][value="1"]').checked = true;
  $('#acc-storage [name=alarmSound]').checked = false;

  click($('#acc-storage [data-act=saveStorage]'));
  await wait(30);

  const body = server.last('POST /api/settings/storage').body;
  assert.equal(body.minFreeGb, 70);
  assert.equal(body.storageMode, 1);
  assert.equal(body.alarmSound, false);
  assert.equal(body.backupRoot, 'D:\\архив');
  assert.equal(status('storage').textContent, 'Хранилище сохранено');
  assert.ok(status('storage').classList.contains('ok'));
  assert.match($('.toast.ok').textContent, /Хранилище сохранено/);
});

test('отказ станции показывается в разделе и уведомлением, один раз', async () => {
  server.on('POST /api/settings/storage', () => ({ status: 400, body: { error: 'диск не подключён' } }));

  click($('#acc-storage [data-act=saveStorage]'));
  await wait(30);

  assert.equal(status('storage').textContent, 'диск не подключён');
  assert.ok(status('storage').classList.contains('bad'));
  assert.equal($$('.toast').length, 1);

  server.on('POST /api/settings/storage', () => 500);
  click($('#acc-storage [data-act=saveStorage]'));
  await wait(30);
  assert.equal(status('storage').textContent, 'Не удалось сохранить');
});

test('счётчик окон ограничен пределами', () => {
  open('bays');
  const step = d => click($(`#acc-bays [data-d="${d}"]`));
  const value = () => $('#acc-bays [name=bayCount]').value;

  step(1);
  assert.equal(value(), '11');
  for (let i = 0; i < 40; i++) step(1);
  assert.equal(value(), '30');
  for (let i = 0; i < 40; i++) step(-1);
  assert.equal(value(), '6');
  assert.equal($('#acc-bays output').textContent, '6');
});

test('окна сохраняются и предлагают перезапуск', async () => {
  server.on('POST /api/settings/bays', {});
  assert.equal($('#bayNote').hidden, true);

  click($('#acc-bays [data-act=saveBays]'));
  await wait(30);

  assert.deepEqual(server.last('POST /api/settings/bays').body, { bayCount: 6, baysPerRow: 3 });
  assert.equal($('#bayNote').hidden, false);
  assert.equal($('#bayRestart').hidden, false);
});

test('отправка и база: сохранение, проверка, повтор, отправка', async () => {
  server.on('POST /api/settings/ftp', {});
  server.on('POST /api/settings/sql', {});
  server.on('POST /api/ftp/test', { ok: true, message: 'подключение установлено' });
  server.on('POST /api/ftp/retry', { ok: true, message: 'возвращено в очередь: 1' });
  server.on('POST /api/sql/test', { ok: false, message: 'нет ответа' });
  server.on('POST /api/sql/sync', { ok: true, message: 'отправлено 5' });
  open('ftp');
  open('sql');

  click($('#acc-ftp [data-act=saveFtp]'));
  click($('#acc-sql [data-act=saveSql]'));
  click(act('testFtp'));
  click(act('retryFtp'));
  click(act('testSql'));
  click(act('syncSql'));
  await wait(50);

  assert.equal(server.last('POST /api/settings/ftp').body.host, 'ftp.local');
  assert.equal(server.last('POST /api/settings/ftp').body.enabled, true);
  assert.equal(status('ftp').textContent, 'возвращено в очередь: 1');
  assert.ok(status('ftp').classList.contains('ok'));
  assert.equal(status('sql').textContent, 'отправлено 5');
  assert.ok(server.last('POST /api/sql/test'));
});

test('проверка с отрицательным итогом красит статус', async () => {
  server.on('POST /api/sql/test', { ok: false, message: 'нет ответа' });

  click(act('testSql'));
  await wait(30);

  assert.equal(status('sql').textContent, 'нет ответа');
  assert.ok(status('sql').classList.contains('bad'));
});

test('проверка, которой станция не ответила, молчит', async () => {
  server.on('POST /api/sql/test', () => 500);
  const before = $$('.toast').length;

  click(act('testSql'));
  await wait(30);

  assert.ok($$('.toast').length >= before);
});

test('пароль меняется, поля очищаются; отказ оставляет введённое', async () => {
  open('access');
  server.on('POST /api/settings/password', () => ({ status: 400, body: { error: 'текущий пароль не подошёл' } }));
  $('#acc-access [name=current]').value = 'старый';
  $('#acc-access [name=next]').value = 'новый-пароль';
  click(act('savePassword'));
  await wait(30);
  assert.equal($('#acc-access [name=current]').value, 'старый');

  server.on('POST /api/settings/password', {});
  click(act('savePassword'));
  await wait(30);
  assert.equal($('#acc-access [name=current]').value, '');
  assert.equal($('#acc-access [name=next]').value, '');
  assert.equal(status('access').textContent, 'Пароль изменён');
});

test('время блокировки сохраняется числом', async () => {
  server.on('POST /api/settings/lock', {});
  $('#acc-access [name=minutes]').value = '15';

  click(act('saveLock'));
  await wait(30);

  assert.deepEqual(server.last('POST /api/settings/lock').body, { minutes: 15 });
});

test('проверка обновлений сообщает ответ станции', async () => {
  server.on('POST /api/update/check', { ok: false, message: 'дождитесь конца копирования' });
  open('about');

  click(act('checkUpdate'));
  await wait(30);

  assert.equal(status('about').textContent, 'дождитесь конца копирования');
});

test('заводской сброс просит пароль и отправляет его', async () => {
  server.on('POST /api/factory-reset', { ok: true, message: 'удалено: устройств 2. Станция перезапускается' });

  click(act('reset'));
  await flush();
  assert.match($('.sheet h3').textContent, /Заводской сброс/);
  assert.equal($('.sheet [name=password]').type, 'password');
  $('.sheet [name=password]').value = 'секрет';
  click($('.sheet [data-act=yes]'));
  await wait(40);

  assert.deepEqual(server.last('POST /api/factory-reset').body, { password: 'секрет' });
  assert.match($('.toast.ok').textContent, /удалено: устройств 2/);
});

test('отказ от заводского сброса ничего не отправляет', async () => {
  server.calls.length = 0;

  click(act('reset'));
  await flush();
  click($('.sheet [data-act=close]'));
  await wait(40);

  assert.equal(server.calls.length, 0);
});

test('неверный пароль заводского сброса не даёт уведомления об успехе', async () => {
  server.on('POST /api/factory-reset', () => ({ status: 400, body: { error: 'пароль не подошёл' } }));

  click(act('reset'));
  await flush();
  click($('.sheet [data-act=yes]'));
  await wait(40);

  assert.match($('.toast.bad').textContent, /пароль не подошёл/);
  assert.equal($$('.toast.ok').length, 0);
});
