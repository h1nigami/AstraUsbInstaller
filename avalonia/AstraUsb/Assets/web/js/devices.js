// Камеры: список, имя и закрепление за сотрудником.
import { $, esc, ico, when, initials, empty, skeletons } from './util.js';
import { api, post } from './net.js';
import { toast, openSheet, closeSheet, sheetNow, busy, vals, field } from './ui.js';
import { store } from './store.js';

let DEV = null;
export const devices = {
  async enter(v) {
    v.innerHTML = skeletons(4);
    const r = await api('/api/devices');
    if (store.section !== 'devices') return;
    if (!r.ok) { v.innerHTML = empty('alert', 'Список не открылся', 'Попробуйте обновить страницу.'); return; }
    DEV = r.data;
    v.innerHTML = DEV.devices.length === 0
      ? empty('camera', 'Камеры ещё не подключались', 'Они появятся здесь после первой загрузки.')
      : `<div class="list stagger">${DEV.devices.map((d, i) => `
        <button class="item" style="--i:${i}" data-act="device" data-id="${d.id}">
          <span class="avatar">${esc(initials(d.name || d.number || d.id))}</span>
          <span class="body"><span class="t" style="display:block">${esc(d.name || d.number || 'Камера ' + d.id)}</span>
            <span class="s" style="display:block">${esc([d.employee || 'ни за кем не закреплена', d.department, 'была ' + when(d.lastSeen)].filter(Boolean).join(' · '))}</span></span>
          ${ico('chevron')}</button>`).join('')}</div>`;
  },
};

export const actions = {
  device: el => {
    const d = DEV.devices.find(x => String(x.id) === el.dataset.id);
    openSheet(`
      <h3>${esc(d.name || d.number || 'Камера ' + d.id)}</h3>
      <p class="sub">${esc([d.number && 'номер ' + d.number, 'впервые ' + when(d.firstSeen), 'была ' + when(d.lastSeen)].filter(Boolean).join(' · '))}</p>
      ${field('Имя камеры', 'name', d.name, 'placeholder="Пусто: показывается номер"')}
      <div class="field"><label for="f-employee">Закреплена за</label><select id="f-employee" name="employee">
        <option value="">Ни за кем</option>${DEV.staff.map(e => `<option value="${e.id}" ${e.id === d.employeeId ? 'selected' : ''}>${esc(e.name)}${e.no ? ' · № ' + esc(e.no) : ''}</option>`).join('')}</select></div>
      <button class="btn primary block" data-act="saveDevice" data-id="${d.id}">Сохранить</button>
      <button class="btn soft block" data-act="close">Отмена</button>`);
  },
  saveDevice: async el => {
    const f = vals(sheetNow.sh), id = el.dataset.id;
    const r = await busy(el, async () => {
      const a = await post(`/api/devices/${id}/name`, { name: f.name });
      return a.ok ? post(`/api/devices/${id}/employee`, { employeeId: f.employee ? Number(f.employee) : null }) : a;
    });
    if (r.ok) { closeSheet(); toast('Камера сохранена', 'ok'); devices.enter($('#view')); }
  },
};
