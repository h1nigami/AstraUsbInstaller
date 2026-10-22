// Настройки: разделы раскрываются, каждый сохраняется своей кнопкой.
import { $, $$, esc, ico, empty, skeletons } from './util.js';
import { api, post } from './net.js';
import { toast, confirmSheet, busy, vals, field, toggle, seg, stepper, placeThumbs } from './ui.js';
import { store } from './store.js';

let CFG = null;
export const settings = {
  async enter(v) {
    v.innerHTML = skeletons(5);
    const r = await api('/api/settings');
    if (store.section !== 'settings') return;
    if (!r.ok) { v.innerHTML = empty('alert', 'Настройки не открылись', 'Попробуйте обновить страницу.'); return; }
    CFG = r.data;
    const c = CFG, st = c.storage;
    const acc = (id, icon, title, sub, body) => `
      <section class="acc" id="acc-${id}">
        <button data-act="acc" aria-expanded="false">${ico(icon)}<b>${title}<small>${sub}</small></b>${ico('chevron', 'chev')}</button>
        <div class="more"><div><div class="inner" data-form="${id}">${body}</div></div></div>
      </section>`;
    const save = (act, label = 'Сохранить') => `<div class="status" role="status"></div><button class="btn primary block" data-act="${act}">${label}</button>`;

    v.innerHTML = `<div class="settings stack stagger">${[
      acc('storage', 'box', 'Хранилище', esc(st.backupRoot || 'папка архива не выбрана'), `
        ${st.onSystemDrive ? '<div class="callout">Архив лежит на системном диске: он невелик, записи его переполнят. Выберите отдельный диск.</div>' : ''}
        ${field('Папка архива', 'backupRoot', st.backupRoot)}
        <div class="row2">${field('Порог места, ГБ', 'minFreeGb', st.minFreeGb, 'type="number" inputmode="numeric" min="1"')}
          ${field('Хранить, дней', 'keepDays', st.keepDays, 'type="number" inputmode="numeric" min="0" max="3650"')}</div>
        <p class="hint">0 дней: записи хранятся бессрочно.</p>
        <div class="row2">${field('Номер станции', 'stationNumber', st.stationNumber, 'type="number" inputmode="numeric" min="0" max="99"')}
          ${field('Название точки', 'stationPlace', st.stationPlace)}</div>
        <div class="field"><span class="lbl">Когда места мало</span>${seg('storageMode', [[0, 'Предупреждать'], [1, 'Стирать старые']], st.storageMode)}</div>
        ${toggle('Удалять видео с камеры после копирования', 'deleteVideoAfterCopy', st.deleteVideoAfterCopy)}
        ${toggle('Звуковая тревога', 'alarmSound', st.alarmSound, 'При нехватке места и обрыве сети')}
        ${toggle('Голосовые подсказки', 'voiceHints', st.voiceHints, 'Станция называет отсек, когда пора забирать камеру')}
        ${save('saveStorage')}`),
      acc('bays', 'bays', 'Окна сбора', `сейчас ${c.bays.count}, по ${c.bays.perRow} в ряд`, `
        <div class="field"><span class="lbl">Число окон</span>${stepper('bayCount', c.bays.count, 6, 30)}</div>
        <div class="field"><span class="lbl">Окон в ряд на экране станции</span>${stepper('baysPerRow', c.bays.perRow, 2, 6)}</div>
        <div class="callout" id="bayNote" hidden>Сохранено. Новое число окон появится после перезапуска станции.</div>
        ${save('saveBays')}
        <button class="btn soft block" id="bayRestart" data-act="restart" hidden>${ico('refresh')}Перезапустить сейчас</button>`),
      acc('ftp', 'server', 'Отправка на сервер', c.ftp.enabled ? 'включена' : 'выключена', `
        ${toggle('Отправлять записи на сервер', 'enabled', c.ftp.enabled, c.ftp.queue)}
        ${field('Адрес сервера', 'host', c.ftp.host, 'autocapitalize="off" autocomplete="off"')}
        <div class="row2">${field('Порт', 'port', c.ftp.port, 'type="number" inputmode="numeric"')}${field('Папка на сервере', 'folder', c.ftp.folder)}</div>
        <div class="row2">${field('Пользователь', 'user', c.ftp.user, 'autocomplete="off"')}
          ${field('Пароль', 'password', '', `type="password" autocomplete="new-password" placeholder="${c.ftp.hasPassword ? 'не менять' : ''}"`)}</div>
        ${toggle('Защищённое соединение (SSL)', 'ssl', c.ftp.ssl)}
        ${save('saveFtp')}
        <div class="btns"><button class="btn small" data-act="testFtp">Проверить подключение</button>
          <button class="btn small" data-act="retryFtp">Повторить отложенные</button></div>`),
      acc('sql', 'server', 'Внешняя база', c.sql.enabled ? 'включена' : 'выключена', `
        ${c.sql.error ? `<div class="callout">${esc(c.sql.error)}</div>` : ''}
        ${toggle('Вести учёт во внешней базе MySQL', 'enabled', c.sql.enabled)}
        ${field('Адрес сервера', 'host', c.sql.host, 'autocapitalize="off" autocomplete="off"')}
        <div class="row2">${field('Порт', 'port', c.sql.port, 'type="number" inputmode="numeric"')}${field('База', 'database', c.sql.database)}</div>
        <div class="row2">${field('Пользователь', 'user', c.sql.user, 'autocomplete="off"')}
          ${field('Пароль', 'password', '', `type="password" autocomplete="new-password" placeholder="${c.sql.hasPassword ? 'не менять' : ''}"`)}</div>
        ${save('saveSql')}
        <div class="btns"><button class="btn small" data-act="testSql">Проверить базу</button>
          <button class="btn small" data-act="syncSql">Отправить за месяц</button></div>`),
      acc('access', 'lock', 'Доступ', `учётная запись ${esc(c.account.name)}`, `
        ${c.account.defaultPassword ? '<div class="callout">Пароль остался заводским. Смените его.</div>' : ''}
        ${field('Учётная запись', 'account', c.account.name, 'autocapitalize="off"')}
        ${field('Текущий пароль', 'current', '', 'type="password" autocomplete="current-password"')}
        ${field('Новый пароль', 'next', '', 'type="password" autocomplete="new-password"')}
        ${field('Повтор нового пароля', 'repeat', '', 'type="password" autocomplete="new-password"')}
        ${save('savePassword', 'Сменить пароль')}
        <hr style="border:0;border-top:1px solid var(--line);width:100%">
        <div class="fields" data-form="lock">${field('Блокировать разделы на станции через, мин', 'minutes', c.lockMinutes, 'type="number" inputmode="numeric" min="0" max="240"')}
        <p class="hint">0: разделы станции не закрываются по простою.</p>
        <button class="btn block" data-act="saveLock">Сохранить время</button></div>`),
      acc('about', 'info', 'О станции', esc(c.version || 'версия неизвестна'), `
        <div class="service"><span class="k">Версия</span><span class="v">${esc(c.version || 'неизвестна')}</span></div>
        <div class="service"><span class="k">Веб-панель</span><span class="v">порт ${c.web.port}${c.web.ssl ? ', SSL' : ''}</span></div>
        <p class="hint">Порт и шифрование панели меняются на самой станции: из панели это оборвало бы связь.</p>
        <div class="status" role="status"></div>
        <div class="btns"><button class="btn small" data-act="checkUpdate">${ico('refresh')}Проверить обновления</button>
          <button class="btn small" data-act="diag">${ico('download')}Скачать диагностику</button></div>
        <hr style="border:0;border-top:1px solid var(--line);width:100%">
        <button class="btn block danger" data-act="reset">Заводской сброс</button>
        <p class="hint">Удаляет камеры, историю выгрузок и записи архива. Журнал и сотрудники сохраняются.</p>`),
    ].map((h, i) => h.replace('<section', `<section style="--i:${i}"`)).join('')}</div>`;
    placeThumbs(v);
  },
};

const formOf = el => el.closest('[data-form]');
function say(el, text, ok = true) {
  const s = $('.status', formOf(el));
  if (s) { s.textContent = text; s.className = 'status ' + (ok ? 'ok' : 'bad'); }
  toast(text, ok ? 'ok' : 'bad');
}
async function submit(el, path, label, extra) {
  const r = await busy(el, () => post(path, vals(formOf(el)), true));
  if (r.ok) { say(el, label); extra?.(r); }
  else say(el, r.data?.error || 'Не удалось сохранить', false);
  return r;
}
async function probe(el, path) {
  const r = await busy(el, () => post(path, vals(formOf(el))));
  if (r.ok) say(el, r.data.message, r.data.ok !== false);
}


export const actions = {
  acc: el => {
    const box = el.parentElement, open = !box.classList.contains('open');
    box.classList.toggle('open', open);
    el.setAttribute('aria-expanded', open);
    if (open) setTimeout(() => placeThumbs(box), 20);
  },
  step: el => {
    const box = el.closest('.stepper'), out = $('output', box), input = $('input', box);
    const n = Math.max(+box.dataset.min, Math.min(+box.dataset.max, +input.value + +el.dataset.d));
    input.value = n; out.textContent = n;
    out.classList.remove('bump'); void out.offsetWidth; out.classList.add('bump');
  },
  saveStorage: el => submit(el, '/api/settings/storage', 'Хранилище сохранено'),
  saveBays: el => submit(el, '/api/settings/bays', 'Окна сохранены', () => { $('#bayNote').hidden = false; $('#bayRestart').hidden = false; }),
  saveLock: el => submit(el, '/api/settings/lock', 'Время блокировки сохранено'),
  savePassword: el => submit(el, '/api/settings/password', 'Пароль изменён', () => {
    $$('[type=password]', formOf(el)).forEach(i => { i.value = ''; });
  }),
  saveFtp: el => submit(el, '/api/settings/ftp', 'Параметры отправки сохранены'),
  saveSql: el => submit(el, '/api/settings/sql', 'Параметры базы сохранены'),
  testFtp: el => probe(el, '/api/ftp/test'),
  retryFtp: el => probe(el, '/api/ftp/retry'),
  testSql: el => probe(el, '/api/sql/test'),
  syncSql: el => probe(el, '/api/sql/sync'),
  checkUpdate: el => probe(el, '/api/update/check'),
  reset: async el => {
    const c = await confirmSheet({
      title: 'Заводской сброс', danger: true, ok: 'Стереть всё и перезапустить',
      text: 'Камеры, история выгрузок и записи архива будут удалены без возможности вернуть. Введите пароль администратора.',
      body: field('Пароль', 'password', '', 'type="password" autocomplete="current-password"'),
    });
    if (!c.ok) return;
    const r = await post('/api/factory-reset', { password: c.values.password });
    if (r.ok) toast(r.data.message, 'ok');
  },
};
