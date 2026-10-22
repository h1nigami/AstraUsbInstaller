'use strict';

/* ======================================================================
   Веб-панель станции BestCam BC-10.
   Без сборщика и библиотек. Разделы строят страницу один раз и дальше
   меняют отдельные элементы на месте: так кольца, полосы и счётчики
   плавно доезжают до новых значений, а набранный в поле текст не пропадает.
   ====================================================================== */

const $ = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => [...r.querySelectorAll(s)];
const esc = t => String(t ?? '').replace(/[&<>"']/g,
  c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
const reduced = matchMedia('(prefers-reduced-motion: reduce)').matches;
const sleep = ms => new Promise(r => setTimeout(r, ms));

/* ---------- значки ---------- */
const ICONS = {
  home: 'M3 11l9-8 9 8M5 10v10h5v-6h4v6h5V10',
  bays: 'M4 4h7v7H4zM13 4h7v7h-7zM4 13h7v7H4zM13 13h7v7h-7z',
  archive: 'M3 5h18v4H3zM5 9v10h14V9M10 13h4',
  camera: 'M4 8h3l2-3h6l2 3h3v11H4zM12 17a3.5 3.5 0 100-7 3.5 3.5 0 000 7z',
  people: 'M16 20v-1a4 4 0 00-4-4H7a4 4 0 00-4 4v1M9.5 11a3.5 3.5 0 100-7 3.5 3.5 0 000 7zM21 20v-1a4 4 0 00-3-3.9M16 4.1a3.5 3.5 0 010 6.8',
  log: 'M5 4h14v16H5zM9 9h6M9 13h6M9 17h3',
  settings: 'M4 7h10M18 7h2M4 17h2M10 17h10M16 4v6M8 14v6',
  more: 'M5 12h.01M12 12h.01M19 12h.01',
  filter: 'M4 6h16M7 12h10M10 18h4',
  play: 'M8 5v14l11-7z',
  download: 'M12 4v11M7 11l5 5 5-5M5 20h14',
  shield: 'M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z',
  trash: 'M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13',
  check: 'M5 12l5 5 9-10',
  x: 'M6 6l12 12M18 6L6 18',
  plus: 'M12 5v14M5 12h14',
  minus: 'M5 12h14',
  convert: 'M4 8h13l-3-3M20 16H7l3 3',
  logout: 'M10 4H5v16h5M15 8l4 4-4 4M19 12H9',
  bolt: 'M13 3L5 14h6l-1 7 8-11h-6z',
  refresh: 'M20 11a8 8 0 10-2.3 5.7M20 4v7h-7',
  chevron: 'M9 6l6 6-6 6',
  alert: 'M12 4l9 16H3zM12 10v4M12 17h.01',
  server: 'M4 5h16v6H4zM4 13h16v6H4zM8 8h.01M8 16h.01',
  lock: 'M6 11h12v9H6zM8 11V8a4 4 0 118 0v3',
  box: 'M4 8l8-4 8 4v8l-8 4-8-4zM4 8l8 4 8-4M12 12v8',
  info: 'M12 21a9 9 0 100-18 9 9 0 000 18zM12 11v5M12 8h.01',
  select: 'M4 5h16M4 12h16M4 19h16',
};
const ico = (n, cls = '') =>
  `<svg class="ico ${cls}" viewBox="0 0 24 24" aria-hidden="true"${n === 'more' ? ' style="stroke-width:3.6"' : ''}><path d="${ICONS[n]}"/></svg>`;

/* ---------- форматы ---------- */
const gb = b => (b / 1024 / 1024 / 1024).toFixed(1) + ' ГБ';
const size = b => b >= 1024 ** 3 ? gb(b) : b >= 1024 ** 2 ? (b / 1024 ** 2).toFixed(1) + ' МБ' : Math.max(1, Math.round(b / 1024)) + ' КБ';
const when = t => new Date(t).toLocaleString('ru-RU', { day: '2-digit', month: '2-digit', year: '2-digit', hour: '2-digit', minute: '2-digit' });
const ymd = d => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
const daysAgo = n => { const d = new Date(); d.setDate(d.getDate() - n); return d; };
const initials = s => (String(s || '?').trim().split(/\s+/).slice(0, 2).map(w => w[0]).join('') || '?').toUpperCase();

/* ======================================================================
   Связь со станцией
   ====================================================================== */
let token = sessionStorage.getItem('token') || '';

async function api(path, o = {}) {
  let res;
  try {
    res = await fetch(path, {
      method: o.method || 'GET',
      headers: { 'X-Token': token, ...(o.body ? { 'Content-Type': 'application/json' } : {}) },
      body: o.body ? JSON.stringify(o.body) : undefined,
    });
  } catch { return { ok: false, status: 0, data: null }; }

  if (res.status === 401 && token) { logout(); return { ok: false, status: 401, data: null }; }
  let data = null;
  try { data = await res.json(); } catch { /* ответ без тела */ }
  if (!res.ok && !o.quiet) toast(data?.error || 'Не удалось выполнить действие', 'bad');
  return { ok: res.ok, status: res.status, data };
}
const post = (path, body, quiet) => api(path, { method: 'POST', body: body ?? {}, quiet });

function logout() {
  sessionStorage.removeItem('token');
  location.hash = '';
  location.reload();
}

/* ======================================================================
   Уведомления, листы, просмотр
   ====================================================================== */
function toast(text, kind = '') {
  const el = document.createElement('div');
  el.className = 'toast ' + kind;
  el.style.setProperty('--life', '4s');
  el.textContent = text;
  $('#toasts').append(el);
  setTimeout(() => { el.classList.add('out'); setTimeout(() => el.remove(), 320); }, 4000);
}

let sheetNow = null;

function openSheet(html, mount) {
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

function closeSheet(instant = false) {
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
function dragToClose(sh) {
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
function confirmSheet({ title, text, ok = 'Подтвердить', danger = false, body = '' }) {
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

function closeViewer() {
  const v = $('.viewer');
  if (!v) return;
  v.classList.remove('in');
  setTimeout(() => v.remove(), 240);
}

document.addEventListener('keydown', e => {
  if (e.key !== 'Escape') return;
  if ($('.viewer')) closeViewer(); else closeSheet();
});

/* ======================================================================
   Формы
   ====================================================================== */
function vals(root) {
  const o = {};
  $$('[name]', root).forEach(e => {
    if (e.type === 'radio') { if (e.checked) o[e.name] = /^\d+$/.test(e.value) ? Number(e.value) : e.value; }
    else if (e.type === 'checkbox') o[e.name] = e.checked;
    else if (e.type === 'number' || e.dataset.num) o[e.name] = Number(e.value);
    else o[e.name] = e.value;
  });
  return o;
}

const field = (label, name, value = '', attrs = '') =>
  `<div class="field"><label for="f-${name}">${esc(label)}</label><input id="f-${name}" name="${name}" value="${esc(value)}" ${attrs}></div>`;

const toggle = (label, name, on, hint = '') =>
  `<label class="switch"><span>${esc(label)}${hint ? `<small>${esc(hint)}</small>` : ''}</span>
     <input type="checkbox" name="${name}" ${on ? 'checked' : ''}><i></i></label>`;

const seg = (name, options, value) =>
  `<div class="seg" role="radiogroup"><span class="thumb"></span>${options.map(([v, t]) =>
    `<label><input type="radio" name="${name}" value="${v}" ${String(v) === String(value) ? 'checked' : ''}>${esc(t)}</label>`).join('')}</div>`;

const stepper = (name, value, min, max) =>
  `<div class="stepper" data-min="${min}" data-max="${max}">
     <button type="button" class="icon-btn" data-act="step" data-d="-1" aria-label="Меньше">${ico('minus')}</button>
     <output>${value}</output><input type="hidden" name="${name}" value="${value}" data-num="1">
     <button type="button" class="icon-btn" data-act="step" data-d="1" aria-label="Больше">${ico('plus')}</button>
   </div>`;

/* Ползунок под выбранным вариантом переезжает с пружиной. */
function placeThumbs(root = document) {
  $$('.seg', root).forEach(s => {
    const on = $('label:has(input:checked)', s), thumb = $('.thumb', s);
    if (!on || !thumb) return;
    thumb.style.width = on.offsetWidth + 'px';
    thumb.style.transform = `translateX(${on.offsetLeft - 4}px)`;
  });
}
document.addEventListener('change', e => { if (e.target.type === 'radio') placeThumbs(e.target.closest('.seg')?.parentElement || document); });

/* ======================================================================
   Числа, которые доезжают до значения
   ====================================================================== */
const shown = new Map();
function countTo(el, n) {
  const key = el.dataset.k, from = shown.get(key) ?? 0;
  shown.set(key, n);
  if (reduced || from === n) { el.textContent = n; return; }
  const t0 = performance.now();
  (function step(t) {
    const p = Math.min(1, (t - t0) / 700);
    el.textContent = Math.round(from + (n - from) * (1 - (1 - p) ** 3));
    if (p < 1) requestAnimationFrame(step);
  })(t0);
}

/* ======================================================================
   Разделы
   ====================================================================== */
const SECTIONS = [
  { id: 'overview', label: 'Обзор', icon: 'home' },
  { id: 'bays', label: 'Отсеки', icon: 'bays' },
  { id: 'archive', label: 'Архив', icon: 'archive' },
  { id: 'devices', label: 'Камеры', icon: 'camera' },
  { id: 'staff', label: 'Сотрудники', icon: 'people' },
  { id: 'log', label: 'Журнал', icon: 'log' },
  { id: 'settings', label: 'Настройки', icon: 'settings' },
];
const MAIN = ['overview', 'bays', 'archive'];
const order = SECTIONS.map(s => s.id);
let section = '', S = null, entering = 0;

const skeletons = n => `<div class="list">${'<div class="skeleton"></div>'.repeat(n)}</div>`;
const empty = (icon, title, text) =>
  `<div class="empty">${ico(icon)}<b>${esc(title)}</b><span>${esc(text)}</span></div>`;

/* ---------- обзор ---------- */
const overview = {
  async enter(v) {
    v.innerHTML = `
      <div class="stack">
        <div class="metrics stagger">
          <div class="metric work" style="--i:0"><b data-k="copying">0</b><span>копируется</span></div>
          <div class="metric done" style="--i:1"><b data-k="done">0</b><span>готово</span></div>
          <div class="metric" style="--i:2"><b data-k="free">0</b><span>свободно окон</span></div>
          <div class="metric bad" id="mFailed" style="--i:3" hidden><b data-k="failed">0</b><span>ошибки</span></div>
        </div>
        <button class="alert" id="alert" data-act="goto" data-to="bays" hidden>${ico('alert')}<span style="flex:1" id="alertText"></span>${ico('chevron')}</button>
        <div class="two stack stagger">
          <div class="card" style="--i:4">
            <div class="row"><span class="h">Том архива</span><span class="muted" id="volText"></span></div>
            <div class="track"><div class="fill" id="vol" style="width:0"></div></div>
            <div class="muted" id="volLabel"></div>
            <div class="btns">
              <button class="btn primary" data-act="restart">${ico('refresh')}Перезапустить станцию</button>
              <button class="btn soft" data-act="refresh">Обновить</button>
            </div>
          </div>
          <div class="card" style="--i:5">
            <span class="h">Состояние служб</span>
            <div id="services"></div>
            <div class="muted" id="ver"></div>
          </div>
        </div>
      </div>`;
    if (S) this.update(S);
  },
  update(s) {
    $$('[data-k]').forEach(el => countTo(el, s[el.dataset.k]));
    const failed = $('#mFailed');
    if (failed) failed.hidden = !(s.failed > 0);
    const alert = $('#alert');
    if (alert) { alert.hidden = !s.trouble; $('#alertText').textContent = s.trouble || ''; }

    const used = s.archiveTotalBytes - s.archiveFreeBytes;
    const pct = s.archiveTotalBytes > 0 ? Math.min(100, Math.round(used / s.archiveTotalBytes * 100)) : 0;
    const vol = $('#vol');
    if (vol) {
      vol.style.width = pct + '%';
      vol.classList.toggle('low', pct >= 90);
      $('#volText').textContent = `свободно ${gb(s.archiveFreeBytes)} из ${gb(s.archiveTotalBytes)}`;
      $('#volLabel').textContent = s.archiveLabel || 'архив';
      $('#ver').textContent = 'Версия: ' + (s.version || 'неизвестна');
      const html = services(s).map(x => `<div class="service"><span class="dot ${x.up ? 'up' : ''}"></span>
        <span class="k">${esc(x.k)}</span><span class="v">${esc(x.v)}</span></div>`).join('');
      if ($('#services').dataset.h !== html) { $('#services').dataset.h = html; $('#services').innerHTML = html; }
    }
  },
};

const services = s => [
  { k: 'Монитор USB', v: 'активен', up: true },
  { k: 'Локальная база', v: 'работает', up: true },
  { k: 'Сеть', v: s.networkUp ? 'доступна' : 'недоступна', up: s.networkUp },
  { k: 'Отправка', v: s.ftpEnabled ? s.ftpState : 'выключена', up: s.ftpEnabled },
];

/* ---------- отсеки ---------- */
const bayClass = state =>
  ['Определение ID', 'Сканирование', 'Копирование'].includes(state) ? 'work'
    : state === 'Готово' ? 'done' : state === 'Ошибка' ? 'bad' : state === 'Свободный' ? 'idle' : '';

const bays = {
  async enter(v) {
    v.innerHTML = '<div class="bays stagger" id="bays"></div>';
    if (S) this.update(S);
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

/* ---------- архив ---------- */
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

const archive = {
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
  if (gen !== A.gen || section !== 'archive') return;
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

function link(url, name) {
  const a = document.createElement('a');
  a.href = url; if (name) a.download = name;
  document.body.append(a); a.click(); a.remove();
}

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
  if (section !== 'archive' || n === 0) { bar.classList.remove('on'); return; }
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

/* ---------- камеры ---------- */
let DEV = null;
const devices = {
  async enter(v) {
    v.innerHTML = skeletons(4);
    const r = await api('/api/devices');
    if (section !== 'devices') return;
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

/* ---------- сотрудники ---------- */
let STAFF = [];
const staff = {
  async enter(v) {
    v.innerHTML = skeletons(4);
    const r = await api('/api/staff');
    if (section !== 'staff') return;
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

/* ---------- журнал ---------- */
const L = { period: 'today', from: '', to: '', kind: '', rows: [] };
const log = {
  async enter(v) {
    v.innerHTML = `
      <div class="toolbar"><div class="chips grow" id="lp" style="margin:0;padding:0"></div></div>
      <div class="toolbar"><div class="chips grow" id="lk" style="margin:0;padding:0"></div></div>
      <div class="toolbar"><span class="grow muted">Журнал станции и действий оператора</span>
        <button class="btn soft small" data-act="diag">${ico('download')}Диагностика</button></div>
      <div class="list" id="events"></div>`;
    logChips();
    loadLog(true);
  },
};
function logChips() {
  $('#lp').innerHTML = [['today', 'Сегодня'], ['week', '7 дней'], ['custom', 'Другой период']].map(([k, t]) =>
    `<button class="chip ${L.period === k ? 'on' : ''}" data-act="lperiod" data-v="${k}">${t}</button>`).join('');
  const kinds = [...new Set(L.rows.map(r => r.kind))];
  $('#lk').innerHTML = (kinds.length > 1 ? ['', ...kinds] : []).map(k =>
    `<button class="chip ${L.kind === k ? 'on' : ''}" data-act="lkind" data-v="${esc(k)}">${k ? esc(k) : 'Все события'}</button>`).join('');
}
async function loadLog(animate) {
  const box = $('#events');
  if (animate) box.innerHTML = skeletons(5);
  const today = ymd(new Date());
  const [from, to] = L.period === 'week' ? [ymd(daysAgo(7)), today] : L.period === 'custom' ? [L.from || today, L.to || today] : [today, today];
  const r = await api(`/api/events?from=${from}&to=${to}`);
  if (section !== 'log') return;
  L.rows = r.data || [];
  logChips();
  const rows = L.kind ? L.rows.filter(x => x.kind === L.kind) : L.rows;
  box.className = 'list stagger';
  box.innerHTML = rows.length === 0 ? empty('log', 'Событий нет', 'За этот период в журнале пусто.')
    : rows.map((e, i) => `<div class="event" style="--i:${i}"><div class="head"><span class="tag">${esc(e.kind)}</span>
        <time>${esc(new Date(e.at).toLocaleString('ru-RU'))}</time></div><div class="text">${esc(e.text)}</div></div>`).join('')
      + (L.rows.length >= 500 ? '<div class="count">Показаны последние 500 событий</div>' : '');
}

/* ---------- настройки ---------- */
let CFG = null;
const settings = {
  async enter(v) {
    v.innerHTML = skeletons(5);
    const r = await api('/api/settings');
    if (section !== 'settings') return;
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
        ${field('Блокировать разделы на станции через, мин', 'minutes', c.lockMinutes, 'type="number" inputmode="numeric" min="0" max="240"')}
        <p class="hint">0: разделы станции не закрываются по простою.</p>
        <button class="btn block" data-act="saveLock">Сохранить время</button>`),
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
async function busy(btn, fn) {
  btn?.classList.add('busy');
  try { return await fn(); } finally { btn?.classList.remove('busy'); }
}
async function probe(el, path) {
  const r = await busy(el, () => post(path, vals(formOf(el))));
  if (r.ok) say(el, r.data.message, r.data.ok !== false);
}

/* ======================================================================
   Действия
   ====================================================================== */
const actions = {
  goto: el => { location.hash = el.dataset.to; },
  refresh: () => tick(true),
  closeViewer, close: () => closeSheet(),

  logout: async () => {
    const r = await confirmSheet({ title: 'Выйти из панели?', text: 'Вход забудется, и панель попросит пароль заново.', ok: 'Выйти' });
    if (r.ok) logout();
  },
  restart: async () => {
    const r = await confirmSheet({ title: 'Перезапустить станцию?', text: 'Программа закроется и поднимется заново. Незавершённые загрузки продолжатся после запуска.', ok: 'Перезапустить', danger: true });
    if (r.ok && (await post('/api/restart')).ok) toast('Станция перезапускается. Панель вернётся через минуту', 'ok');
  },
  more: () => {
    const rest = SECTIONS.filter(s => !MAIN.includes(s.id));
    openSheet(`<h3>Разделы</h3><div class="menu">${rest.map(s =>
      `<button class="btn" data-act="menuGo" data-to="${s.id}">${ico(s.icon)}${s.label}</button>`).join('')}
      <button class="btn soft" data-act="menuOut">${ico('logout')}Выйти</button></div>`);
  },
  menuGo: el => { closeSheet(); location.hash = el.dataset.to; },
  menuOut: () => { closeSheet(true); actions.logout(); },

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
    if (r.ok) { closeSheet(); toast('Команда передана станции', 'ok'); setTimeout(tick, 2200); }
  },

  /* архив */
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

  /* камеры и сотрудники */
  device: el => {
    const d = DEV.devices.find(x => String(x.id) === el.dataset.id);
    const sh = openSheet(`
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

  /* журнал */
  lperiod: el => {
    L.period = el.dataset.v;
    if (L.period === 'custom') {
      const today = ymd(new Date());
      openSheet(`<h3>Период журнала</h3><div class="row2">${field('С', 'from', L.from || today, 'type="date"')}${field('По', 'to', L.to || today, 'type="date"')}</div>
        <button class="btn primary block" data-act="applyLog">Показать</button>`);
      return;
    }
    loadLog(true);
  },
  applyLog: () => { const f = vals(sheetNow.sh); L.from = f.from; L.to = f.to; closeSheet(); loadLog(true); },
  lkind: el => { L.kind = el.dataset.v; loadLog(); },
  diag: async el => {
    const res = await busy(el, () => fetch('/api/diagnostics', { headers: { 'X-Token': token } }));
    if (!res.ok) { toast('Не удалось собрать диагностику', 'bad'); return; }
    const url = URL.createObjectURL(await res.blob());
    link(url, 'diagnostics.zip');
    setTimeout(() => URL.revokeObjectURL(url), 5000);
  },

  /* настройки */
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

document.addEventListener('click', e => {
  const el = e.target.closest('[data-act]');
  if (el && actions[el.dataset.act]) actions[el.dataset.act](el, e);
});

/* Волна от места нажатия на кнопке. */
document.addEventListener('pointerdown', e => {
  const b = e.target.closest('.btn');
  if (!b || reduced) return;
  const r = b.getBoundingClientRect(), d = Math.max(r.width, r.height) * 2;
  const w = document.createElement('span');
  w.className = 'ripple';
  w.style.cssText = `width:${d}px;height:${d}px;left:${e.clientX - r.left - d / 2}px;top:${e.clientY - r.top - d / 2}px`;
  b.append(w);
  setTimeout(() => w.remove(), 600);
});

/* ======================================================================
   Каркас: вход, меню, опрос
   ====================================================================== */
const sections = { overview, bays, archive, devices, staff, log, settings };

function drawNav() {
  const item = (s, act) => `<button data-act="${act}" data-to="${s.id}" data-id="${s.id}">${ico(s.icon)}<span>${s.label}</span></button>`;
  $('#sideNav').innerHTML = '<span class="thumb"></span>' + SECTIONS.map(s => item(s, 'goto')).join('');
  $('#tabs').innerHTML = '<span class="thumb"></span>'
    + SECTIONS.filter(s => MAIN.includes(s.id)).map(s => item(s, 'goto')).join('')
    + `<button data-act="more" data-id="more">${ico('more')}<span>Ещё</span></button>`;
}

function markNav() {
  if (!section) return;
  const tab = MAIN.includes(section) ? section : 'more';
  $$('#sideNav button').forEach(b => b.classList.toggle('on', b.dataset.id === section));
  $$('#tabs button').forEach(b => b.classList.toggle('on', b.dataset.id === tab));
  $('#desk').textContent = SECTIONS.find(s => s.id === section)?.label || '';

  const side = $('#sideNav .thumb');
  side.style.transform = `translateY(${order.indexOf(section) * 52}px)`;
  const on = $('#tabs button.on'), thumb = $('#tabs .thumb');
  if (on && on.offsetWidth) {
    thumb.style.width = on.offsetWidth + 'px';
    thumb.style.transform = `translateX(${on.offsetLeft - 10}px)`;
  }
}
addEventListener('resize', () => { markNav(); placeThumbs(); });

async function route() {
  const id = order.includes(location.hash.slice(1)) ? location.hash.slice(1) : 'overview';
  if (id === section) return;
  const dir = order.indexOf(id) > order.indexOf(section) ? 'fwd' : 'back', first = section === '';
  section = id;
  closeSheet(true);
  A.sel.clear(); selbar();
  markNav();
  scrollTo({ top: 0 });
  const v = $('#view');
  v.className = 'view';
  void v.offsetWidth; // перезапуск анимации появления
  v.className = 'view ' + (first ? '' : dir);
  const n = ++entering;
  await sections[id].enter(v);
  if (n !== entering) return;
}
addEventListener('hashchange', route);

function paintHeader(s) {
  $('#station').textContent = s.station || 'BC-10';
  $('#place').textContent = (s.station || '').split('·').slice(1).join('·').trim();
  $('#clock').textContent = new Date(s.at).toLocaleTimeString('ru-RU') + (s.os ? ' · ' + s.os : '');
  $('#sidePath').textContent = s.archiveLabel || '';
  const html = services(s).map(x => `<span class="pill"><span class="dot ${x.up ? 'up' : ''}"></span>${esc(x.k)} · ${esc(x.v)}</span>`).join('');
  if ($('#pills').dataset.h !== html) { $('#pills').dataset.h = html; $('#pills').innerHTML = html; }
}

let misses = 0;
async function tick(manual) {
  const r = await api('/api/state', { quiet: true });
  if (!r.ok) {
    if (r.status !== 401 && ++misses >= 2) $('#offline').classList.add('on');
    if (manual) toast('Станция не отвечает', 'bad');
    return;
  }
  misses = 0;
  $('#offline').classList.remove('on');
  S = r.data;
  paintHeader(S);
  sections[section]?.update?.(S);
  if (manual) toast('Состояние обновлено', 'ok');
}

function start() {
  $('#login').hidden = true;
  $('#panel').hidden = false;
  drawNav();
  route().then(() => tick());
  setInterval(() => { if (!document.hidden) tick(); }, 3000);
  document.addEventListener('visibilitychange', () => { if (!document.hidden) tick(); });
}

$('#loginForm').addEventListener('submit', async e => {
  e.preventDefault();
  const card = e.currentTarget, err = $('#loginError');
  const btn = $('button', card);
  err.textContent = '';
  const res = await busy(btn, () => fetch('/api/login', { method: 'POST', body: new FormData(card) }).catch(() => null));
  if (res?.ok) { token = (await res.json()).token; sessionStorage.setItem('token', token); start(); return; }
  err.textContent = !res ? 'станция не отвечает'
    : res.status === 429 ? 'слишком много попыток, подождите минуту' : 'учётная запись или пароль не подошли';
  card.classList.remove('shake'); void card.offsetWidth; card.classList.add('shake');
});

if (token) start();
