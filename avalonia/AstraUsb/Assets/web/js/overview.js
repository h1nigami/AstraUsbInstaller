// Обзор: счётчики отсеков, том архива и состояние служб.
import { $, $$, esc, gb, ico, countTo } from './util.js';
import { store } from './store.js';

export const overview = {
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
    if (store.state) this.update(store.state);
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

export const services = s => [
  { k: 'Монитор USB', v: 'активен', up: true },
  { k: 'Локальная база', v: 'работает', up: true },
  { k: 'Сеть', v: s.networkUp ? 'доступна' : 'недоступна', up: s.networkUp },
  { k: 'Отправка', v: s.ftpEnabled ? s.ftpState : 'выключена', up: s.ftpEnabled },
];
