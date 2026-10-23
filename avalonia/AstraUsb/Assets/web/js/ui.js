// Всё, что видно поверх страницы: уведомления, листы, просмотр, поля форм.
import { $, $$, esc, ico } from './util.js';

export function toast(text, kind = '') {
  const el = document.createElement('div');
  el.className = 'toast ' + kind;
  el.style.setProperty('--life', '4s');
  el.textContent = text;
  $('#toasts').append(el);
  setTimeout(() => { el.classList.add('out'); setTimeout(() => el.remove(), 320); }, 4000);
}

export let sheetNow = null;

export function openSheet(html, mount) {
  closeSheet(true);
  const scrim = document.createElement('div');
  scrim.className = 'scrim';
  const sh = document.createElement('div');
  sh.className = 'sheet';
  sh.setAttribute('role', 'dialog');
  sh.setAttribute('aria-modal', 'true');
  sh.innerHTML = `<div class="grip"></div><div class="scroll">${html}</div>`;
  $('#layer').append(scrim, sh);
  document.body.style.overflow = 'hidden';
  // Два кадра: иначе браузер склеит начальное и конечное состояние и переход не сыграет.
  requestAnimationFrame(() => requestAnimationFrame(() => { scrim.classList.add('in'); sh.classList.add('in'); }));
  scrim.onclick = () => closeSheet();
  dragToClose(sh);
  sheetNow = { scrim, sh, onClose: null };
  placeThumbs(sh);
  mount?.(sh);
  return sh;
}

export function closeSheet(instant = false) {
  if (!sheetNow) return;
  const { scrim, sh, onClose } = sheetNow;
  sheetNow = null;
  document.body.style.overflow = '';
  scrim.classList.remove('in');
  sh.classList.remove('in');
  sh.style.transform = '';
  const done = () => { scrim.remove(); sh.remove(); };
  instant ? done() : setTimeout(done, 420);
  onClose?.();
}

/* Лист можно утянуть вниз за ручку, как на телефоне привыкли. */
export function dragToClose(sh) {
  const grip = $('.grip', sh);
  let y0 = null;
  grip.addEventListener('pointerdown', e => { y0 = e.clientY; grip.setPointerCapture(e.pointerId); sh.classList.add('drag'); });
  grip.addEventListener('pointermove', e => {
    if (y0 === null) return;
    sh.style.transform = `translateY(${Math.max(0, e.clientY - y0)}px)`;
  });
  const end = e => {
    if (y0 === null) return;
    const dy = e.clientY - y0;
    y0 = null;
    sh.classList.remove('drag');
    if (dy > 90) closeSheet(); else sh.style.transform = '';
  };
  grip.addEventListener('pointerup', end);
  grip.addEventListener('pointercancel', end);
}

/* Подтверждение действия. Опасное делается красной кнопкой и отдельным листом. */
export function confirmSheet({ title, text, ok = 'Подтвердить', danger = false, body = '' }) {
  return new Promise(resolve => {
    let answered = false;
    const sh = openSheet(`
      <h3>${esc(title)}</h3>
      <p class="sub">${esc(text)}</p>
      ${body}
      <button class="btn ${danger ? 'danger' : 'primary'} block" data-act="yes">${esc(ok)}</button>
      <button class="btn soft block" data-act="close">Отмена</button>`);
    $('[data-act=yes]', sh).addEventListener('click', () => {
      answered = true;
      resolve({ ok: true, values: vals(sh) });
      closeSheet();
    });
    sheetNow.onClose = () => { if (!answered) resolve({ ok: false }); };
  });
}

export function closeViewer() {
  const v = $('.viewer');
  if (!v) return;
  v.classList.remove('in');
  setTimeout(() => v.remove(), 240);
}

document.addEventListener('keydown', e => {
  if (e.key !== 'Escape') return;
  if ($('.viewer')) closeViewer(); else closeSheet();
});

export function vals(root) {
  const o = {};
  $$('[name]', root).forEach(e => {
    if (e.type === 'radio') { if (e.checked) o[e.name] = /^\d+$/.test(e.value) ? Number(e.value) : e.value; }
    else if (e.type === 'checkbox') o[e.name] = e.checked;
    else if (e.type === 'number' || e.dataset.num) o[e.name] = Number(e.value);
    else o[e.name] = e.value;
  });
  return o;
}

export const field = (label, name, value = '', attrs = '') =>
  `<div class="field"><label for="f-${name}">${esc(label)}</label><input id="f-${name}" name="${name}" value="${esc(value)}" ${attrs}></div>`;

export const toggle = (label, name, on, hint = '') =>
  `<label class="switch"><span>${esc(label)}${hint ? `<small>${esc(hint)}</small>` : ''}</span>
     <input type="checkbox" name="${name}" ${on ? 'checked' : ''}><i></i></label>`;

export const seg = (name, options, value) =>
  `<div class="seg" role="radiogroup"><span class="thumb"></span>${options.map(([v, t]) =>
    `<label><input type="radio" name="${name}" value="${v}" ${String(v) === String(value) ? 'checked' : ''}>${esc(t)}</label>`).join('')}</div>`;

export const stepper = (name, value, min, max) =>
  `<div class="stepper" data-min="${min}" data-max="${max}">
     <button type="button" class="icon-btn" data-act="step" data-d="-1" aria-label="Меньше">${ico('minus')}</button>
     <output>${value}</output><input type="hidden" name="${name}" value="${value}" data-num="1">
     <button type="button" class="icon-btn" data-act="step" data-d="1" aria-label="Больше">${ico('plus')}</button>
   </div>`;

/* Ползунок под выбранным вариантом переезжает с пружиной. */
export function placeThumbs(root = document) {
  $$('.seg', root).forEach(s => {
    const on = $('label:has(input:checked)', s), thumb = $('.thumb', s);
    if (!on || !thumb) return;
    thumb.style.width = on.offsetWidth + 'px';
    thumb.style.transform = `translateX(${on.offsetLeft - 4}px)`;
  });
}
document.addEventListener('change', e => { if (e.target.type === 'radio') placeThumbs(e.target.closest('.seg')?.parentElement || document); });

/* Кнопка занята, пока идёт запрос: повторное нажатие не уйдёт. */
export async function busy(btn, fn) {
  btn?.classList.add('busy');
  try { return await fn(); } finally { btn?.classList.remove('busy'); }
}

export function link(url, name) {
  const a = document.createElement('a');
  a.href = url; if (name) a.download = name;
  document.body.append(a); a.click(); a.remove();
}
