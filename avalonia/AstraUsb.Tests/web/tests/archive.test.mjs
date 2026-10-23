import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { boot, jsUrl, wait, click, act, wire, type } from './env.mjs';

const { server } = boot();
const mod = await import(jsUrl('archive'));
const { store } = await import(jsUrl('store'));
const ui = await import(jsUrl('ui'));
const { $, $$, ymd, daysAgo } = await import(jsUrl('util'));
wire(mod.actions, { closeViewer: ui.closeViewer });

const row = (file, extra = {}) => ({
  path: `Device1/${file}`, file, kind: 'Видео', camera: 'Охрана-1', employee: 'Иван', department: 'Охрана',
  size: 24224, collected: '2026-10-22T12:49:00', shot: 'ok', shotAt: '2026-10-22T11:49:00',
  important: false, note: '', formats: ['mp4', 'mov'], ...extra,
});
const rows = () => [
  row('VID_1.mp4', { important: true }),
  row('IMG_2.jpg', { kind: 'Фото', formats: ['jpg'], note: 'инцидент' }),
  row('REC_3.wav', { kind: 'Аудио', shot: 'off', shotAt: null, formats: [] }),
  row('LOG_4.txt', { kind: 'Журнал', formats: [], camera: '', employee: '' }),
];
const found = (list = rows(), extra = {}) => ({ limit: 500, converter: true, rows: list, ...extra });
const search = () => server.calls.filter(c => c.key === 'GET /api/search').at(-1).url.searchParams;
const view = () => $('#view');
const recs = () => $$('.rec');
const sel = () => $$('.rec.sel').length;

beforeEach(() => { ui.closeSheet(true); $('#layer').innerHTML = ''; $('#toasts').innerHTML = ''; window.__clicked = []; window.__opened = []; });

async function openArchive(list) {
  store.section = 'archive';
  server.on('GET /api/search', found(list));
  await mod.archive.enter(view());
  await wait(30);
}

test('архив строит страницу и показывает записи за неделю', async () => {
  await openArchive();

  assert.equal(recs().length, 4);
  assert.equal($('#count').textContent, 'Найдено: 4');
  assert.equal(search().get('from'), ymd(daysAgo(7)));
  assert.equal(search().get('protected'), '0');
  assert.equal($$('#kinds .chip').length, 5);
  assert.ok($('#kinds .chip.on').textContent.includes('Все'));
  assert.ok($('#periods .chip.on').textContent.includes('7 дней'));
  assert.ok($('#recs').classList.contains('stagger'));
});

test('запись описана: владелец, камера, метки', () => {
  const [video, photo, audio, text] = recs();

  assert.equal($('.fname', video).textContent, 'VID_1.mp4');
  assert.match($('.rec-meta', video).textContent, /Иван · камера Охрана-1 · .* · 24 КБ/);
  assert.match(video.querySelector('.tags').textContent, /защищено/);
  assert.match(photo.querySelector('.tags').textContent, /заметка/);
  assert.match(audio.querySelector('.tags').textContent, /часы сбиты/);
  assert.doesNotMatch($('.rec-meta', text).textContent, /камера/);
});

test('условие по имени уходит не на каждую букву', async () => {
  const before = server.calls.filter(c => c.key === 'GET /api/search').length;

  type($('#q'), 'V');
  type($('#q'), 'VID');
  await wait(100);
  assert.equal(server.calls.filter(c => c.key === 'GET /api/search').length, before);
  await wait(400);

  assert.equal(server.calls.filter(c => c.key === 'GET /api/search').length, before + 1);
  assert.equal(search().get('name'), 'VID');
});

test('вид и период переключаются и запрашивают заново', async () => {
  click($$('#kinds .chip')[2]);
  await wait(30);
  assert.equal(search().get('kind'), 'Photo');
  assert.ok($('#kinds .chip.on').textContent.includes('Фото'));

  click($$('#periods .chip')[0]);
  await wait(30);
  assert.equal(search().get('from'), ymd(new Date()));

  click($$('#periods .chip')[2]);
  await wait(30);
  assert.equal(search().get('from'), ymd(daysAgo(30)));

  click($$('#kinds .chip')[0]);
  click($$('#periods .chip')[1]);
  await wait(30);
});

test('условия отбора: лист с отделами, применение переводит период в «свой»', async () => {
  server.on('GET /api/departments', [{ id: 3, name: 'Охрана / Смена 1' }]);

  click(act('filters'));
  await wait(30);
  assert.match($('.sheet h3').textContent, /Условия отбора/);
  assert.match($('[name=department]').innerHTML, /Охрана \/ Смена 1/);
  $('[name=from]').value = '2026-09-01';
  $('[name=to]').value = '2026-09-30';
  $('[name=employee]').value = '  Иван ';
  $('[name=no]').value = '101';
  $('[name=camera]').value = 'Охрана';
  $('[name=department]').value = '3';
  $('[name=protectedOnly]').checked = true;
  click(act('applyFilters'));
  await wait(40);

  assert.equal(ui.sheetNow, null);
  assert.equal(search().get('from'), '2026-09-01');
  assert.equal(search().get('to'), '2026-09-30');
  assert.equal(search().get('employee'), 'Иван');
  assert.equal(search().get('no'), '101');
  assert.equal(search().get('camera'), 'Охрана');
  assert.equal(search().get('department'), '3');
  assert.equal(search().get('protected'), '1');
  assert.ok($('#filterBtn').classList.contains('on'));
});

test('условия отбора: лист помнит выбранное, даже если отделы не пришли', async () => {
  server.on('GET /api/departments', () => 500);

  click(act('filters'));
  await wait(30);

  assert.equal($('[name=employee]').value, 'Иван');
  assert.equal($('[name=from]').value, '2026-09-01');
  assert.equal($('[name=department]').options.length, 1);
  assert.equal($('[name=protectedOnly]').checked, true);
  click(act('applyFilters'));
  await wait(40);
  assert.equal(search().get('from'), '2026-09-01');
});

test('условия отбора: сброс возвращает неделю и очищает поля', async () => {
  server.on('GET /api/departments', []);
  click(act('filters'));
  await wait(30);

  click(act('resetFilters'));
  await wait(40);

  assert.equal(search().get('from'), ymd(daysAgo(7)));
  assert.equal(search().get('employee'), '');
  assert.equal(search().get('protected'), '0');
  assert.equal($('#q').value, '');
  assert.ok(!$('#filterBtn').classList.contains('on'));
});

test('выбор: галочка, панель действий, выбрать все и снять', () => {
  assert.ok(!$('#selbar').classList.contains('on'));

  click($$('.chk')[0]);
  assert.equal(sel(), 1);
  assert.ok($('#selbar').classList.contains('on'));
  assert.match($('#selbar .n').textContent, /Выбрано: 1/);

  click($$('.chk')[0]);
  assert.equal(sel(), 0);
  assert.ok(!$('#selbar').classList.contains('on'));

  click($$('.chk')[1]);
  click(act('pickAll'));
  assert.equal(sel(), 4);
  click(act('pickNone'));
  assert.equal(sel(), 0);
});

test('нажатие на запись открывает лист, а при выборе переключает галочку', () => {
  click($$('.rec-main')[1]);
  assert.match($('.sheet h3').textContent, /IMG_2.jpg/);
  ui.closeSheet(true);
  $('#layer').innerHTML = '';

  click($$('.chk')[0]);
  click($$('.rec-main')[1]);
  assert.equal(sel(), 2);
  assert.equal($$('.sheet').length, 0);
  click(act('pickNone'));
});

test('лист записи: сведения, заметка и кнопки перевода', () => {
  click($$('.rec-main')[0]);

  assert.match($('.sheet .sub').textContent, /Видео · Иван · Охрана · камера Охрана-1/);
  assert.match($$('.sheet .sub')[1].textContent, /снято/);
  assert.equal($('[data-act=sheetProtect]').textContent.trim(), 'Снять защиту');
  assert.deepEqual($$('[data-act=sheetConvert]').map(b => b.dataset.f), ['mp4', 'mov']);
  ui.closeSheet(true);
  $('#layer').innerHTML = '';

  click($$('.rec-main')[2]);
  assert.match($$('.sheet .sub')[1].textContent, /Загружено/);
  assert.doesNotMatch($$('.sheet .sub')[1].textContent, /снято/);
  assert.equal($('[data-act=sheetProtect]').textContent.trim(), 'Защитить от удаления');
  assert.equal($$('[data-act=sheetConvert]').length, 0);
});

test('лист записи: часы сбиты — об этом сказано', async () => {
  server.on('GET /api/search', found([row('x.mp4', { shot: 'off' })]));
  click($$('#kinds .chip')[0]);
  await wait(30);
  ui.closeSheet(true);
  $('#layer').innerHTML = '';

  click($('.rec-main'));

  assert.match($$('.sheet .sub')[1].textContent, /часы камеры сбиты/);
});

test('без конвертера кнопок перевода нет', async () => {
  server.on('GET /api/search', found(rows(), { converter: false }));
  click($$('#kinds .chip')[0]);
  await wait(30);
  ui.closeSheet(true);
  $('#layer').innerHTML = '';

  click($$('.rec-main')[0]);

  assert.equal($$('[data-act=sheetConvert]').length, 0);
});

test('результаты без выбранной записи убирают её из выбора', async () => {
  server.on('GET /api/search', found(rows()));
  click($$('#kinds .chip')[0]);
  await wait(30);
  click($$('.chk')[0]);
  click($$('.chk')[1]);
  assert.equal(sel(), 2);

  server.on('GET /api/search', found(rows().slice(1)));
  click($$('#periods .chip')[1]);
  await wait(30);

  assert.equal(sel(), 1);
  click(act('pickNone'));
});

test('пустая выдача, предел и сбой объясняются', async () => {
  server.on('GET /api/search', found([]));
  click($$('#periods .chip')[2]);
  await wait(30);
  assert.match($('#recs').textContent, /Записей нет/);
  assert.equal($('#count').textContent, '');

  server.on('GET /api/search', found(rows(), { limit: 4 }));
  click($$('#periods .chip')[1]);
  await wait(30);
  assert.match($('#count').textContent, /Показаны первые 4/);

  server.on('GET /api/search', () => 500);
  click($$('#periods .chip')[2]);
  await wait(30);
  assert.match($('#recs').textContent, /Архив не открылся/);
});

test('устаревший ответ не перебивает новый', async () => {
  let release;
  server.on('GET /api/search', async () => { await new Promise(r => { release = r; }); return found([row('СТАРЫЙ.mp4')]); });
  click($$('#periods .chip')[0]);
  await wait(10);
  const slow = release;
  server.on('GET /api/search', found([row('НОВЫЙ.mp4')]));
  click($$('#periods .chip')[1]);
  await wait(30);

  slow();
  await wait(30);

  assert.equal($('.fname').textContent, 'НОВЫЙ.mp4');
});

test('ответ после ухода из раздела игнорируется', async () => {
  server.on('GET /api/search', found(rows()));
  click($$('#periods .chip')[2]);
  store.section = 'log';
  const html = $('#recs').innerHTML;
  await wait(30);

  assert.equal($('#recs').innerHTML, html);
  store.section = 'archive';
});

test('защита: выбранные защищаются, если среди них есть незащищённые, иначе снимается', async () => {
  await openArchive();
  server.on('POST /api/archive/protect', ({ body }) => ({ count: body.paths.length }));
  click($$('.chk')[0]);
  click($$('.chk')[1]);

  click(act('bulkProtect'));
  await wait(30);
  assert.equal(server.last('POST /api/archive/protect').body.on, true);
  assert.match($('.toast.ok').textContent, /Защищено записей: 2/);
  assert.equal($$('.rec .tag.shield').length, 2);

  $('#toasts').innerHTML = '';
  click(act('bulkProtect'));
  await wait(30);
  assert.equal(server.last('POST /api/archive/protect').body.on, false);
  assert.match($('.toast.ok').textContent, /Защита снята с записей: 2/);
  click(act('pickNone'));
});

test('защита: отказ станции ничего не меняет', async () => {
  server.on('POST /api/archive/protect', () => 500);
  click($$('.chk')[3]);
  const before = $$('.rec .tag.shield').length;

  click(act('bulkProtect'));
  await wait(30);

  assert.equal($$('.rec .tag.shield').length, before);
  click(act('pickNone'));
});

test('лист записи: защита переключается и закрывает лист', async () => {
  server.on('POST /api/archive/protect', { count: 1 });
  click($$('.rec-main')[3]);

  click(act('sheetProtect'));
  await wait(30);

  assert.deepEqual(server.last('POST /api/archive/protect').body, { paths: ['Device1/LOG_4.txt'], on: true });
  assert.equal(ui.sheetNow, null);
});

test('заметка сохраняется и обрезается; отказ оставляет лист', async () => {
  server.on('POST /api/archive/note', {});
  click($$('.rec-main')[3]);
  $('[name=note]').value = '  по заявке  ';

  click(act('sheetNote'));
  await wait(30);

  assert.deepEqual(server.last('POST /api/archive/note').body, { paths: ['Device1/LOG_4.txt'], note: '  по заявке  ' });
  assert.match($$('.rec')[3].textContent, /заметка/);
  assert.match($('.toast.ok').textContent, /Заметка сохранена/);
  assert.equal(ui.sheetNow, null);

  server.on('POST /api/archive/note', () => 500);
  click($$('.rec-main')[0]);
  click(act('sheetNote'));
  await wait(30);
  assert.ok(ui.sheetNow);
});

test('перевод формата: успех обновляет список, неудача объясняется', async () => {
  server.on('POST /api/archive/convert', { ok: true, message: 'VID_1.mov' });
  click($$('.rec-main')[0]);
  const before = server.calls.filter(c => c.key === 'GET /api/search').length;

  click($('[data-f=mov]'));
  await wait(60);

  assert.deepEqual(server.last('POST /api/archive/convert').body, { paths: ['Device1/VID_1.mp4'], format: 'mov' });
  assert.match($('.toast.ok').textContent, /Копия готова: VID_1.mov/);
  assert.equal(ui.sheetNow, null);
  assert.equal(server.calls.filter(c => c.key === 'GET /api/search').length, before + 1);

  server.on('POST /api/archive/convert', { ok: false, message: 'ffmpeg не найден' });
  click($$('.rec-main')[0]);
  click($('[data-f=mp4]'));
  await wait(30);
  assert.match($('.toast.bad').textContent, /ffmpeg не найден/);
  assert.ok(ui.sheetNow);

  server.on('POST /api/archive/convert', () => 500);
  click($('[data-f=mp4]'));
  await wait(30);
});

test('просмотр: видео, фото и звук открываются поверх страницы', async () => {
  server.on('POST /api/ticket', { ticket: 'T1' });

  for (const [index, tag] of [[0, 'video'], [1, 'img'], [2, 'audio']]) {
    click($$('.rec-acts [data-act=watch]')[index]);
    await wait(100);
    const media = $(`.viewer ${tag}`);
    assert.ok(media, tag);
    assert.match(media.getAttribute('src'), /^\/api\/file\?p=Device1%2F.*&t=T1$/);
    $('#layer').innerHTML = '';
  }
  assert.equal(server.calls.filter(c => c.key === 'POST /api/ticket').length, 1);
});

test('просмотр: фото получает подпись, кнопка и подложка закрывают его', async () => {
  click($$('.rec-acts [data-act=watch]')[1]);
  await wait(100);
  assert.equal($('.viewer img').alt, 'IMG_2.jpg');
  assert.ok($('.viewer').classList.contains('in'));

  click($('.viewer'));
  await wait(300);
  assert.equal($$('.viewer').length, 0);
});

test('просмотр: журнал открывается в новой вкладке, видео закрывается крестиком', async () => {
  click($$('.rec-acts [data-act=watch]')[3]);
  await wait(60);
  assert.equal(window.__opened.length, 1);
  assert.match(window.__opened[0], /LOG_4.txt/);

  click($$('.rec-acts [data-act=watch]')[0]);
  await wait(100);
  click($('.viewer .x'));
  await wait(300);
  assert.equal($$('.viewer').length, 0);
});

test('просроченный билет запрашивается заново, а при отказе станции просмотра нет', async () => {
  $('#layer').innerHTML = '';
  window.__opened = [];
  const real = Date.now;
  Date.now = () => real() + 26 * 60e3;
  try {
    server.on('POST /api/ticket', { ticket: 'T3' });
    click($$('.rec-acts [data-act=watch]')[3]);
    await wait(60);
    assert.match(window.__opened.at(-1), /t=T3/);

    Date.now = () => real() + 60 * 60e3;
    server.on('POST /api/ticket', () => 500);
    window.__opened = [];
    click($$('.rec-acts [data-act=watch]')[3]);
    click($$('.rec-acts [data-act=save]')[0]);
    await wait(60);
    assert.equal(window.__opened.length, 0);
    assert.equal($$('.viewer').length, 0);
  } finally {
    Date.now = real;
  }
});

test('скачивание: одна запись по прямой ссылке, несколько — одним архивом', async () => {
  server.on('POST /api/ticket', { ticket: 'T2' });
  server.on('POST /api/archive/pack', { id: 'PK1' });

  click($$('.rec-acts [data-act=save]')[0]);
  await wait(40);
  assert.match(window.__clicked.at(-1).href, /\/api\/file\?p=Device1%2FVID_1.mp4&t=/);
  assert.equal(window.__clicked.at(-1).download, 'VID_1.mp4');

  click($$('.chk')[0]);
  click($$('.chk')[1]);
  click(act('bulkSave'));
  await wait(40);
  assert.deepEqual(server.last('POST /api/archive/pack').body.paths, ['Device1/VID_1.mp4', 'Device1/IMG_2.jpg']);
  assert.match(window.__clicked.at(-1).href, /\/api\/zip\?id=PK1&t=/);

  click(act('pickNone'));
  click($$('.rec-main')[0]);
  click(act('sheetSave'));
  await wait(40);
  assert.match(window.__clicked.at(-1).href, /VID_1.mp4/);
});

test('скачивание: отказ в упаковке не создаёт ссылку', async () => {
  server.on('POST /api/archive/pack', () => 500);
  window.__clicked = [];
  click($$('.chk')[0]);
  click($$('.chk')[1]);

  click(act('bulkSave'));
  await wait(40);

  assert.equal(window.__clicked.length, 0);
  click(act('pickNone'));
});

test('лист записи: просмотр из листа', async () => {
  click($$('.rec-main')[0]);

  click(act('sheetWatch'));
  await wait(100);

  assert.ok($('.viewer video'));
  $('#layer').innerHTML = '';
});

test('удаление: подтверждение, пропуск защищённых, повторный поиск', async () => {
  await openArchive();
  server.on('POST /api/archive/delete', { deleted: 1, skipped: 1, failed: 0 });
  click($$('.chk')[0]);
  click($$('.chk')[1]);

  click(act('bulkDelete'));
  await wait(30);
  assert.match($('.sheet h3').textContent, /Удалить записи: 2\?/);
  const before = server.calls.filter(c => c.key === 'GET /api/search').length;
  click($('.sheet [data-act=yes]'));
  await wait(60);

  assert.deepEqual(server.last('POST /api/archive/delete').body.paths, ['Device1/VID_1.mp4', 'Device1/IMG_2.jpg']);
  assert.match($('.toast').textContent, /удалено: 1, пропущено защищённых: 1/);
  assert.equal($$('.rec.leaving').length, 1);
  assert.ok(!$('#selbar').classList.contains('on'));
  await wait(450);
  assert.equal(server.calls.filter(c => c.key === 'GET /api/search').length, before + 1);
});

test('удаление: сбои названы, одна запись спрашивается отдельной фразой', async () => {
  server.on('POST /api/archive/delete', { deleted: 0, skipped: 0, failed: 2 });
  click($$('.rec-main')[3]);

  click(act('sheetDelete'));
  await wait(30);
  assert.match($('.sheet h3').textContent, /Удалить запись\?/);
  click($('.sheet [data-act=yes]'));
  await wait(500);

  assert.match($('.toast.bad').textContent, /не удалось: 2/);
});

test('удаление: отказ и ошибка станции ничего не меняют', async () => {
  click($$('.chk')[3]);
  click(act('bulkDelete'));
  await wait(30);
  click($('.sheet [data-act=close]'));
  await wait(30);
  assert.equal(sel(), 1);

  server.on('POST /api/archive/delete', () => 500);
  click(act('bulkDelete'));
  await wait(30);
  click($('.sheet [data-act=yes]'));
  await wait(30);
  assert.equal(sel(), 1);
  click(act('pickNone'));
});

test('снятие выбора при уходе из раздела', () => {
  click($$('.chk')[0]);

  mod.clearSelection();

  assert.equal(sel(), 1);
  assert.ok(!$('#selbar').classList.contains('on'));
});

test('панель действий не показывается вне архива', () => {
  click($$('.chk')[0]);
  store.section = 'log';
  mod.clearSelection();

  assert.ok(!$('#selbar').classList.contains('on'));
  store.section = 'archive';
});
