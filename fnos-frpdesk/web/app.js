const state = { page: 'dashboard', configs: [], instances: [], selected: null, addressConfig: '', logConfig: '', drafts: new Map() };
const $ = s => document.querySelector(s);
const apiBase = () => location.pathname.startsWith('/app/frpdesk') ? '/app/frpdesk' : '';
const pageTitles = { dashboard: '概览', configs: '配置实例', addresses: '穿透地址', logs: '运行日志', settings: '设置' };
let toastTimer;
let renderVersion = 0;

async function api(path, init) {
  const r = await fetch(apiBase() + path, { headers: { 'Content-Type': 'application/json' }, ...init });
  if (!r.ok) {
    let message = '请求失败';
    try { message = (await r.json()).error || message; } catch {}
    throw Error(message);
  }
  return r.status === 204 ? null : r.json();
}
function esc(x) { return String(x ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])); }
function toast(text) {
  clearTimeout(toastTimer);
  $('#toast').textContent = text;
  $('#toast').classList.add('show');
  toastTimer = setTimeout(() => $('#toast').classList.remove('show'), 2400);
}
function connection(online) {
  $('#connection').textContent = online ? '已连接' : '连接中断';
  $('#connection').className = 'connection ' + (online ? 'online' : 'offline');
}
function instance(id) { return state.instances.find(x => x.profileId === id); }
function status(value) {
  const map = { running: ['运行中', 'running'], starting: ['启动中', 'starting'], stopping: ['停止中', 'starting'], failed: ['异常', 'failed'], exited: ['已退出', 'failed'], stopped: ['已停止', ''] };
  const v = map[value] || ['未知', ''];
  return `<span class="pill ${v[1]}">${v[0]}</span>`;
}
function statusSlot(id) { return `<span data-status="${esc(id)}">${status(instance(id)?.status || 'stopped')}</span>`; }
function command(label, name, id = '', classes = '') {
  return `<button class="btn ${classes}" data-command="${name}" data-id="${esc(id)}">${label}</button>`;
}
function empty(title, description, withImport = false) {
  return `<section class="panel empty"><h2>${title}</h2><p>${description}</p>${withImport ? command('导入 TOML', 'import', '', 'primary') : ''}</section>`;
}
async function copy(text) {
  if (!text) return;
  try {
    if (navigator.clipboard && window.isSecureContext) {
      await navigator.clipboard.writeText(text);
    } else {
      const input = document.createElement('textarea');
      input.value = text;
      input.style.cssText = 'position:fixed;left:-9999px;top:0';
      document.body.append(input);
      input.select();
      const ok = document.execCommand('copy');
      input.remove();
      if (!ok) throw Error('复制失败，请选择文本后复制');
    }
    toast('已复制');
  } catch (e) { toast(e.message || '复制失败，请选择文本后复制'); }
}
async function load(background = false) {
  [state.configs, state.instances] = await Promise.all([api('/api/configs'), api('/api/instances')]);
  connection(true);
  if (state.selected && !state.configs.some(x => x.id === state.selected)) state.selected = null;
  if (!background) { render(); return; }
  // Refresh live values without replacing editors, keyboard focus or text selection.
  document.querySelectorAll('[data-status]').forEach(node => {
    const html = status(instance(node.dataset.status)?.status || 'stopped');
    if (node.innerHTML !== html) node.innerHTML = html;
  });
  const values = metrics();
  document.querySelectorAll('[data-metric]').forEach(node => { node.textContent = values[node.dataset.metric]; });
  if (state.page === 'logs') await refreshLogs();
}
function render() {
  renderVersion++;
  document.querySelectorAll('#nav button').forEach(node => {
    const active = node.dataset.page === state.page;
    node.classList.toggle('active', active);
    if (active) node.setAttribute('aria-current', 'page'); else node.removeAttribute('aria-current');
  });
  $('#page-title').textContent = pageTitles[state.page];
  $('#import-button').classList.toggle('hidden', !state.configs.length || !['dashboard', 'configs'].includes(state.page));
  ({ dashboard, configs, addresses, logs, settings }[state.page])();
}
function metrics() {
  return {
    configs: state.configs.length,
    running: state.instances.filter(x => x.status === 'running').length,
    failed: state.instances.filter(x => ['failed', 'exited'].includes(x.status)).length,
    tunnels: state.configs.reduce((n, x) => n + (x.proxies || []).length, 0)
  };
}
function dashboard() {
  if (!state.configs.length) {
    $('#content').innerHTML = empty('添加第一个配置', '粘贴 TOML 配置即可开始。支持多个配置独立运行，关闭页面后实例继续运行。', true);
    return;
  }
  const values = metrics();
  const names = { configs: '配置总数', running: '运行实例', failed: '异常实例', tunnels: '穿透隧道' };
  $('#content').innerHTML = `<div class="metrics">${Object.entries(names).map(([key, label]) =>
    `<div class="metric"><small>${label}</small><strong data-metric="${key}">${values[key]}</strong></div>`).join('')}</div>
    <section class="panel"><div class="panel-heading"><h2>实例</h2><span class="muted">${state.configs.length} 个配置</span></div>
    ${state.configs.map(c => `<div class="instance-row"><div class="row-main"><div class="row-title">${esc(c.name)}</div><div class="row-sub">${esc(c.serverAddr)}:${c.serverPort} · ${(c.proxies || []).length} 条隧道</div></div><div class="actions">${statusSlot(c.id)}${command('编辑', 'edit', c.id, 'small')}${command('启动', 'start', c.id, 'small')}${command('停止', 'stop', c.id, 'small')}</div></div>`).join('')}</section>`;
}
function configs() {
  if (!state.configs.length) {
    $('#content').innerHTML = empty('还没有配置', '导入一个 TOML 配置，之后可在这里编辑内容、启动实例和查看状态。', true);
    return;
  }
  const c = state.configs.find(x => x.id === state.selected) || state.configs[0];
  state.selected = c.id;
  $('#content').innerHTML = `<div class="config-layout"><section class="panel config-list"><div class="panel-heading">全部配置<span>${state.configs.length}</span></div><div class="list-items">${state.configs.map(x =>
    `<button class="config-item ${x.id === c.id ? 'selected' : ''}" data-command="edit" data-id="${esc(x.id)}" aria-pressed="${x.id === c.id}"><div class="row-main"><div class="row-title">${esc(x.name)}</div><div class="row-sub">${esc(x.serverAddr)}:${x.serverPort}</div></div>${statusSlot(x.id)}</button>`).join('')}</div></section>${configEditor(c)}</div>`;
}
function configEditor(c) {
  const draft = state.drafts.get(c.id);
  return `<section class="panel config-editor"><div class="editor-heading"><div><h2>${esc(c.name)}</h2><div class="row-sub">${esc(c.filename)} · ${(c.proxies || []).length} 条隧道</div></div>${statusSlot(c.id)}</div>
    <div class="actions">${command('启动', 'start', c.id, 'primary')}${command('停止', 'stop', c.id)}${command('重载配置', 'reload', c.id)}${command('删除', 'delete', c.id, 'danger')}</div>
    <div class="field"><label for="edit-name">配置名称</label><input id="edit-name" value="${esc(draft?.name ?? c.name)}"></div>
    <div class="field source-field"><div class="split"><label for="edit-raw">TOML 配置</label><span id="draft-status" class="draft-status ${draft ? 'unsaved' : ''}">${draft ? '未保存' : '已保存'}</span></div><textarea id="edit-raw" class="editor" spellcheck="false">${esc(draft?.raw ?? c.raw)}</textarea></div>
    <div class="editor-footer"><label class="check-label"><input id="config-autostart" type="checkbox" ${c.autoStart ? 'checked' : ''}>随应用启动</label><div class="actions">${command('校验已保存配置', 'validate', c.id)}${command('保存配置', 'save', c.id, 'primary')}</div></div></section>`;
}
function options(selected, all = false) {
  return `${all ? '<option value="">全部配置</option>' : ''}${state.configs.map(c => `<option value="${esc(c.id)}" ${c.id === selected ? 'selected' : ''}>${esc(c.name)}</option>`).join('')}`;
}
function addressInfo(c, p) {
  const type = (p.type || '').toLowerCase();
  const local = `${p.localIP || '127.0.0.1'}:${p.localPort || '-'}`;
  let remote = '';
  let url = '';
  if (['tcp', 'udp'].includes(type) && p.remotePort) remote = `${c.serverAddr}:${p.remotePort}`;
  if (['http', 'https'].includes(type)) {
    const domain = p.customDomains?.[0] || (p.subdomain ? `${p.subdomain}.${c.serverAddr}` : '');
    if (domain) {
      try {
        const candidate = new URL(`${type}://${domain}/`);
        if (candidate.protocol === type + ':' && !candidate.username && !candidate.password) url = candidate.href;
      } catch {}
    }
    remote = url;
  }
  return { local, remote, url };
}
function addressValue(value, url = '') {
  if (!value) return '<span class="muted">未配置</span>';
  return url
    ? `<a class="address-value" href="${esc(url)}" target="_blank" rel="noopener noreferrer" title="打开网址">${esc(value)} ↗</a>`
    : `<button class="address-value" data-command="copy" data-value="${esc(value)}" title="点击复制">${esc(value)}</button>`;
}
function addresses() {
  if (!state.configs.some(x => x.id === state.addressConfig)) state.addressConfig = '';
  const rows = state.configs.filter(c => !state.addressConfig || c.id === state.addressConfig).flatMap(c => (c.proxies || []).map(p => ({ c, p })));
  $('#content').innerHTML = `${state.configs.length ? `<div class="toolbar"><div class="filter"><label for="address-config">配置</label><select id="address-config">${options(state.addressConfig, true)}</select></div><p class="muted">${rows.length} 条地址 · 地址可复制，网址可打开</p></div>` : ''}
    ${rows.length ? `<section class="panel"><div class="table-header"><span>隧道</span><span>本地地址</span><span>穿透地址</span></div>${rows.map(({ c, p }) => {
      const info = addressInfo(c, p);
      return `<div class="address-row"><div class="row-main"><div class="row-title">${esc(p.name || '未命名隧道')}</div><div class="row-sub">${esc(c.name)} · ${esc((p.type || '').toUpperCase())}</div></div><div class="address-cell"><small>本地地址</small>${addressValue(info.local)}</div><div class="address-cell"><small>穿透地址</small>${addressValue(info.remote, info.url)}</div></div>`;
    }).join('')}</section>` : empty('暂无穿透地址', state.configs.length ? '当前配置还没有可展示的隧道，请在 TOML 中添加代理配置。' : '导入配置后，本地地址和穿透地址会分列显示在这里。', !state.configs.length)}`;
}
function logs() {
  if (!state.configs.length) {
    $('#content').innerHTML = empty('暂无运行日志', '导入并启动配置后，可以在这里查看和复制日志。', true);
    return;
  }
  if (!state.configs.some(x => x.id === state.logConfig)) state.logConfig = state.configs[0].id;
  $('#content').innerHTML = `<div class="toolbar"><div class="filter"><label for="log-config">配置</label><select id="log-config">${options(state.logConfig)}</select></div><div class="actions">${statusSlot(state.logConfig)}${command('复制日志', 'copy-logs', state.logConfig)}</div></div><section class="panel log-panel"><pre id="log-output" class="log" tabindex="0" aria-label="实例运行日志">读取中…</pre></section>`;
  refreshLogs().catch(e => { if ($('#log-output')) $('#log-output').textContent = e.message; });
}
function formatLogs(lines) {
  return lines.map(x => `${new Date(x.at).toLocaleString()}  ${String(x.line).replace(/\x1b\[[0-?]*[ -/]*[@-~]/g, '')}`).join('\n');
}
async function refreshLogs() {
  const id = state.logConfig;
  if (!id || state.page !== 'logs') return;
  const lines = (await api('/api/instances/' + id + '/logs')) || [];
  const node = $('#log-output');
  if (!node || state.page !== 'logs' || state.logConfig !== id) return;
  const text = formatLogs(lines) || '暂无输出，启动实例后日志会显示在这里。';
  if (node.textContent === text || !window.getSelection().isCollapsed) return;
  const atEnd = node.scrollHeight - node.scrollTop - node.clientHeight < 24;
  node.textContent = text;
  if (atEnd) node.scrollTop = node.scrollHeight;
}
async function settings() {
  const version = renderVersion;
  $('#content').innerHTML = '<p class="muted">正在读取设置…</p>';
  try {
    const [d, boot] = await Promise.all([api('/api/diagnostics'), api('/api/settings/fnos-autostart')]);
    if (version !== renderVersion) return;
    $('#content').innerHTML = `<div class="stack settings"><section class="panel"><div class="panel-heading"><h2>运行环境</h2></div>
      <div class="setting-row"><div class="row-main"><div class="row-title">后台服务</div><div class="row-sub">关闭页面后，实例继续运行。</div></div>${status(d.core.ok ? 'running' : 'failed')}</div>
      <div class="setting-row"><div class="row-main"><div class="row-title">内置 frpc</div><div class="row-sub">${esc(d.frpc.path || '未找到')}</div></div><span class="pill ${d.frpc.available ? 'running' : 'failed'}">${d.frpc.available ? '可用' : '不可用'}</span></div></section>
      <section class="panel"><div class="panel-heading"><h2>启动设置</h2></div><div class="setting-row"><div class="row-main"><div class="row-title">开机自启</div><div class="row-sub">fnOS 启动时读取此开关，恢复已勾选“随应用启动”的配置。</div></div><div class="actions"><span class="pill ${boot.enabled ? 'running' : ''}">${boot.enabled ? '已开启' : '已关闭'}</span>${command('开启', 'service-on')}${command('关闭', 'service-off')}</div></div></section>
      <section class="panel"><div class="panel-heading"><h2>应用信息</h2></div><div class="setting-row"><div class="row-main"><div class="row-title">访问方式</div><div class="row-sub">飞牛统一网关</div></div></div><div class="setting-row"><div class="row-main"><div class="row-title">数据目录</div><div class="row-sub">${esc(d.dataDir)}</div></div></div></section></div>`;
  } catch (e) {
    if (version === renderVersion) $('#content').innerHTML = `<p class="notice">读取设置失败：${esc(e.message)}</p>`;
  }
}
function newConfig() {
  $('#import-error').classList.add('hidden');
  $('#import-dialog').showModal();
}
async function importConfig(event) {
  event.preventDefault();
  const button = $('#import-submit');
  button.disabled = true;
  $('#import-error').classList.add('hidden');
  try {
    const c = await api('/api/configs', { method: 'POST', body: JSON.stringify({ filename: $('#import-filename').value.trim(), content: $('#import-content').value }) });
    state.selected = c.id;
    state.page = 'configs';
    $('#import-dialog').close();
    $('#import-form').reset();
    await load();
    toast('配置已导入');
  } catch (e) {
    $('#import-error').textContent = e.message;
    $('#import-error').classList.remove('hidden');
  } finally { button.disabled = false; }
}
async function action(id, name) {
  try {
    await api('/api/instances/' + id + '/' + name, { method: 'POST' });
    await load();
    toast(name === 'reload' ? '已重载保存的配置' : '操作完成');
  } catch (e) { toast(e.message); }
}
async function saveConfig(id) {
  try {
    await api('/api/configs/' + id, { method: 'PUT', body: JSON.stringify({ name: $('#edit-name').value, content: $('#edit-raw').value }) });
    state.drafts.delete(id);
    await load();
    toast('配置已保存');
  } catch (e) { toast(e.message); }
}
async function validateConfig(id) {
  try {
    const result = await api('/api/configs/' + id + '/validate', { method: 'POST' });
    toast(result.length ? result.join('；') : '配置校验通过');
  } catch (e) { toast(e.message); }
}
async function toggleAuto(id, value) {
  try {
    await api('/api/configs/' + id + '/autostart', { method: 'PUT', body: JSON.stringify({ autoStart: value }) });
    await load();
    toast(value ? '配置将随应用启动' : '已关闭配置自启');
  } catch (e) { toast(e.message); await load(); }
}
async function toggleService(value) {
  try {
    await api('/api/settings/fnos-autostart', { method: 'PUT', body: JSON.stringify({ enabled: value }) });
    toast(value ? '已开启开机自启' : '已关闭开机自启');
    await settings();
  } catch (e) { toast(e.message); }
}
async function removeConfig(id) {
  if (!confirm('确定删除这个配置吗？')) return;
  try {
    await api('/api/configs/' + id, { method: 'DELETE' });
    state.drafts.delete(id);
    state.selected = null;
    await load();
    toast('已删除');
  } catch (e) { toast(e.message); }
}
async function copyLogs(id) {
  try {
    const lines = (await api('/api/instances/' + id + '/logs')) || [];
    if (!lines.length) { toast('暂无可复制的日志'); return; }
    await copy(formatLogs(lines));
  } catch (e) { toast(e.message); }
}
document.addEventListener('click', event => {
  const button = event.target.closest('[data-command]');
  if (!button || button.disabled) return;
  const id = button.dataset.id;
  switch (button.dataset.command) {
    case 'import': newConfig(); break;
    case 'close-import': $('#import-dialog').close(); break;
    case 'edit': state.selected = id; state.page = 'configs'; render(); break;
    case 'start': case 'stop': case 'reload': action(id, button.dataset.command); break;
    case 'save': saveConfig(id); break;
    case 'validate': validateConfig(id); break;
    case 'delete': removeConfig(id); break;
    case 'copy': copy(button.dataset.value); break;
    case 'copy-logs': copyLogs(id); break;
    case 'service-on': toggleService(true); break;
    case 'service-off': toggleService(false); break;
  }
});
document.addEventListener('change', event => {
  if (event.target.id === 'address-config') { state.addressConfig = event.target.value; addresses(); }
  if (event.target.id === 'log-config') { state.logConfig = event.target.value; logs(); }
  if (event.target.id === 'config-autostart') toggleAuto(state.selected, event.target.checked);
});
document.addEventListener('input', event => {
  if (!['edit-name', 'edit-raw'].includes(event.target.id)) return;
  const draft = { name: $('#edit-name').value, raw: $('#edit-raw').value };
  const c = state.configs.find(x => x.id === state.selected);
  const dirty = draft.name !== c.name || draft.raw !== c.raw;
  if (dirty) state.drafts.set(c.id, draft); else state.drafts.delete(c.id);
  $('#draft-status').textContent = dirty ? '未保存' : '已保存';
  $('#draft-status').classList.toggle('unsaved', dirty);
});
document.querySelectorAll('#nav button').forEach(node => {
  node.onclick = () => { state.page = node.dataset.page; render(); $('#content').scrollTop = 0; };
});
$('#import-form').addEventListener('submit', importConfig);
window.addEventListener('beforeunload', event => {
  if (state.drafts.size) { event.preventDefault(); event.returnValue = ''; }
});
load().catch(e => {
  connection(false);
  $('#content').innerHTML = `<p class="notice">无法连接 FrpDesk：${esc(e.message)}</p>`;
});
setInterval(() => load(true).catch(() => connection(false)), 5000);
