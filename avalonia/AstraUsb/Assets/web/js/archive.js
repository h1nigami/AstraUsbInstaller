// Архив: отбор записей, просмотр, скачивание, защита, заметки, удаление.
import { $, $$, esc, ico, ICONS, size, when, ymd, daysAgo, empty, sleep } from './util.js';
import { api, post } from './net.js';
import { toast, openSheet, closeSheet, closeViewer, sheetNow, confirmSheet, busy, link, vals, field, toggle } from './ui.js';
import { store } from './store.js';

const A = {
  name: '', kind: '', period: 'week', from: '', to: '', employee: '', no: '', camera: '', department: '',
  protectedOnly: false, rows: [], sel: new Set(), converter: false, ticket: null, until: 0, gen: 0,
};
const KINDS = [['', 'Все'], ['Video', 'Видео'], ['Photo', 'Фото'], ['Audio', 'Звук'], ['Log', 'Журналы']];
const PERIODS = [['today', 'Сегодня'], ['week', '7 дней'], ['month', '30 дней']];

function range() {
  const today = ymd(new Date());
  if (A.period === 'today') return [today, today];
  if (A.period === 'month') return [ymd(daysAgo(30)), today];
  if (A.period === 'custom') return [A.from || ymd(daysAgo(7)), A.to || today];
  return [ymd(daysAgo(7)), today];
}

export const archive = {
  async enter(v) {
    A.sel.clear();
    v.innerHTML = `
      <div class="find">
        <div class="searchrow">
          <input class="search" id="q" type="search" placeholder="Имя файла" value="${esc(A.name)}" aria-label="Имя файла">
          <button class="icon-btn" id="filterBtn" data-act="filters" aria-label="Условия отбора">${ico('filter')}</button>
        </div>
        <div class="chips" id="kinds"></div>
        <div class="chips" id="periods"></div>
      </div>
      <div class="count" id="count"></div>
      <div class="recs" id="recs"></div>`;
    chips();
    let t = 0;
    $('#q').addEventListener('input', e => {
      A.name = e.target.value;
      clearTimeout(t);
      t = setTimeout(findRecords, 400); // не на каждую букву: станция и так занята сбором
    });
    findRecords(true);
  },
};

function chips() {
  const row = (id, list, cur, act) => { $(id).innerHTML = list.map(([k, t]) =>
    `<button class="chip ${k === cur ? 'on' : ''}" data-act="${act}" data-v="${k}">${t}</button>`).join(''); };
  row('#kinds', KINDS, A.kind, 'kind');
  row('#periods', PERIODS, A.period, 'period');
  $('#filterBtn')?.classList.toggle('on', !!(A.employee || A.no || A.camera || A.department || A.protectedOnly || A.period === 'custom'));
}

async function findRecords(animate = false) {
  const gen = ++A.gen;
  if (animate) $('#recs').innerHTML = '<div class="skeleton"></div>'.repeat(3);
  const [from, to] = range();
  const q = new URLSearchParams({ from, to, name: A.name, kind: A.kind, employee: A.employee, no: A.no,
    camera: A.camera, department: A.department, protected: A.protectedOnly ? '1' : '0' });
  const r = await api('/api/search?' + q);
  if (gen !== A.gen || store.section !== 'archive') return;
  if (!r.ok) { $('#recs').innerHTML = empty('alert', 'Архив не открылся', 'Проверьте, что диск архива подключён.'); return; }

  A.rows = r.data.rows;
  A.converter = r.data.converter;
  const have = new Set(A.rows.map(x => x.path));
  A.sel = new Set([...A.sel].filter(p => have.has(p)));
  drawRecords(animate || !$('#recs .rec'));
  selbar();
  $('#count').textContent = A.rows.length === 0 ? '' : A.rows.length >= r.data.limit
    ? `Показаны первые ${r.data.limit}: сузьте условия, чтобы увидеть остальное`
    : `Найдено: ${A.rows.length}`;
}

function recHtml(r, i) {
  const meta = [r.employee, r.camera && 'камера ' + r.camera, when(r.collected), size(r.size)].filter(Boolean).join(' · ');
  return `
    <article class="rec ${A.sel.has(r.path) ? 'sel' : ''}" data-p="${esc(r.path)}" style="--i:${i}">
      <button class="chk" data-act="pick" aria-label="Выбрать запись"><i><svg viewBox="0 0 24 24"><path d="${ICONS.check}"/></svg></i></button>
      <div class="rec-main" data-act="rec">
        <span class="fname">${esc(r.file)}</span>
        <div class="rec-meta">${esc(meta)}</div>
        <div class="tags"><span class="tag">${esc(r.kind)}</span>
          ${r.important ? '<span class="tag shield">защищено</span>' : ''}
          ${r.note ? '<span class="tag note">заметка</span>' : ''}
          ${r.shot === 'off' ? '<span class="tag off">часы сбиты</span>' : ''}</div>
      </div>
      <div class="rec-acts">
        <button class="btn soft" data-act="watch">${ico('play')}Смотреть</button>
        <button class="btn primary" data-act="save">${ico('download')}Скачать</button>
      </div>
    </article>`;
}

function drawRecords(animate) {
  const box = $('#recs');
  if (!box) return;
  box.classList.toggle('stagger', animate);
  box.innerHTML = A.rows.length ? A.rows.map(recHtml).join('')
    : empty('archive', 'Записей нет', 'По этим условиям ничего не нашлось. Расширьте период или сбросьте условия.');
}

const rowOf = el => A.rows.find(r => r.path === el.closest('.rec')?.dataset.p);

async function ticket() {
  if (A.ticket && Date.now() < A.until) return A.ticket;
  const r = await post('/api/ticket');
  if (!r.ok) return null;
  A.ticket = r.data.ticket;
  A.until = Date.now() + 25 * 60e3;
  return A.ticket;
}
const fileUrl = (p, t) => `/api/file?p=${encodeURIComponent(p)}&t=${t}`;


async function watch(row) {
  const t = await ticket();
  if (!t) return;
  const url = fileUrl(row.path, t);
  const tag = { 'Видео': 'video', 'Фото': 'img', 'Аудио': 'audio' }[row.kind];
  if (!tag) { window.open(url, '_blank'); return; }
  const v = document.createElement('div');
  v.className = 'viewer';
  v.innerHTML = `<${tag} class="media" src="${url}" ${tag === 'img' ? `alt="${esc(row.file)}"` : 'controls autoplay playsinline'}></${tag}>
    <button class="x" aria-label="Закрыть" data-act="closeViewer">${ico('x')}</button>`;
  v.addEventListener('click', e => { if (e.target === v) closeViewer(); });
  $('#layer').append(v);
  requestAnimationFrame(() => requestAnimationFrame(() => v.classList.add('in')));
}

async function packAndLoad(paths) {
  const t = await ticket();
  if (!t) return;
  if (paths.length === 1) { const r = A.rows.find(x => x.path === paths[0]); link(fileUrl(paths[0], t), r?.file); return; }
  const r = await post('/api/archive/pack', { paths });
  if (r.ok) link(`/api/zip?id=${r.data.id}&t=${t}`);
}

/* Панель действий над выбранным */
function selbar() {
  const bar = $('#selbar'), n = A.sel.size;
  if (store.section !== 'archive' || n === 0) { bar.classList.remove('on'); return; }
  bar.innerHTML = `<span class="n">Выбрано: ${n}</span>
    <button class="icon-btn" data-act="pickAll" title="Выбрать все" aria-label="Выбрать все">${ico('select')}</button>
    <button class="icon-btn" data-act="bulkSave" title="Скачать" aria-label="Скачать">${ico('download')}</button>
    <button class="icon-btn" data-act="bulkProtect" title="Защитить" aria-label="Защитить">${ico('shield')}</button>
    <button class="icon-btn warn" data-act="bulkDelete" title="Удалить" aria-label="Удалить">${ico('trash')}</button>
    <button class="icon-btn" data-act="pickNone" title="Снять выбор" aria-label="Снять выбор">${ico('x')}</button>`;
  bar.classList.add('on');
}

async function protect(paths, on) {
  const r = await post('/api/archive/protect', { paths, on });
  if (!r.ok) return;
  A.rows.forEach(x => { if (paths.includes(x.path)) x.important = on; });
  drawRecords(false);
  toast(on ? `Защищено записей: ${r.data.count}` : `Защита снята с записей: ${r.data.count}`, 'ok');
}

async function remove(paths) {
  const res = await confirmSheet({
    title: paths.length === 1 ? 'Удалить запись?' : `Удалить записи: ${paths.length}?`,
    text: 'Файлы удалятся с диска архива без возможности вернуть. Защищённые записи пропускаются.',
    ok: 'Удалить', danger: true,
  });
  if (!res.ok) return;
  const r = await post('/api/archive/delete', { paths });
  if (!r.ok) return;
  paths.forEach(p => $$('.rec').find(e => e.dataset.p === p && !A.rows.find(x => x.path === p)?.important)?.classList.add('leaving'));
  const parts = [`удалено: ${r.data.deleted}`];
  if (r.data.skipped) parts.push(`пропущено защищённых: ${r.data.skipped}`);
  if (r.data.failed) parts.push(`не удалось: ${r.data.failed}`);
  toast(parts.join(', '), r.data.failed ? 'bad' : 'ok');
  A.sel.clear(); selbar();
  await sleep(380);
  findRecords();
}

function recSheet(row) {
  const sh = openSheet(`
    <h3 style="word-break:break-all">${esc(row.file)}</h3>
    <p class="sub">${esc([row.kind, row.employee, row.department, row.camera && 'камера ' + row.camera, size(row.size)].filter(Boolean).join(' · '))}</p>
    <p class="sub" style="margin-top:-4px">Загружено ${esc(when(row.collected))}${row.shotAt ? `, снято ${esc(when(row.shotAt))}${row.shot === 'off' ? ' (часы камеры сбиты)' : ''}` : ''}</p>
    <div class="btns"><button class="btn soft" style="flex:1" data-act="sheetWatch">${ico('play')}Смотреть</button>
      <button class="btn primary" style="flex:1" data-act="sheetSave">${ico('download')}Скачать</button></div>
    <button class="btn block" data-act="sheetProtect">${ico('shield')}${row.important ? 'Снять защиту' : 'Защитить от удаления'}</button>
    <div class="field"><label for="f-note">Заметка</label><textarea id="f-note" name="note" placeholder="По какому случаю нужна запись">${esc(row.note)}</textarea></div>
    <button class="btn block" data-act="sheetNote">Сохранить заметку</button>
    ${A.converter && row.formats.length ? `<div class="field"><span class="lbl">Перевести в другой формат (оригинал останется)</span>
      <div class="btns">${row.formats.map(f => `<button class="btn small" data-act="sheetConvert" data-f="${f}">${ico('convert')}${f}</button>`).join('')}</div></div>` : ''}
    <button class="btn block danger" data-act="sheetDelete">${ico('trash')}Удалить запись</button>`);
  sh.dataset.p = row.path;
}

/* Условия отбора */
async function filtersSheet() {
  const [from, to] = range();
  const deps = await api('/api/departments', { quiet: true });
  openSheet(`
    <h3>Условия отбора</h3>
    <div class="row2">${field('С', 'from', from, 'type="date"')}${field('По', 'to', to, 'type="date"')}</div>
    ${field('Сотрудник', 'employee', A.employee, 'placeholder="Фамилия или имя"')}
    ${field('Табельный номер', 'no', A.no, 'inputmode="numeric"')}
    ${field('Камера', 'camera', A.camera, 'placeholder="Номер или имя камеры"')}
    <div class="field"><label for="f-department">Отдел</label><select id="f-department" name="department">
      <option value="">Все отделы</option>${(deps.data || []).map(d => `<option value="${d.id}" ${String(d.id) === A.department ? 'selected' : ''}>${esc(d.name)}</option>`).join('')}</select></div>
    ${toggle('Только защищённые', 'protectedOnly', A.protectedOnly)}
    <button class="btn primary block" data-act="applyFilters">Показать</button>
    <button class="btn soft block" data-act="resetFilters">Сбросить условия</button>`);
}


/* Уход из раздела снимает выбор: панель действий принадлежит только архиву. */
export function clearSelection() { A.sel.clear(); selbar(); }

export const actions = {
  kind: el => { A.kind = el.dataset.v; chips(); findRecords(); },
  period: el => { A.period = el.dataset.v; chips(); findRecords(); },
  filters: filtersSheet,
  applyFilters: () => {
    const f = vals(sheetNow.sh), [from, to] = range();
    if (f.from !== from || f.to !== to) { A.period = 'custom'; A.from = f.from; A.to = f.to; }
    Object.assign(A, { employee: f.employee.trim(), no: f.no.trim(), camera: f.camera.trim(), department: f.department, protectedOnly: f.protectedOnly });
    closeSheet(); chips(); findRecords(true);
  },
  resetFilters: () => {
    Object.assign(A, { period: 'week', from: '', to: '', employee: '', no: '', camera: '', department: '', protectedOnly: false, kind: '', name: '' });
    $('#q').value = ''; closeSheet(); chips(); findRecords(true);
  },
  pick: el => {
    const r = rowOf(el), card = el.closest('.rec');
    A.sel.has(r.path) ? A.sel.delete(r.path) : A.sel.add(r.path);
    card.classList.toggle('sel', A.sel.has(r.path));
    selbar();
  },
  rec: el => { const r = rowOf(el); if (!r) return; A.sel.size ? actions.pick(el.closest('.rec').querySelector('.chk')) : recSheet(r); },
  pickAll: () => { A.rows.forEach(r => A.sel.add(r.path)); $$('.rec').forEach(e => e.classList.add('sel')); selbar(); },
  pickNone: () => { A.sel.clear(); $$('.rec').forEach(e => e.classList.remove('sel')); selbar(); },
  watch: el => watch(rowOf(el)),
  save: el => packAndLoad([rowOf(el).path]),
  bulkSave: () => packAndLoad([...A.sel]),
  bulkProtect: () => protect([...A.sel], A.rows.filter(r => A.sel.has(r.path)).some(r => !r.important)),
  bulkDelete: () => remove([...A.sel]),
  sheetWatch: () => watch(A.rows.find(r => r.path === sheetNow.sh.dataset.p)),
  sheetSave: () => packAndLoad([sheetNow.sh.dataset.p]),
  sheetProtect: async () => {
    const p = sheetNow.sh.dataset.p, r = A.rows.find(x => x.path === p);
    closeSheet(); await protect([p], !r.important);
  },
  sheetNote: async el => {
    const p = sheetNow.sh.dataset.p, note = $('[name=note]', sheetNow.sh).value;
    const r = await busy(el, () => post('/api/archive/note', { paths: [p], note }));
    if (r.ok) { A.rows.find(x => x.path === p).note = note.trim(); drawRecords(false); toast('Заметка сохранена', 'ok'); closeSheet(); }
  },
  sheetConvert: async el => {
    const p = sheetNow.sh.dataset.p;
    const r = await busy(el, () => post('/api/archive/convert', { paths: [p], format: el.dataset.f }));
    if (r.ok) { toast(r.data.ok ? 'Копия готова: ' + r.data.message : r.data.message, r.data.ok ? 'ok' : 'bad'); if (r.data.ok) { closeSheet(); findRecords(); } }
  },
  sheetDelete: () => { const p = sheetNow.sh.dataset.p; closeSheet(true); remove([p]); },
};
