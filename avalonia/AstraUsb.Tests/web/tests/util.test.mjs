import { test } from 'node:test';
import assert from 'node:assert/strict';
import { boot, jsUrl, wait } from './env.mjs';

boot();
const u = await import(jsUrl('util'));

test('выборка и экранирование', () => {
  document.body.innerHTML = '<p class="a">1</p><p class="a">2</p>';
  assert.equal(u.$('.a').textContent, '1');
  assert.equal(u.$$('.a').length, 2);
  assert.equal(u.$('.a', document.body) instanceof window.Element, true);
  assert.equal(u.esc('<a href="x">\'&</a>'), '&lt;a href=&quot;x&quot;&gt;&#39;&amp;&lt;/a&gt;');
  assert.equal(u.esc(null), '');
  assert.equal(u.esc(0), '0');
});

test('значок рисуется по имени, у «ещё» линия толще', () => {
  assert.match(u.ico('home'), /<svg class="ico "/);
  assert.match(u.ico('check', 'big'), /class="ico big"/);
  assert.match(u.ico('more'), /stroke-width:3.6/);
  assert.doesNotMatch(u.ico('home'), /stroke-width/);
});

test('размеры и даты', () => {
  assert.equal(u.gb(1024 ** 3), '1.0 ГБ');
  assert.equal(u.size(2 * 1024 ** 3), '2.0 ГБ');
  assert.equal(u.size(5 * 1024 ** 2), '5.0 МБ');
  assert.equal(u.size(10), '1 КБ');
  assert.equal(u.size(4096), '4 КБ');
  assert.equal(u.ymd(new Date(2026, 0, 5)), '2026-01-05');
  const d = u.daysAgo(3);
  assert.equal(Math.round((Date.now() - d) / 864e5), 3);
  assert.match(u.when('2026-10-22T12:49:00'), /22\.10\.26/);
});

test('инициалы', () => {
  assert.equal(u.initials('Иван Петров Сидорович'), 'ИП');
  assert.equal(u.initials('мария'), 'М');
  assert.equal(u.initials(''), '?');
  assert.equal(u.initials(null), '?');
});

test('пустое состояние и заглушки загрузки', () => {
  assert.match(u.empty('log', 'Пусто', 'Нет <событий>'), /<b>Пусто<\/b><span>Нет &lt;событий&gt;<\/span>/);
  assert.equal((u.skeletons(3).match(/skeleton/g) || []).length, 3);
});

test('счётчик доезжает до значения и запоминает его', async () => {
  const el = document.createElement('b');
  el.dataset.k = 'x';
  u.countTo(el, 10);
  await wait(900);
  assert.equal(el.textContent, '10');
  u.countTo(el, 10);
  assert.equal(el.textContent, '10');
  u.countTo(el, 4);
  await wait(900);
  assert.equal(el.textContent, '4');
});
