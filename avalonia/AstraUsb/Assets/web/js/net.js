// Связь со станцией: ключ входа и запросы с ним.
import { toast } from './ui.js';

export let token = sessionStorage.getItem('token') || '';

export function setToken(value) {
  token = value;
  sessionStorage.setItem('token', value);
}

export async function api(path, o = {}) {
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
export const post = (path, body, quiet) => api(path, { method: 'POST', body: body ?? {}, quiet });

export function logout() {
  sessionStorage.removeItem('token');
  location.hash = '';
  location.reload();
}
