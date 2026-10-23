// Сотрудники: справочник с карточками.
import { $, esc, ico, initials, empty, skeletons } from './util.js';
import { api, post } from './net.js';
import { toast, openSheet, closeSheet, sheetNow, confirmSheet, busy, vals, field } from './ui.js';
import { store } from './store.js';

let STAFF = [];
export const staff = {
  async enter(v) {
    v.innerHTML = skeletons(4);
    const r = await api('/api/staff');
    if (store.section !== 'staff') return;
    if (!r.ok) { v.innerHTML = empty('alert', 'Справочник не открылся', 'Попробуйте обновить страницу.'); return; }
    STAFF = r.data;
    v.innerHTML = `
      <div class="toolbar"><span class="grow muted">Сотрудников: ${STAFF.length}</span>
        <button class="btn primary small" data-act="person" data-id="">${ico('plus')}Добавить</button></div>
      ${STAFF.length === 0 ? empty('people', 'Сотрудников пока нет', 'Добавьте номер и имя: по номеру станция узнаёт человека в записях камеры.')
        : `<div class="list stagger">${STAFF.map((p, i) => `
          <button class="item" style="--i:${i}" data-act="person" data-id="${p.id}">
            <span class="avatar">${esc(initials(p.name))}</span>
            <span class="body"><span class="t" style="display:block">${esc(p.name)}</span>
              <span class="s" style="display:block">№ ${esc(p.no)}${p.phone ? ' · ' + esc(p.phone) : ''}</span></span>${ico('chevron')}</button>`).join('')}</div>`}`;
  },
};

export const actions = {
  person: el => {
    const p = STAFF.find(x => String(x.id) === el.dataset.id);
    openSheet(`
      <h3>${p ? 'Карточка сотрудника' : 'Новый сотрудник'}</h3>
      ${field('Номер сотрудника', 'no', p?.no, 'inputmode="numeric" placeholder="Прописан в самой камере"')}
      ${field('Имя', 'name', p?.name, 'autocomplete="off"')}
      ${field('Телефон', 'phone', p?.phone, 'type="tel"')}
      <button class="btn primary block" data-act="savePerson" data-id="${p?.id ?? ''}">${p ? 'Сохранить' : 'Добавить сотрудника'}</button>
      ${p ? `<button class="btn danger block" data-act="deletePerson" data-id="${p.id}">${ico('trash')}Удалить</button>` : ''}
      <button class="btn soft block" data-act="close">Отмена</button>`);
  },
  savePerson: async el => {
    const r = await busy(el, () => post('/api/staff', { id: el.dataset.id ? Number(el.dataset.id) : null, ...vals(sheetNow.sh) }));
    if (r.ok) { closeSheet(); toast('Карточка сохранена', 'ok'); staff.enter($('#view')); }
  },
  deletePerson: async el => {
    const p = STAFF.find(x => String(x.id) === el.dataset.id);
    closeSheet(true);
    const c = await confirmSheet({ title: `Удалить «${p.name}»?`, text: 'Карточка исчезнет, камеры этого сотрудника станут свободными.', ok: 'Удалить', danger: true });
    if (c.ok && (await api('/api/staff/' + p.id, { method: 'DELETE' })).ok) { toast('Сотрудник удалён', 'ok'); staff.enter($('#view')); }
  },
};
