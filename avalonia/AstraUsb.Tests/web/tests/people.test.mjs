import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { boot, jsUrl, wait, click, act, wire, flush } from './env.mjs';

const { server } = boot();
const dev = await import(jsUrl('devices'));
const stf = await import(jsUrl('staff'));
const { store } = await import(jsUrl('store'));
const ui = await import(jsUrl('ui'));
const { $, $$ } = await import(jsUrl('util'));
wire(dev.actions, stf.actions);

const view = () => $('#view');
beforeEach(() => { ui.closeSheet(true); $('#layer').innerHTML = ''; $('#toasts').innerHTML = ''; });
const devices = {
  devices: [
    { id: 1, name: 'Охрана-1', number: 'BCU-1', employeeId: 7, employee: 'Иван', department: 'Охрана', firstSeen: '2026-01-01T10:00:00', lastSeen: '2026-10-20T10:00:00' },
    { id: 2, name: '', number: '', employeeId: null, employee: '', department: '', firstSeen: '2026-02-01T10:00:00', lastSeen: '2026-10-21T10:00:00' },
  ],
  staff: [{ id: 7, name: 'Иван', no: '101' }, { id: 8, name: 'Мария', no: '' }],
};

test('камеры: пока раздел не открыт, список не рисуется', async () => {
  store.section = 'bays';
  server.on('GET /api/devices', devices);
  await dev.devices.enter(view());
  assert.equal($$('.skeleton').length, 4);
});

test('камеры: список с именами, номерами и владельцами', async () => {
  store.section = 'devices';
  await dev.devices.enter(view());

  const items = $$('.item');
  assert.equal(items.length, 2);
  assert.equal($('.t', items[0]).textContent, 'Охрана-1');
  assert.match($('.s', items[0]).textContent, /Иван · Охрана · была/);
  assert.equal($('.t', items[1]).textContent, 'Камера 2');
  assert.match($('.s', items[1]).textContent, /ни за кем не закреплена/);
  assert.equal($('.avatar', items[0]).textContent, 'О');
});

test('камеры: пустой список и сбой объясняются', async () => {
  server.on('GET /api/devices', { devices: [], staff: [] });
  await dev.devices.enter(view());
  assert.match(view().textContent, /Камеры ещё не подключались/);

  server.on('GET /api/devices', () => 500);
  await dev.devices.enter(view());
  assert.match(view().textContent, /Список не открылся/);
});

test('камеры: лист показывает имя, номер и выбранного сотрудника', async () => {
  server.on('GET /api/devices', devices);
  await dev.devices.enter(view());

  click($$('.item')[0]);

  assert.match($('.sheet h3').textContent, /Охрана-1/);
  assert.match($('.sheet .sub').textContent, /номер BCU-1/);
  assert.equal($('[name=name]').value, 'Охрана-1');
  assert.equal($('[name=employee]').value, '7');
  assert.match($('[name=employee]').innerHTML, /Мария<\/option>/);
  assert.match($('[name=employee]').innerHTML, /Иван · № 101/);

  ui.closeSheet(true);
  click($$('.item')[1]);
  assert.match($('.sheet h3').textContent, /Камера 2/);
  assert.equal($('[name=employee]').value, '');
});

test('камеры: сохранение отправляет имя и сотрудника и перечитывает список', async () => {
  server.on('POST /api/devices/2/name', {});
  server.on('POST /api/devices/2/employee', {});
  click($$('.item')[1]);
  $('[name=name]').value = 'Склад';
  $('[name=employee]').value = '8';
  const before = server.calls.filter(c => c.key === 'GET /api/devices').length;

  click(act('saveDevice'));
  await wait(60);

  assert.deepEqual(server.last('POST /api/devices/2/name').body, { name: 'Склад' });
  assert.deepEqual(server.last('POST /api/devices/2/employee').body, { employeeId: 8 });
  assert.equal(server.calls.filter(c => c.key === 'GET /api/devices').length, before + 1);
  assert.match($('.toast.ok').textContent, /Камера сохранена/);
  assert.equal(ui.sheetNow, null);
});

test('камеры: снятие сотрудника шлёт null', async () => {
  server.on('POST /api/devices/1/name', {});
  server.on('POST /api/devices/1/employee', {});
  click($$('.item')[0]);
  $('[name=employee]').value = '';

  click(act('saveDevice'));
  await wait(60);

  assert.equal(server.last('POST /api/devices/1/employee').body.employeeId, null);
});

test('камеры: отказ при сохранении имени не доходит до сотрудника', async () => {
  server.on('POST /api/devices/1/name', () => ({ status: 400, body: { error: 'нельзя' } }));
  server.on('POST /api/devices/1/employee', () => { throw new Error('не должно вызываться'); });
  click($$('.item')[0]);

  click(act('saveDevice'));
  await wait(60);

  assert.ok(ui.sheetNow);
});

const people = [
  { id: 1, no: '101', name: 'Иван Петров', phone: '+7 900', active: true },
  { id: 2, no: '102', name: 'Мария', phone: '', active: true },
];

test('сотрудники: список, счётчик и кнопка добавления', async () => {
  store.section = 'staff';
  server.on('GET /api/staff', people);
  await stf.staff.enter(view());

  assert.match(view().textContent, /Сотрудников: 2/);
  const items = $$('.item');
  assert.equal(items.length, 2);
  assert.match($('.s', items[0]).textContent, /№ 101 · \+7 900/);
  assert.equal($('.s', items[1]).textContent, '№ 102');
  assert.ok(act('person', view()));
});

test('сотрудники: пустой справочник и сбой объясняются, чужой раздел не рисуется', async () => {
  server.on('GET /api/staff', []);
  await stf.staff.enter(view());
  assert.match(view().textContent, /Сотрудников пока нет/);

  server.on('GET /api/staff', () => 500);
  await stf.staff.enter(view());
  assert.match(view().textContent, /Справочник не открылся/);

  store.section = 'log';
  const html = view().innerHTML;
  await stf.staff.enter(view());
  assert.notEqual(view().innerHTML, html);
  store.section = 'staff';
});

test('сотрудники: новая карточка добавляется', async () => {
  server.on('GET /api/staff', people);
  await stf.staff.enter(view());
  server.on('POST /api/staff', {});

  click(act('person', view()));
  assert.match($('.sheet h3').textContent, /Новый сотрудник/);
  assert.equal($('.sheet [data-act=deletePerson]'), null);
  $('[name=no]').value = '103';
  $('[name=name]').value = 'Пётр';
  $('[name=phone]').value = '555';
  click(act('savePerson'));
  await wait(60);

  assert.deepEqual(server.last('POST /api/staff').body, { id: null, no: '103', name: 'Пётр', phone: '555' });
  assert.match($('.toast.ok').textContent, /Карточка сохранена/);
});

test('сотрудники: карточка правится, ошибка оставляет лист открытым', async () => {
  click($$('.item')[0]);
  assert.match($('.sheet h3').textContent, /Карточка сотрудника/);
  assert.equal($('[name=no]').value, '101');
  server.on('POST /api/staff', () => ({ status: 400, body: { error: 'номер 101 уже занят' } }));

  click(act('savePerson'));
  await wait(60);

  assert.equal(server.last('POST /api/staff').body.id, 1);
  assert.ok(ui.sheetNow);
  ui.closeSheet(true);
});

test('сотрудники: удаление спрашивает подтверждение', async () => {
  server.on('DELETE /api/staff/1', {});
  click($$('.item')[0]);

  click(act('deletePerson'));
  await flush();
  assert.match($('.sheet h3').textContent, /Удалить «Иван Петров»\?/);
  click($('[data-act=yes]'));
  await wait(60);

  assert.ok(server.last('DELETE /api/staff/1'));
  assert.match($('.toast.ok:last-child').textContent, /Сотрудник удалён/);
});

test('сотрудники: отказ от удаления ничего не отправляет', async () => {
  click($$('.item')[0]);
  const calls = server.calls.length;
  click(act('deletePerson'));
  await flush();

  click($('.sheet [data-act=close]'));
  await wait(30);

  assert.equal(server.calls.filter(c => c.key.startsWith('DELETE')).length, 1);
  assert.ok(server.calls.length >= calls);
});
