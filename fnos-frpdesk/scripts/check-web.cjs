const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const nodes = new Map();
const node = selector => {
  if (!nodes.has(selector)) nodes.set(selector, {
    innerHTML: '', textContent: '', className: '',
    classList: { toggle() {}, add() {}, remove() {} }
  });
  return nodes.get(selector);
};
const context = vm.createContext({
  document: { querySelector: node, querySelectorAll: () => [] },
  URL, console, setTimeout, clearTimeout, location: { pathname: '/app/frpdesk' },
  fetch: async () => ({ ok: true, status: 200, json: async () => [] })
});
const source = fs.readFileSync(path.join(__dirname, '../web/app.js'), 'utf8');
vm.runInContext(source.slice(0, source.indexOf("document.addEventListener('click'")), context);
const evaluate = code => vm.runInContext(code, context);

(async () => {
  assert.equal(evaluate(`addressInfo({serverAddr:'relay.example.com'}, {type:'tcp',localPort:3389,remotePort:33389}).remote`), 'relay.example.com:33389');
  assert.equal(evaluate(`addressInfo({serverAddr:'relay.example.com'}, {type:'https',customDomains:['home.example.com']}).url`), 'https://home.example.com/');
  assert.equal(evaluate(`addressInfo({serverAddr:'relay.example.com'}, {type:'https',customDomains:['user@bad.example.com']}).url`), '');
  assert.equal(evaluate(`addressInfo({}, {type:'stcp'}).remote`), '');
  assert.ok(!evaluate(`addressValue("<img onerror='bad'>")`).includes('<img'));
  assert.ok(evaluate(`formatLogs([{at:'2026-10-10T00:00:00Z',line:'\\x1b[31mconnection refused\\x1b[0m'}])`).endsWith('connection refused'));
  evaluate(`dashboard()`);
  assert.ok(node('#content').innerHTML.includes('添加第一个配置'));
  assert.ok(!node('#content').innerHTML.includes('metric'));
  evaluate(`state.configs = [{id:'one',name:'中文配置',filename:'one.toml',serverAddr:'relay.example.com',serverPort:7000,raw:'original',proxies:[]}];
    state.selected = 'one';
    state.drafts.set('one', {name:'修改后的名字',raw:'unsaved\\nmultiline'});
    configs();`);
  assert.ok(node('#content').innerHTML.includes('修改后的名字'));
  assert.ok(node('#content').innerHTML.includes('unsaved\nmultiline'));
  const before = node('#content').innerHTML;
  await evaluate('load(true)');
  assert.equal(node('#content').innerHTML, before, 'background refresh must not replace the editor');
  const css = fs.readFileSync(path.join(__dirname, '../web/app.css'), 'utf8');
  const contentRule = css.match(/\.content\s*\{([^}]+)\}/)[1];
  assert.match(contentRule, /margin:\s*0/);
  assert.doesNotMatch(contentRule, /margin:\s*auto/);
  console.log('Web checks passed: address rendering, escaping, empty state, draft retention and top alignment.');
})().catch(error => { console.error(error); process.exitCode = 1; });
