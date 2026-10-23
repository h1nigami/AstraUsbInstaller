import { test } from 'node:test';
import assert from 'node:assert/strict';
import { boot, jsUrl, wait, click } from './env.mjs';

boot();
const ui = await import(jsUrl('ui'));
const { $, $$ } = await import(jsUrl('util'));

const reset = () => { document.body.style.overflow = ''; $('#layer').innerHTML = ''; $('#toasts').innerHTML = ''; };

test('уведомление появляется и само уходит', () => {
  reset();
  ui.toast('Готово', 'ok');
  assert.equal($('.toast.ok').textContent, 'Готово');
  assert.equal($('.toast').style.getPropertyValue('--life'), '4s');
});

test('лист открывается, блокирует прокрутку и закрывается по подложке', async () => {
  reset();
  let mounted = null;
  const sh = ui.openSheet('<h3>Привет</h3>', el => { mounted = el; });
  await wait(100);

  assert.equal(mounted, sh);
  assert.equal(document.body.style.overflow, 'hidden');
  assert.ok(sh.classList.contains('in'));
  assert.equal(ui.sheetNow.sh, sh);

  click($('.scrim'));
  assert.equal(ui.sheetNow, null);
  assert.equal(document.body.style.overflow, '');
  await wait(450);
  assert.equal($$('.sheet').length, 0);
});

test('второй лист сразу заменяет первый, закрытие без листа безвредно', () => {
  reset();
  ui.closeSheet();
  ui.openSheet('<b>1</b>');
  ui.openSheet('<b>2</b>');
  assert.equal($$('.sheet').length, 1);
  assert.equal($('.sheet b').textContent, '2');
  ui.closeSheet(true);
  assert.equal($$('.sheet').length, 0);
});

test('лист утягивается вниз за ручку: далеко закрывается, близко возвращается', () => {
  reset();
  const sh = ui.openSheet('x');
  const grip = $('.grip', sh);
  const ev = (type, y) => grip.dispatchEvent(new window.MouseEvent(type, { clientY: y, bubbles: true }));

  ev('pointermove', 500);
  ev('pointerup', 500);
  ev('pointerdown', 100);
  ev('pointermove', 150);
  assert.equal(sh.style.transform, 'translateY(50px)');
  assert.ok(sh.classList.contains('drag'));
  ev('pointermove', 20);
  assert.equal(sh.style.transform, 'translateY(0px)');
  ev('pointerup', 150);
  assert.equal(sh.style.transform, '');
  assert.ok(ui.sheetNow);

  ev('pointerdown', 100);
  ev('pointerup', 300);
  assert.equal(ui.sheetNow, null);
});

test('подтверждение возвращает значения полей, отмена даёт «нет»', async () => {
  reset();
  const yes = ui.confirmSheet({ title: 'Удалить?', text: 'Навсегда', ok: 'Да', danger: true, body: ui.field('Пароль', 'pw', 'abc') });
  assert.match($('.sheet').textContent, /Удалить\?/);
  assert.ok($('.sheet .btn.danger'));
  click($('[data-act=yes]'));
  assert.deepEqual(await yes, { ok: true, values: { pw: 'abc' } });

  const no = ui.confirmSheet({ title: 'Точно?', text: '' });
  assert.ok($('.sheet .btn.primary'));
  ui.closeSheet();
  assert.deepEqual(await no, { ok: false });
});

test('Escape закрывает просмотр, затем лист; другие клавиши ничего не трогают', async () => {
  reset();
  ui.closeViewer();
  const v = document.createElement('div');
  v.className = 'viewer in';
  $('#layer').append(v);
  ui.openSheet('x');

  document.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Enter' }));
  assert.ok(ui.sheetNow);
  document.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape' }));
  assert.ok(!v.classList.contains('in'));
  await wait(300);
  assert.equal($$('.viewer').length, 0);

  document.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape' }));
  assert.equal(ui.sheetNow, null);
});

test('значения формы собираются с типами', () => {
  document.body.innerHTML = `<form>
    <input name="t" value="текст"><input name="n" type="number" value="5"><input name="h" data-num="1" value="7">
    <input name="c" type="checkbox" checked><input name="u" type="checkbox">
    <input name="r" type="radio" value="1"><input name="r" type="radio" value="2" checked>
    <input name="s" type="radio" value="да"><input name="s" type="radio" value="нет" checked></form>`;

  assert.deepEqual(ui.vals(document.body), { t: 'текст', n: 5, h: 7, c: true, u: false, r: 2, s: 'нет' });
});

test('поля форм рисуются с подписью и экранированием', () => {
  assert.match(ui.field('Имя', 'nm', '<x>', 'required'), /for="f-nm".*value="&lt;x&gt;" required/s);
  assert.match(ui.toggle('Звук', 'snd', true, 'подсказка'), /<small>подсказка.*checked/s);
  assert.doesNotMatch(ui.toggle('Звук', 'snd', false), /checked|small/);
  const seg = ui.seg('m', [[0, 'Один'], [1, 'Два']], 1);
  assert.match(seg, /value="1" checked/);
  assert.doesNotMatch(seg, /value="0" checked/);
  assert.match(ui.stepper('cnt', 8, 6, 30), /data-min="6" data-max="30".*value="8"/s);
});

test('ползунок сегмента ставится под выбранный вариант', () => {
  document.body.innerHTML = ui.seg('m', [[0, 'Один'], [1, 'Два']], 1);
  const second = $$('label')[1];
  Object.defineProperty(second, 'offsetWidth', { value: 90 });
  Object.defineProperty(second, 'offsetLeft', { value: 100 });

  ui.placeThumbs();
  assert.equal($('.thumb').style.width, '90px');
  assert.equal($('.thumb').style.transform, 'translateX(96px)');

  $('input[value="0"]').checked = true;
  $('input[value="0"]').dispatchEvent(new window.Event('change', { bubbles: true }));
  assert.equal($('.thumb').style.width, '0px');

  document.body.innerHTML = '<div class="seg"></div>';
  ui.placeThumbs();
});

test('занятая кнопка помечается и освобождается, даже если запрос упал', async () => {
  const b = document.createElement('button');
  const seen = [];
  const r = await ui.busy(b, async () => { seen.push(b.classList.contains('busy')); return 5; });
  assert.equal(r, 5);
  assert.deepEqual(seen, [true]);
  assert.ok(!b.classList.contains('busy'));

  await assert.rejects(ui.busy(b, async () => { throw new Error('x'); }));
  assert.ok(!b.classList.contains('busy'));
  await ui.busy(null, async () => {});
});

test('ссылка на скачивание создаётся и нажимается', () => {
  window.__clicked = [];
  ui.link('/api/file?p=a', 'a.mp4');
  ui.link('/x');
  assert.equal(window.__clicked.length, 2);
  assert.equal(window.__clicked[0].download, 'a.mp4');
  assert.equal($$('a').length, 0);
});
