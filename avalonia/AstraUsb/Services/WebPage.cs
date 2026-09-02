namespace AstraUsb.Services;

/// <summary>
/// Страница веб-панели.
///
/// Одна страница без сборщика и без внешних библиотек: на станцию ставится
/// самодостаточный каталог, и тянуть туда узел сборки ради нескольких экранов
/// незачем.
///
/// Раскладка одна на телефон и на компьютер, как в шаблоне задания: до 900
/// точек ширины разделы переключаются полосой снизу, шире того же адреса
/// появляется боковое меню и шапка со состоянием служб. Палитра и скругления
/// взяты из того же шаблона, поэтому панель узнаётся рядом с киоском.
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
            --bg: #eef3f9; --surface: #dde7f2; --panel: #f7fafd; --text: #16202c;
            --line: #16202c29; --accent: #2f77ad;
            --n200: #e9f0f7; --n300: #d3dee9; --n400: #b2c1d1;
            --n500: #90a1b5; --n600: #71839a; --n700: #56677e; --n900: #212d3b;
            --a100: #eaf3fc; --a200: #d0e4f8; --a300: #a7caee; --a500: #3f84c5;
            --a700: #1d4f83; --a800: #143a61; --a900: #0d2740;
            --t200: #c8eaee; --t500: #3f9ba6; --t600: #2c7d88; --t900: #0f2f34;
          }
          * { box-sizing: border-box; }

          /* Плавность как в станции: цвет и нажатие приходят переходом, а не
             щелчком. Ход короткий, иначе панель кажется задумчивой. */
          button, input, .bay, .file, .event, .metric, .card, .pill, .tag {
            transition: background-color .14s ease-out, color .14s ease-out,
                        border-color .14s ease-out, transform .09s ease-out,
                        opacity .14s ease-out;
          }
          button:active { transform: scale(.97); }

          /* Полоса заполнения и полосы гнёзд догоняют новое значение сами. */
          .fill { transition: width .5s cubic-bezier(.25, .8, .25, 1),
                              background-color .3s ease-out; }

          /* Смена раздела: содержимое проявляется и чуть поднимается. */
          @keyframes appear {
            from { opacity: 0; transform: translateY(8px); }
            to   { opacity: 1; transform: none; }
          }
          .appear { animation: appear .18s cubic-bezier(.25, .8, .25, 1) both; }

          @keyframes rise {
            from { transform: translateY(100%); }
            to   { transform: none; }
          }

          @keyframes dim {
            from { opacity: 0; }
            to   { opacity: 1; }
          }

          /* Кому анимации мешают, тот их и не увидит: браузер об этом
             спрашивают, а не решают за него. */
          @media (prefers-reduced-motion: reduce) {
            *, .appear { animation: none !important; transition: none !important; }
          }
          /* Скрытое должно быть скрыто: вход и панель заданы через grid и
             flex, а это перебивает атрибут hidden, и форма входа оставалась
             поверх панели. */
          [hidden] { display: none !important; }
          body {
            margin: 0; background: var(--bg); color: var(--text);
            font: 15px/1.5 "Manrope", system-ui, sans-serif;
          }
          button, input { font: inherit; }
          h1, h2, .h { font-family: "Nunito", system-ui, sans-serif; font-weight: 800; }

          /* вход */
          #login { min-height: 100vh; display: grid; place-items: center; padding: 20px; }
          #login form {
            width: 100%; max-width: 380px; background: var(--panel); border-radius: 28px;
            padding: 22px; display: grid; gap: 12px;
          }
          #login .t { font-family: "Nunito", system-ui, sans-serif; font-weight: 800; font-size: 20px; }
          #login .s { font-size: 13.5px; color: var(--n700); }
          input {
            min-height: 52px; border-radius: 999px; border: 1px solid var(--line);
            padding: 0 18px; background: var(--panel); font-size: 15px; width: 100%;
          }
          .go {
            min-height: 52px; border: 0; border-radius: 999px; background: var(--accent);
            color: var(--panel); font-size: 15.5px; font-weight: 700; cursor: pointer;
          }
          .warn { color: #6e4700; font-size: 13px; min-height: 18px; }

          /* каркас */
          #panel { min-height: 100vh; display: flex; flex-direction: column; }
          aside { display: none; }
          .top {
            flex: none; display: flex; align-items: center; gap: 12px;
            padding: 12px 16px; background: var(--panel); border-bottom: 1px solid var(--line);
          }
          /* Логотип тот же, что в киоске: панель должна узнаваться своей.
             Если картинка не пришла, остаётся подпись под ней. */
          .logo { height: 34px; width: auto; flex: none; object-fit: contain; }
          .top .who { flex: 1; min-width: 0; }
          /* Заголовок раздела и пилюли служб живут в шапке только на широком
             экране: на телефоне разделы подписаны полосой снизу, а служб
             четыре, и в узкую шапку они не встают. */
          .top .desk, .top .pills { display: none; }
          .top .name { font-family: "Nunito", system-ui, sans-serif; font-weight: 800; font-size: 16.5px; }
          .top .sub { font-size: 12px; color: var(--n600); }
          .ghost {
            min-height: 40px; padding: 0 16px; border: 1px solid var(--line);
            border-radius: 999px; background: var(--bg); color: var(--n700);
            font-size: 13.5px; font-weight: 600; cursor: pointer; white-space: nowrap;
          }
          main { flex: 1; min-height: 0; overflow: auto; padding: 14px 16px 20px; }
          .stack { display: flex; flex-direction: column; gap: 12px; }

          /* полоса разделов на телефоне */
          .tabs {
            flex: none; display: flex; gap: 4px; padding: 8px 10px;
            background: var(--panel); border-top: 1px solid var(--line);
          }
          .tabs button {
            flex: 1; min-height: 52px; display: flex; flex-direction: column;
            align-items: center; justify-content: center; gap: 3px; border: 0;
            border-radius: 20px; background: transparent; color: var(--n700); cursor: pointer;
          }
          .tabs button i { font-size: 17px; font-style: normal; line-height: 1; }
          .tabs button span { font-size: 11.5px; font-weight: 700; }
          .tabs button.on { background: var(--a100); color: var(--a700); }

          /* обзор */
          .metrics { display: grid; gap: 8px;
            grid-template-columns: repeat(auto-fit, minmax(110px, 1fr)); }
          .metric { padding: 12px 10px; border-radius: 22px; background: var(--panel); }
          .metric b { font-family: "Nunito", system-ui, sans-serif; font-weight: 800;
            font-size: 26px; line-height: 1; display: block; }
          .metric span { margin-top: 4px; font-size: 11.5px; font-weight: 600; display: block; }
          .metric.work { background: var(--a200); color: var(--a900); }
          .metric.done { background: var(--t200); color: var(--t900); }
          .metric.bad { background: var(--a800); color: var(--panel); }
          .card { padding: 16px; border-radius: 24px; background: var(--panel);
            display: flex; flex-direction: column; gap: 10px; }
          .card .h { font-size: 15px; }
          .row { display: flex; justify-content: space-between; align-items: baseline; gap: 10px; }
          .muted { font-size: 12.5px; color: var(--n700); }
          .track { height: 12px; border-radius: 999px; background: var(--n300); overflow: hidden; }
          .fill { height: 100%; border-radius: 999px; background: var(--t500); }
          .alert {
            display: flex; align-items: center; gap: 12px; padding: 14px 16px; width: 100%;
            text-align: left; border: 0; border-radius: 24px; background: var(--a800);
            color: var(--panel); font-size: 14px; font-weight: 600; cursor: pointer;
          }
          .service { display: flex; align-items: center; gap: 10px; min-height: 38px; }
          .dot { width: 9px; height: 9px; flex: none; border-radius: 999px; background: var(--n400); }
          .dot.up { background: var(--t500); }
          .service .k { flex: 1; font-size: 14px; font-weight: 600; }
          .service .v { font-size: 13px; color: var(--n700); }

          /* отсеки */
          .bays { display: flex; flex-direction: column; gap: 8px; }
          .bay {
            display: flex; align-items: center; gap: 12px; min-height: 70px; width: 100%;
            padding: 10px 14px; text-align: left; border: 1px solid var(--line);
            border-radius: 24px; background: var(--panel); color: var(--text); cursor: pointer;
          }
          .bay .n {
            width: 38px; height: 38px; flex: none; border-radius: 999px; display: grid;
            place-items: center; background: var(--n200); color: var(--text);
            font-size: 15px; font-weight: 700;
          }
          .bay .body { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 4px; }
          .bay .state { font-family: "Nunito", system-ui, sans-serif; font-weight: 800; }
          .bay .person { font-size: 14px; font-weight: 700; overflow: hidden;
            text-overflow: ellipsis; white-space: nowrap; }
          .bay .line { font-size: 12px; opacity: .8; }
          .bay .pct { flex: none; font-size: 14px; font-weight: 700; }
          .bay .track { height: 7px; }
          .bay.work { background: var(--a100); border-color: var(--a500); }
          .bay.done { background: var(--t200); border-color: var(--t500); }
          .bay.bad { background: var(--a800); border-color: var(--a800); color: var(--panel); }
          .bay.bad .n { background: var(--a300); color: var(--a900); }
          .bay.bad .track { background: var(--a700); }

          /* архив и журнал */
          .find { display: flex; flex-direction: column; gap: 8px; }
          .chips { display: flex; gap: 6px; overflow: auto; padding-bottom: 2px; }
          .chips button {
            flex: none; min-height: 42px; padding: 0 16px; border: 1px solid var(--line);
            border-radius: 999px; background: var(--panel); color: var(--text);
            font-size: 13.5px; font-weight: 700; cursor: pointer;
          }
          .chips button.on { background: var(--accent); border-color: var(--accent); color: var(--panel); }
          .file { padding: 14px; border-radius: 24px; background: var(--panel);
            display: flex; flex-direction: column; gap: 8px; }
          .file .top-line { display: flex; align-items: baseline; gap: 10px; }
          .file .fname { flex: 1; min-width: 0; font-size: 14.5px; font-weight: 700;
            overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
          .tag { flex: none; padding: 3px 10px; border-radius: 999px; background: var(--n200);
            font-size: 11.5px; font-weight: 700; }
          .file .acts { display: flex; gap: 8px; }
          .file .acts button { flex: 1; min-height: 44px; border-radius: 999px;
            border: 1px solid var(--line); background: var(--bg); color: var(--text);
            font-size: 13.5px; font-weight: 600; cursor: pointer; }
          .file .acts button.load { border: 0; background: var(--accent); color: var(--panel);
            font-weight: 700; }
          .event { padding: 13px 15px; border-radius: 22px; background: var(--panel);
            display: flex; flex-direction: column; gap: 5px; }
          .event .head { display: flex; align-items: center; gap: 8px; }
          .event .time { font-size: 12px; color: var(--n600); }
          .event .text { font-size: 13.5px; }
          .empty { padding: 18px; border-radius: 24px; background: var(--panel);
            color: var(--n700); font-size: 13.5px; }

          /* лист действий */
          .sheet {
            position: fixed; inset: 0; z-index: 80; display: flex; flex-direction: column;
            justify-content: flex-end; background: #212d3b73;
            animation: dim .16s ease-out both;
          }
          .sheet .inner {
            animation: rise .24s cubic-bezier(.25, .8, .25, 1) both;
            padding: 20px 18px 30px; border-radius: 32px 32px 0 0; background: var(--surface);
            display: flex; flex-direction: column; gap: 12px;
          }
          .sheet .grip { width: 44px; height: 5px; border-radius: 999px;
            background: var(--n400); align-self: center; }
          .sheet .t { font-family: "Nunito", system-ui, sans-serif; font-weight: 800; font-size: 20px; }
          .sheet .s { font-size: 13.5px; color: var(--n700); }
          .sheet button {
            min-height: 52px; border: 1px solid var(--line); border-radius: 999px;
            background: var(--panel); color: var(--text); font-size: 15px; font-weight: 700;
            cursor: pointer;
          }
          .sheet button.main { border: 0; background: var(--accent); color: var(--panel); }
          .sheet button.plain { border: 0; background: transparent; color: var(--n700);
            font-weight: 600; }

          /* компьютер: то же самое, боковым меню и шире */
          @media (min-width: 900px) {
            #panel { flex-direction: row; }
            aside {
              display: flex; width: 236px; flex: none; flex-direction: column; gap: 6px;
              padding: 18px 14px; background: var(--panel); border-right: 1px solid var(--line);
            }
            aside .brand { display: flex; align-items: center; gap: 10px; margin-bottom: 12px; }
            aside .brand .logo { height: 30px; }
            aside .brand .name { font-family: "Nunito", system-ui, sans-serif;
              font-weight: 800; font-size: 15px; }
            aside .brand .place { font-size: 11.5px; color: var(--n600); }
            aside nav { display: flex; flex-direction: column; gap: 6px; }
            aside nav button {
              display: flex; align-items: center; gap: 10px; min-height: 48px; padding: 0 16px;
              text-align: left; border: 0; border-radius: 999px; background: transparent;
              color: var(--text); font-size: 14.5px; font-weight: 700; cursor: pointer;
            }
            aside nav button.on { background: var(--accent); color: var(--panel); }
            aside .path { margin-top: auto; padding: 12px 14px; border-radius: 20px;
              background: var(--bg); font-size: 12px; color: var(--n700); word-break: break-all; }
            .side { flex: 1; min-width: 0; display: flex; flex-direction: column; }
            .top .logo, .top .who .name { display: none; }
            .top { padding: 14px 22px; flex-wrap: wrap; }
            .top .desk { display: block; font-family: "Nunito", system-ui, sans-serif;
              font-weight: 800; font-size: 18px; }
            .top .pills { display: flex; gap: 8px; flex-wrap: wrap; margin-left: auto; }
            .top .logo, .top > .ghost { display: none; }
            .pill { display: inline-flex; align-items: center; gap: 7px; padding: 6px 12px;
              border-radius: 999px; background: var(--bg); font-size: 12.5px; font-weight: 600; }
            .tabs { display: none; }
            main { padding: 20px 22px 26px; }
            .metrics { grid-template-columns: repeat(auto-fit, minmax(210px, 1fr)); gap: 14px; }
            .metric { padding: 20px 22px; border-radius: 26px; }
            .metric b { font-size: 44px; }
            .metric span { font-size: 13.5px; }
            /* Карточки по содержимому: иначе том архива растягивался под
               соседнюю и внутри оставалась пустота. */
            .two { display: grid; grid-template-columns: 1.4fr 1fr; gap: 14px;
              align-items: start; }
            .bays { display: grid; grid-template-columns: repeat(auto-fill, minmax(210px, 1fr));
              gap: 14px; }
            .bay { flex-direction: column; align-items: stretch; min-height: 168px;
              padding: 16px 18px; border-width: 2px; border-radius: 28px; gap: 9px; }
            .bay .head { display: flex; align-items: center; gap: 10px; }
            .bay .head .person { flex: 1; min-width: 0; }
            .bay .state { font-family: "Nunito", system-ui, sans-serif; font-weight: 800;
              font-size: 19px; }
            .bay .track { margin-top: auto; height: 10px; }
            .find { flex-direction: row; align-items: center; flex-wrap: wrap; gap: 12px; }
            .find input { width: 320px; }
            .files { border-radius: 26px; background: var(--panel); padding: 6px 20px 14px;
              overflow: auto; }
            table { width: 100%; border-collapse: collapse; font-size: 14px; }
            th, td { text-align: left; padding: 10px 8px; border-bottom: 1px solid var(--line); }
            th { font-size: 12px; text-transform: uppercase; letter-spacing: .04em;
              color: var(--n700); }
            .sheet { justify-content: center; align-items: center; }
            .sheet .inner { width: 420px; border-radius: 28px; }
          }
        </style>
        </head>
        <body>
        <div id="login">
          <form onsubmit="signIn(event)">
            <img class="logo" src="/logo.png" alt="BestCam" style="height:38px;align-self:start">
            <div class="t">BestCam BC-10</div>
            <div class="s">Панель показывает состояние станции, её архив и журнал.</div>
            <input id="account" value="admin" autocomplete="username">
            <input id="password" type="password" placeholder="пароль" autocomplete="current-password">
            <button class="go" type="submit">Войти</button>
            <div class="warn" id="loginError"></div>
          </form>
        </div>

        <div id="panel" hidden>
          <aside>
            <div class="brand">
              <img class="logo" src="/logo.png" alt="BestCam">
              <div>
                <div class="name">BC-10</div>
                <div class="place" id="place"></div>
              </div>
            </div>
            <nav id="sideTabs"></nav>
            <div class="path" id="sidePath"></div>
            <button class="ghost" onclick="askLogout()">Выйти</button>
          </aside>

          <div class="side">
            <div class="top">
              <img class="logo" src="/logo.png" alt="BestCam">
              <div class="who">
                <div class="name" id="station">BC-10</div>
                <div class="desk" id="desk"></div>
                <div class="sub" id="clock"></div>
              </div>
              <div class="pills" id="pills"></div>
              <button class="ghost" onclick="askLogout()">Выйти</button>
            </div>

            <main><div class="stack" id="view"></div></main>

            <div class="tabs" id="bottomTabs"></div>
          </div>
        </div>

        <div id="sheet"></div>

        <script>
        const TABS = [
          { id: 'overview', label: 'Обзор', icon: '⌂' },
          { id: 'bays', label: 'Отсеки', icon: '▦' },
          { id: 'archive', label: 'Архив', icon: '⛁' },
          { id: 'log', label: 'Журнал', icon: '☰' },
        ];
        const KINDS = [
          { id: '', label: 'Все' },
          { id: 'Video', label: 'Видео' },
          { id: 'Photo', label: 'Фото' },
          { id: 'Audio', label: 'Звук' },
          { id: 'Log', label: 'Журналы' },
        ];

        let token = sessionStorage.getItem('token') || '';
        let view = 'overview';
        let kind = '';
        let query = '';
        let timer = 0;
        let _shown = '';

        // Панель обновляется каждые три секунды. Если каждый раз переписывать
        // разметку целиком, список мигает, нажатие теряется под пальцем, а
        // переходы не успевают доиграть. Поэтому разметка ставится только
        // тогда, когда она и правда стала другой.
        function put(id, html, animate) {
          const node = document.getElementById(id);
          if (!node || node.dataset.html === html) return false;

          // Панель обновляется под руками: если в этот момент оператор
          // набирает условие поиска, перерисовка не должна съесть ни текст,
          // ни место каретки.
          const active = document.activeElement;
          const keep = active && node.contains(active) && active.id
            ? { id: active.id, value: active.value, at: active.selectionStart }
            : null;

          node.dataset.html = html;
          node.innerHTML = html;

          if (keep) {
            const back = document.getElementById(keep.id);
            if (back) {
              back.value = keep.value;
              back.focus();
              try { back.setSelectionRange(keep.at, keep.at); } catch (e) { }
            }
          }

          if (animate) {
            node.classList.remove('appear');
            // Пересчёт нужен, чтобы анимация запустилась заново.
            void node.offsetWidth;
            node.classList.add('appear');
          }

          return true;
        }

        function esc(text) {
          return String(text ?? '').replace(/[&<>"]/g,
            c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);
        }

        function gb(bytes) { return (bytes / 1024 / 1024 / 1024).toFixed(1) + ' ГБ'; }

        function size(bytes) {
          if (bytes >= 1024 * 1024 * 1024) return gb(bytes);
          if (bytes >= 1024 * 1024) return (bytes / 1024 / 1024).toFixed(1) + ' МБ';
          return Math.max(1, Math.round(bytes / 1024)) + ' КБ';
        }

        async function signIn(e) {
          e.preventDefault();
          const body = new FormData();
          body.append('account', document.getElementById('account').value);
          body.append('password', document.getElementById('password').value);
          const res = await fetch('/api/login', { method: 'POST', body });
          if (res.status === 429) {
            document.getElementById('loginError').textContent =
              'слишком много попыток, подождите минуту';
            return;
          }
          if (!res.ok) {
            document.getElementById('loginError').textContent =
              'учётная запись или пароль не подошли';
            return;
          }
          token = (await res.json()).token;
          sessionStorage.setItem('token', token);
          start();
        }

        function start() {
          document.getElementById('login').hidden = true;
          document.getElementById('panel').hidden = false;
          drawTabs();
          tick();
          if (!timer) timer = setInterval(tick, 3000);
        }

        function drawTabs() {
          const buttons = TABS.map(t => `
            <button class="${t.id === view ? 'on' : ''}" onclick="show('${t.id}')">
              <i>${t.icon}</i><span>${t.label}</span>
            </button>`).join('');

          put('bottomTabs', buttons, false);
          put('sideTabs', buttons, false);
          document.getElementById('desk').textContent =
            (TABS.find(t => t.id === view) || TABS[0]).label;
        }

        function show(name) {
          if (view === name) return;

          view = name;
          drawTabs();
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

        async function post(path) {
          const res = await fetch(path, { method: 'POST', headers: { 'X-Token': token } });
          return res.ok;
        }

        async function tick() {
          const s = await get('/api/state');
          if (!s) return;

          document.getElementById('station').textContent = s.station || 'BC-10';
          document.getElementById('place').textContent =
            (s.station || '').split('·').slice(1).join('·').trim();
          document.getElementById('clock').textContent =
            new Date(s.at).toLocaleTimeString('ru-RU') + ' · ' + (s.os || '');
          document.getElementById('sidePath').textContent = s.archiveLabel || '';
          put('pills', services(s)
            .map(x => `<span class="pill"><span class="dot ${x.up ? 'up' : ''}"></span>${esc(x.k)} · ${esc(x.v)}</span>`)
            .join(''), false);

          if (view === 'overview') drawOverview(s);
          if (view === 'bays') drawBays(s);
          if (view === 'archive') await drawArchive();
          if (view === 'log') await drawLog();
        }

        function services(s) {
          return [
            { k: 'Монитор USB', v: 'активен', up: true },
            { k: 'Локальная база', v: 'работает', up: true },
            { k: 'Сеть', v: s.networkUp ? 'доступна' : 'недоступна', up: s.networkUp },
            { k: 'Отправка', v: s.ftpEnabled ? s.ftpState : 'выключена', up: s.ftpEnabled },
          ];
        }

        function drawOverview(s) {
          const used = s.archiveTotalBytes - s.archiveFreeBytes;
          const width = s.archiveTotalBytes > 0
            ? Math.min(100, Math.round(used / s.archiveTotalBytes * 100)) : 0;

          const alert = s.trouble
            ? `<button class="alert" onclick="show('bays')">
                 <span style="flex:1">${esc(s.trouble)}</span><span>›</span>
               </button>` : '';

          const failed = s.failed > 0
            ? `<div class="metric bad"><b>${s.failed}</b><span>ошибки</span></div>` : '';

          put('view', `
            <div class="metrics">
              <div class="metric work"><b>${s.copying}</b><span>копируется</span></div>
              <div class="metric done"><b>${s.done}</b><span>готово</span></div>
              <div class="metric"><b>${s.free}</b><span>свободно окон</span></div>
              ${failed}
            </div>
            ${alert}
            <div class="two">
              <div class="card">
                <div class="row">
                  <span class="h">Том архива</span>
                  <span class="muted">свободно ${gb(s.archiveFreeBytes)} из ${gb(s.archiveTotalBytes)}</span>
                </div>
                <div class="track"><div class="fill" style="width:${width}%"></div></div>
                <div class="muted">${esc(s.archiveLabel || 'архив')}</div>
                <div style="display:flex;gap:8px;flex-wrap:wrap;margin-top:auto">
                  <button class="go" style="padding:0 22px" onclick="askRestart()">Перезапустить станцию</button>
                  <button class="ghost" style="min-height:52px" onclick="tick()">Обновить состояние</button>
                </div>
              </div>
              <div class="card">
                <span class="h">Состояние служб</span>
                ${services(s).map(x => `
                  <div class="service">
                    <span class="dot ${x.up ? 'up' : ''}"></span>
                    <span class="k">${esc(x.k)}</span>
                    <span class="v">${esc(x.v)}</span>
                  </div>`).join('')}
                <div class="muted">Версия: ${esc(s.version || 'неизвестна')}</div>
              </div>
            </div>`, view !== _shown);

          _shown = view;
        }

        function bayClass(state) {
          if (state === 'Копирование') return 'work';
          if (state === 'Готово') return 'done';
          if (state === 'Ошибка') return 'bad';
          return '';
        }

        // На телефоне отсек это строка, на компьютере карточка: так в шаблоне
        // задания, и одной разметкой это не выразить без перекосов.
        function drawBays(s) {
          const wide = window.matchMedia('(min-width: 900px)').matches;

          const row = b => `
            <button class="bay ${bayClass(b.state)}" onclick="askBay(${b.slot}, '${esc(b.state)}')">
              <span class="n">${b.slot + 1}</span>
              <span class="body">
                <span class="person">${esc(b.employee || b.camera || 'нет регистратора')}</span>
                <span class="line">${esc(b.state)}${b.files ? ' · ' + esc(b.files) : ''}</span>
                <span class="track"><span class="fill" style="width:${b.percent}%;display:block"></span></span>
              </span>
              <span class="pct">${b.percent > 0 ? b.percent + '%' : ''}</span>
            </button>`;

          const card = b => `
            <button class="bay ${bayClass(b.state)}" onclick="askBay(${b.slot}, '${esc(b.state)}')">
              <span class="head">
                <span class="n">${b.slot + 1}</span>
                <span class="person">${esc(b.employee || b.camera || 'нет регистратора')}</span>
              </span>
              <span class="head">
                <span class="state" style="flex:1">${esc(b.state)}</span>
                <span class="pct">${b.percent > 0 ? b.percent + '%' : ''}</span>
              </span>
              <span class="line">${esc(b.files || '')}</span>
              <span class="track"><span class="fill" style="width:${b.percent}%;display:block"></span></span>
            </button>`;

          put('view', '<div class="bays">' + s.bays.map(wide ? card : row).join('') + '</div>',
            view !== _shown);

          _shown = view;
        }

        async function drawArchive() {
          const rows = await get('/api/archive?name=' + encodeURIComponent(query)
            + '&kind=' + encodeURIComponent(kind)) || [];

          const chips = KINDS.map(k => `
            <button class="${k.id === kind ? 'on' : ''}" onclick="setKind('${k.id}')">${k.label}</button>`)
            .join('');

          const head = `
            <div class="find">
              <input id="q" value="${esc(query)}" placeholder="сотрудник, устройство, файл"
                     oninput="setQuery(this.value)">
              <div class="chips">${chips}</div>
            </div>`;

          const wide = window.matchMedia('(min-width: 900px)').matches;

          const body = rows.length === 0
            ? '<div class="empty">Записей по этим условиям нет.</div>'
            : wide
              ? `<div class="files"><table>
                   <thead><tr><th>Файл</th><th style="width:104px">Тип</th>
                   <th>Сотрудник, устройство, размер</th><th style="width:270px"></th></tr></thead>
                   <tbody>${rows.map(r => `
                     <tr>
                       <td>${esc(r.file)}</td>
                       <td><span class="tag">${esc(r.kind)}</span></td>
                       <td class="muted">${esc(meta(r))}</td>
                       <td style="display:flex;gap:8px;justify-content:flex-end">
                         <button class="ghost" style="padding:0 14px"
                                 onclick="watch('${esc(r.path)}')">Смотреть</button>
                         <button class="go" style="min-height:40px;padding:0 16px;font-size:13.5px"
                                 onclick="download('${esc(r.path)}', '${esc(r.file)}')">Скачать</button>
                       </td>
                     </tr>`).join('')}
                   </tbody></table></div>`
              : rows.map(r => `
                  <div class="file">
                    <div class="top-line">
                      <span class="fname">${esc(r.file)}</span>
                      <span class="tag">${esc(r.kind)}</span>
                    </div>
                    <div class="muted">${esc(meta(r))}</div>
                    <div class="acts">
                      <button onclick="watch('${esc(r.path)}')">Смотреть</button>
                      <button class="load" onclick="download('${esc(r.path)}', '${esc(r.file)}')">Скачать</button>
                    </div>
                  </div>`).join('');

          put('view', head + body, view !== _shown);
          _shown = view;
        }

        function meta(row) {
          const parts = [];
          if (row.employee) parts.push(row.employee);
          if (row.camera) parts.push('камера ' + row.camera);
          parts.push(new Date(row.collected).toLocaleString('ru-RU'));
          parts.push(size(row.size));
          if (row.shielded) parts.push('под защитой');
          return parts.join(' · ');
        }

        function setKind(value) { kind = value; drawArchive(); }

        let typing = 0;
        function setQuery(value) {
          query = value;
          clearTimeout(typing);
          // Запрос уходит не на каждую букву: станция и так занята сбором.
          typing = setTimeout(drawArchive, 400);
        }

        async function drawLog() {
          const rows = await get('/api/log') || [];
          const html = rows.length === 0
            ? '<div class="empty">Событий за сутки нет.</div>'
            : rows.map(l => `
                <div class="event">
                  <div class="head">
                    <span class="tag">${esc(l.kind)}</span>
                    <span class="time">${new Date(l.at).toLocaleString('ru-RU')}</span>
                  </div>
                  <div class="text">${esc(l.text)}</div>
                </div>`).join('');

          put('view', html, view !== _shown);
          _shown = view;
        }

        // Запись отдаётся с ключом в заголовке, поэтому ссылкой её не открыть:
        // забираем её запросом и показываем уже полученный файл.
        async function fetchFile(path) {
          const res = await fetch('/api/file?p=' + encodeURIComponent(path),
            { headers: { 'X-Token': token } });
          return res.ok ? res.blob() : null;
        }

        async function watch(path) {
          const blob = await fetchFile(path);
          if (!blob) return;
          window.open(URL.createObjectURL(blob), '_blank');
        }

        async function download(path, name) {
          const blob = await fetchFile(path);
          if (!blob) return;
          const link = document.createElement('a');
          link.href = URL.createObjectURL(blob);
          link.download = name;
          link.click();
          URL.revokeObjectURL(link.href);
        }

        function sheet(title, text, actions) {
          document.getElementById('sheet').innerHTML = `
            <div class="sheet" onclick="if (event.target === this) closeSheet()">
              <div class="inner">
                <div class="grip"></div>
                <div class="t">${esc(title)}</div>
                <div class="s">${esc(text)}</div>
                ${actions.map((a, i) => `
                  <button class="${a.main ? 'main' : ''}" onclick="sheetAction(${i})">${esc(a.label)}</button>`)
                  .join('')}
                <button class="plain" onclick="closeSheet()">Отмена</button>
              </div>
            </div>`;
          window.sheetActions = actions;
        }

        function sheetAction(index) {
          const action = (window.sheetActions || [])[index];
          closeSheet();
          if (action && action.run) action.run();
        }

        function closeSheet() { document.getElementById('sheet').innerHTML = ''; }

        function askBay(slot, state) {
          sheet('Отсек ' + (slot + 1), 'Состояние: ' + state + '. Действие выполнится на станции.', [
            { label: 'Первым в очередь', main: true, run: () => bay(slot, 'priority') },
            { label: 'Только зарядка', run: () => bay(slot, 'charge') },
            { label: 'Возобновить загрузку', run: () => bay(slot, 'resume') },
          ]);
        }

        async function bay(slot, action) {
          await post('/api/bay/' + slot + '/' + action);
          tick();
        }

        function askRestart() {
          sheet('Перезапустить станцию', 'Программа закроется и поднимется заново. Незавершённые загрузки продолжатся после запуска.', [
            { label: 'Перезапустить', main: true, run: async () => { await post('/api/restart'); } },
          ]);
        }

        function askLogout() {
          sheet('Выйти из панели', 'Ключ входа будет забыт, и панель попросит пароль заново.', [
            {
              label: 'Выйти', main: true, run: () => {
                sessionStorage.removeItem('token');
                location.reload();
              },
            },
          ]);
        }

        // Ширина решает, строка это или карточка, поэтому поворот телефона
        // требует перерисовки.
        window.addEventListener('resize', () => tick());

        if (token) start();
        </script>
        </body>
        </html>
        """;
}
