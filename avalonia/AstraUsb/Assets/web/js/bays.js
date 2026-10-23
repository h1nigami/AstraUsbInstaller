// Отсеки: плитки с кольцом заполнения и команды станции.
import { $, esc, ico } from './util.js';
import { post } from './net.js';
import { toast, openSheet, closeSheet, busy } from './ui.js';
import { store } from './store.js';

const bayClass = state =>
  ['Определение ID', 'Сканирование', 'Копирование'].includes(state) ? 'work'
    : state === 'Готово' ? 'done' : state === 'Ошибка' ? 'bad' : state === 'Свободный' ? 'idle' : '';

export const bays = {
  async enter(v) {
    v.innerHTML = '<div class="bays stagger" id="bays"></div>';
    if (store.state) this.update(store.state);
  },
  update(s) {
    const box = $('#bays');
    if (!box) return;
    if (box.children.length !== s.bays.length) {
      box.innerHTML = s.bays.map((b, i) => `
        <button class="bay" data-act="bay" data-slot="${b.slot}" style="--i:${i}">
          <span class="num">${b.slot + 1}</span>
          <span class="ring"><svg viewBox="0 0 72 72"><circle class="bg" cx="36" cy="36" r="30"/><circle class="fg" cx="36" cy="36" r="30"/></svg><span class="mid"></span></span>
          <span class="name"></span><span class="state"></span>
        </button>`).join('');
    }
    s.bays.forEach((b, i) => {
      const el = box.children[i], kind = bayClass(b.state);
      el.className = 'bay ' + kind;
      el.dataset.state = b.state;
      el.style.setProperty('--p', Math.max(0, Math.min(100, b.percent)) / 100);
      $('.name', el).textContent = b.employee || b.camera || (kind === 'idle' ? 'Свободен' : 'Без имени');
      $('.state', el).textContent = kind === 'idle' ? '' : b.state + (b.files ? ' · ' + b.files : '');
      $('.mid', el).innerHTML = kind === 'done' ? ico('check') : kind === 'bad' ? ico('alert')
        : kind === 'idle' ? ico('bolt') : b.percent > 0 ? b.percent + '%' : '…';
    });
  },
};

export const actions = {
  bay: el => {
    const slot = Number(el.dataset.slot), state = el.dataset.state;
    openSheet(`<h3>Отсек ${slot + 1}</h3><p class="sub">Состояние: ${esc(state)}. Действие выполнится на станции в течение пары секунд.</p>
      <button class="btn primary block" data-act="bayDo" data-slot="${slot}" data-do="priority">Первым в очередь</button>
      <button class="btn block" data-act="bayDo" data-slot="${slot}" data-do="charge">Только зарядка</button>
      <button class="btn block" data-act="bayDo" data-slot="${slot}" data-do="resume">Возобновить загрузку</button>
      <button class="btn soft block" data-act="close">Закрыть</button>`);
  },
  bayDo: async el => {
    const r = await busy(el, () => post(`/api/bay/${el.dataset.slot}/${el.dataset.do}`));
    if (r.ok) { closeSheet(); toast('Команда передана станции', 'ok'); setTimeout(() => store.tick(), 2200); }
  },
};
