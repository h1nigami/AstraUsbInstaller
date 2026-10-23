import { test } from 'node:test';
import assert from 'node:assert/strict';
import { boot, jsUrl } from './env.mjs';

boot({ reducedMotion: true });
const u = await import(jsUrl('util'));

test('при «уменьшить движение» число ставится сразу', () => {
  const el = document.createElement('b');
  el.dataset.k = 'calm';
  u.countTo(el, 7);
  assert.equal(el.textContent, '7');
  assert.equal(u.reduced, true);
});
