// Окружение для проверок страницы панели: настоящая index.html в jsdom и
// поддельный сервер вместо fetch. Каждый тестовый файл идёт в своём процессе,
// поэтому состояние модулей между файлами не делится.
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { JSDOM, VirtualConsole } from 'jsdom';

const web = new URL('../../../AstraUsb/Assets/web/', import.meta.url);
export const jsUrl = name => new URL(`js/${name}.js`, web).href;
const html = readFileSync(fileURLToPath(new URL('index.html', web)), 'utf8');

const realSetTimeout = globalThis.setTimeout;
export const wait = ms => new Promise(r => realSetTimeout(r, ms));
/** Даёт отработать цепочкам промисов и таймерам нулевой длины. */
export const flush = async (n = 5) => { for (let i = 0; i < n; i++) await new Promise(r => setImmediate(r)); };

/**
 * Поднимает страницу и ставит её глобальные объекты туда, где их ждут модули.
 * Таймеры отвязываются от процесса: не закрытый опрос не должен держать его.
 */
export function boot({ reducedMotion = false, token = '' } = {}) {
  const dom = new JSDOM(html, { url: 'http://localhost:8099/', pretendToBeVisual: true, virtualConsole: new VirtualConsole() });
  const w = dom.window;
  if (token) w.sessionStorage.setItem('token', token);
  w.matchMedia = query => ({ matches: reducedMotion && /reduce/.test(query), media: query, addEventListener() {} });
  w.Element.prototype.setPointerCapture = () => {};
  w.scrollTo = () => {};
  w.URL.createObjectURL = () => 'blob:fake';
  w.URL.revokeObjectURL = () => {};
  w.HTMLAnchorElement.prototype.click = function () { (w.__clicked ||= []).push({ href: this.href, download: this.download }); };
  w.open = url => { (w.__opened ||= []).push(url); };

  for (const k of ['window', 'document', 'location', 'sessionStorage', 'matchMedia', 'FormData', 'scrollTo',
    'addEventListener', 'requestAnimationFrame', 'URL', 'URLSearchParams', 'Blob']) {
    Object.defineProperty(globalThis, k, { value: w[k] instanceof Function && !/^[A-Z]/.test(k) ? w[k].bind(w) : w[k], configurable: true, writable: true });
  }
  // Метка времени кадра должна идти от тех же часов, что performance.now() в страницы.
  globalThis.requestAnimationFrame = cb => w.requestAnimationFrame(() => cb(performance.now()));
  Object.defineProperty(globalThis, 'navigator', { value: w.navigator, configurable: true });

  for (const name of ['setTimeout', 'setInterval']) {
    const original = globalThis[name];
    if (original.__wrapped) continue;
    const wrapped = (fn, ms, ...args) => { const t = original(fn, ms, ...args); t.unref?.(); return t; };
    wrapped.__wrapped = true;
    globalThis[name] = wrapped;
  }

  const server = fakeServer();
  globalThis.fetch = server.fetch;
  return { dom, w, server };
}

/**
 * Сервер вместо станции. Маршрут задаётся как «МЕТОД /путь»; ответ функции:
 * число — код без тела, { status, body } — код с телом, иначе тело с кодом 200.
 */
export function fakeServer() {
  const routes = new Map();
  const calls = [];
  const server = {
    routes, calls,
    on(key, handler) { routes.set(key, typeof handler === 'function' ? handler : () => handler); return server; },
    last: key => [...calls].reverse().find(c => c.key === key),
    fetch: async (input, init = {}) => {
      const url = new URL(input, 'http://localhost:8099');
      const method = (init.method || 'GET').toUpperCase();
      const key = `${method} ${url.pathname}`;
      let body = init.body;
      if (typeof body === 'string') { try { body = JSON.parse(body); } catch { /* не JSON */ } }
      calls.push({ key, url, body, headers: init.headers || {} });
      const handler = routes.get(key);
      if (!handler) return reply(404, { error: 'нет маршрута ' + key });
      const out = await handler({ url, body, headers: init.headers || {} });
      if (typeof out === 'number') return reply(out);
      if (out instanceof Error) throw out;
      return out && out.status && 'body' in out ? reply(out.status, out.body) : reply(200, out);
    },
  };
  return server;
}

function reply(status, body) {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => { if (body === undefined) throw new Error('нет тела'); return body; },
    blob: async () => new Blob(['данные']),
    text: async () => JSON.stringify(body),
  };
}

/** Нажатие так, как его получает обработчик страницы. */
export const click = el => el.dispatchEvent(new window.MouseEvent('click', { bubbles: true, cancelable: true }));
export const act = (name, root = document) => root.querySelector(`[data-act=${name}]`);
export const type = (el, value) => { el.value = value; el.dispatchEvent(new window.Event('input', { bubbles: true })); };

/** Один обработчик клика на таблицу действий, как в каркасе страницы. */
export function wire(...tables) {
  const all = Object.assign({}, ...tables);
  document.addEventListener('click', e => {
    const el = e.target.closest('[data-act]');
    if (el && all[el.dataset.act]) all[el.dataset.act](el, e);
  });
  return all;
}
