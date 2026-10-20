const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const source = fs.readFileSync(process.argv[2], 'utf8');
const script = source.match(/<script>([\s\S]*?)<\/script>/)[1];
const wide = process.argv[3] === 'wide';
const file = "');alert(1);('x.mp4";
const path = 'Device7/DCIM/' + file;
const rows = process.argv[4] ? JSON.parse(process.argv[4])
  : [{ file, path, kind: 'Video', size: 3, collected: '2026-10-20' }];
const decode = value => value.replace(/&(amp|lt|gt|quot|#39);/g,
  (_, name) => ({ amp: '&', lt: '<', gt: '>', quot: '"', '#39': "'" })[name]);
const listeners = {};
const view = { dataset: {}, innerHTML: '', contains: () => false,
  classList: { remove() {}, add() {} },
  addEventListener(type, handler) { listeners[type] = handler; } };
const document = { activeElement: null, getElementById: id => id === 'view' ? view : null };
const sandbox = { document, sessionStorage: { getItem: () => '' },
  window: { matchMedia: () => ({ matches: wide }), addEventListener() {} },
  alert: () => { sandbox.executions++; }, executions: 0, calls: [] };
vm.createContext(sandbox);
vm.runInContext(script, sandbox);
vm.runInContext(`get = async () => ${JSON.stringify(rows)};
watch = path => calls.push(['watch', path]);
download = (path, file) => calls.push(['download', path, file]);`, sandbox);
(async () => {
  await sandbox.drawArchive();
  for (const [label, expected] of [['Смотреть', ['watch', path]], ['Скачать', ['download', path, file]]]) {
    const tag = [...view.innerHTML.matchAll(/<button\b([^>]*)>([\s\S]*?)<\/button>/g)]
      .find(match => match[2].trim() === label);
    assert.ok(tag, label);
    const attributes = Object.fromEntries([...tag[1].matchAll(/([\w-]+)="([^"]*)"/g)]
      .map(match => [match[1], decode(match[2])]));
    const dataset = Object.fromEntries(Object.entries(attributes).filter(([key]) => key.startsWith('data-'))
      .map(([key, value]) => [key.slice(5).replace(/-([a-z])/g, (_, c) => c.toUpperCase()), value]));
    const button = { dataset, closest: () => button };
    if (attributes.onclick) vm.runInContext(attributes.onclick, sandbox);
    else { assert.ok(listeners.click, 'Обработчик нажатия'); listeners.click({ target: button }); }
    assert.equal(sandbox.executions, 0, 'Имя файла выполнило JavaScript');
    assert.deepEqual(JSON.parse(JSON.stringify(sandbox.calls)), [expected]);
    sandbox.calls.length = 0;
  }
  console.log(`PASS archive actions ${wide ? 'wide' : 'mobile'}`);
})().catch(error => { console.error(error); process.exitCode = 1; });
