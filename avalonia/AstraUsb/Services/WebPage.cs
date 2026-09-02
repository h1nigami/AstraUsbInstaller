namespace AstraUsb.Services;

/// <summary>
/// Страница веб-панели.
///
/// Одна страница без сборщика и без внешних библиотек: на станцию ставится
/// самодостаточный каталог, и тянуть туда узел сборки ради нескольких экранов
/// незачем. Раскладка одна на телефон и на компьютер, ширина решает всё
/// остальное, как и требует задание.
///
/// Палитра та же, что у киоска, чтобы панель узнавалась.
/// </summary>
public static class WebPage
{
    public const string Html = """
        <!doctype html>
        <html lang="ru">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>BestCam BC-10</title>
        <style>
          :root {
            --bg: #eef3f9; --surface: #dde7f2; --panel: #fff; --text: #16202c;
            --muted: #56677e; --accent: #2f77ad; --teal: #3f9ba6; --line: #16202c29;
          }
          * { box-sizing: border-box; }
          body {
            margin: 0; background: var(--bg); color: var(--text);
            font: 15px/1.5 system-ui, sans-serif;
          }
          header {
            background: var(--panel); padding: 14px 16px; display: flex;
            align-items: center; gap: 12px; border-bottom: 1px solid var(--line);
          }
          header b { font-size: 17px; }
          header .clock { margin-left: auto; font-variant-numeric: tabular-nums; }
          main { padding: 16px; max-width: 1100px; margin: 0 auto; }
          .cards { display: grid; gap: 12px; grid-template-columns: repeat(auto-fit, minmax(210px, 1fr)); }
          .card { background: var(--panel); border-radius: 16px; padding: 14px; }
          .card h3 { margin: 0 0 8px; font-size: 13px; letter-spacing: .04em;
            text-transform: uppercase; color: var(--muted); }
          .big { font-size: 26px; font-weight: 700; }
          .bay { background: var(--panel); border-radius: 16px; padding: 12px;
            border: 2px solid var(--line); }
          .bay .n { display: inline-flex; width: 24px; height: 24px; border-radius: 12px;
            background: var(--surface); align-items: center; justify-content: center;
            font-size: 12px; font-weight: 700; }
          .bay .state { font-size: 17px; font-weight: 700; margin: 6px 0 2px; }
          .bay small { color: var(--muted); }
          .track { height: 7px; border-radius: 4px; background: var(--surface); margin-top: 8px; }
          .fill { height: 7px; border-radius: 4px; background: var(--accent); }
          .copying { background: #eaf3fc; border-color: #72a9de; }
          .done { background: #c8eaee; border-color: #3f9ba6; }
          .failed { background: #143a61; border-color: #143a61; color: #f7fafd; }
          .failed small { color: #a7caee; }
          table { width: 100%; border-collapse: collapse; background: var(--panel);
            border-radius: 16px; overflow: hidden; }
          th, td { text-align: left; padding: 8px 10px; font-size: 13px;
            border-bottom: 1px solid var(--line); }
          th { font-size: 11px; text-transform: uppercase; color: var(--muted); }
          nav { display: flex; gap: 8px; padding: 12px 16px 0; max-width: 1100px; margin: 0 auto; }
          nav button { flex: 1; min-height: 44px; border: 0; border-radius: 22px;
            background: var(--panel); color: var(--text); font-size: 13px; font-weight: 600; }
          nav button.on { background: var(--accent); color: #fff; }
          form { background: var(--panel); border-radius: 16px; padding: 16px;
            max-width: 360px; margin: 40px auto; display: grid; gap: 10px; }
          input { min-height: 48px; border-radius: 24px; border: 1px solid var(--line);
            padding: 0 14px; font-size: 15px; }
          button.go { min-height: 48px; border: 0; border-radius: 24px;
            background: var(--accent); color: #fff; font-size: 15px; font-weight: 600; }
          .err { color: #8a5a00; font-size: 13px; }
          .trouble { background: #143a61; color: #f7fafd; border-radius: 16px;
            padding: 12px 14px; margin-bottom: 12px; }
        </style>
        </head>
        <body>
        <div id="login">
          <form onsubmit="signIn(event)">
            <b>BestCam BC-10</b>
            <div class="err">Панель показывает состояние станции и её архив.</div>
            <input id="account" value="admin" autocomplete="username">
            <input id="password" type="password" placeholder="пароль" autocomplete="current-password">
            <button class="go" type="submit">Войти</button>
            <div class="err" id="loginError"></div>
          </form>
        </div>

        <div id="panel" hidden>
          <header>
            <b>Терминал BC-10</b>
            <span class="clock" id="clock"></span>
          </header>
          <nav>
            <button class="on" onclick="show('overview', this)">Обзор</button>
            <button onclick="show('bays', this)">Отсеки</button>
            <button onclick="show('archive', this)">Архив</button>
            <button onclick="show('log', this)">Журнал</button>
          </nav>
          <main>
            <div id="trouble"></div>
            <section id="overview"></section>
            <section id="bays" hidden></section>
            <section id="archive" hidden></section>
            <section id="log" hidden></section>
          </main>
        </div>

        <script>
        let token = sessionStorage.getItem('token') || '';
        let view = 'overview';

        async function signIn(e) {
          e.preventDefault();
          const body = new FormData();
          body.append('account', document.getElementById('account').value);
          body.append('password', document.getElementById('password').value);
          const res = await fetch('/api/login', { method: 'POST', body });
          if (res.status === 429) {
            document.getElementById('loginError').textContent = 'слишком много попыток, подождите минуту';
            return;
          }
          if (!res.ok) {
            document.getElementById('loginError').textContent = 'учётная запись или пароль не подошли';
            return;
          }
          token = (await res.json()).token;
          sessionStorage.setItem('token', token);
          start();
        }

        function start() {
          document.getElementById('login').hidden = true;
          document.getElementById('panel').hidden = false;
          tick();
          setInterval(tick, 3000);
        }

        function show(name, button) {
          view = name;
          for (const id of ['overview', 'bays', 'archive', 'log'])
            document.getElementById(id).hidden = id !== name;
          for (const b of document.querySelectorAll('nav button')) b.classList.remove('on');
          button.classList.add('on');
          tick();
        }

        async function get(path) {
          const res = await fetch(path, { headers: { 'X-Token': token } });
          if (res.status === 401) {
            sessionStorage.removeItem('token');
            location.reload();
            return null;
          }
          return res.ok ? res.json() : null;
        }

        function gb(bytes) { return (bytes / 1024 / 1024 / 1024).toFixed(1) + ' ГБ'; }

        async function tick() {
          const s = await get('/api/state');
          if (!s) return;

          document.getElementById('clock').textContent = new Date(s.at).toLocaleTimeString('ru-RU');
          document.getElementById('trouble').innerHTML = s.trouble
            ? '<div class="trouble">' + s.trouble + '</div>' : '';

          if (view === 'overview') {
            document.getElementById('overview').innerHTML = `
              <div class="cards">
                <div class="card"><h3>Копирование</h3><div class="big">${s.copying}</div></div>
                <div class="card"><h3>Готово</h3><div class="big">${s.done}</div></div>
                <div class="card"><h3>Ошибки</h3><div class="big">${s.failed}</div></div>
                <div class="card"><h3>Свободно окон</h3><div class="big">${s.free}</div></div>
                <div class="card"><h3>Том архива</h3>
                  <div>${s.archiveLabel || 'архив'}</div>
                  <small>свободно ${gb(s.archiveFreeBytes)} из ${gb(s.archiveTotalBytes)}</small>
                </div>
                <div class="card"><h3>Службы</h3>
                  <div>${s.networkUp ? 'сеть доступна' : 'сети нет'}</div>
                  <small>${s.ftpEnabled ? s.ftpState : 'отправка выключена'}</small>
                </div>
              </div>`;
          }

          if (view === 'bays') {
            document.getElementById('bays').innerHTML = '<div class="cards">' + s.bays.map(b => `
              <div class="bay ${b.state === 'Копирование' ? 'copying' : b.state === 'Готово' ? 'done' : b.state === 'Ошибка' ? 'failed' : ''}">
                <span class="n">${b.slot + 1}</span>
                <span>${b.employee || b.camera || 'Отсек свободен'}</span>
                <div class="state">${b.state}</div>
                <small>${b.files || ''}</small>
                <div class="track"><div class="fill" style="width:${b.percent}%"></div></div>
              </div>`).join('') + '</div>';
          }

          if (view === 'archive') {
            const rows = await get('/api/archive');
            document.getElementById('archive').innerHTML = table(
              ['Файл', 'Тип', 'Устройство', 'Сотрудник', 'Размер'],
              (rows || []).map(r => [r.file, r.kind, r.camera, r.employee || '', gb(r.size)]));
          }

          if (view === 'log') {
            const rows = await get('/api/log');
            document.getElementById('log').innerHTML = table(
              ['Время', 'Событие', 'Описание'],
              (rows || []).map(r => [new Date(r.at).toLocaleString('ru-RU'), r.kind, r.text]));
          }
        }

        function table(head, rows) {
          return '<table><thead><tr>' + head.map(h => '<th>' + h + '</th>').join('')
            + '</tr></thead><tbody>'
            + rows.map(r => '<tr>' + r.map(c => '<td>' + (c ?? '') + '</td>').join('') + '</tr>').join('')
            + '</tbody></table>';
        }

        if (token) start();
        </script>
        </body>
        </html>
        """;
}
