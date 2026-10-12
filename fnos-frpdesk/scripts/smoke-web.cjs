const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright');

const base = 'http://127.0.0.1:47992/app/frpdesk';
const output = path.join(__dirname, '../dist/ui-review');
const raw = `serverAddr = "127.0.0.1"
serverPort = 1

[[proxies]]
name = "远程桌面 RDP"
type = "tcp"
localIP = "127.0.0.1"
localPort = 3389
remotePort = 33389

[[proxies]]
name = "管理页面"
type = "https"
localIP = "192.168.1.100"
localPort = 8080
customDomains = ["preview.example.com"]
`;
const created = [];

(async () => {
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({ headless: true });
  const context = await browser.newContext({ viewport: { width: 1280, height: 800 }, permissions: ['clipboard-read', 'clipboard-write'] });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  const nav = name => page.locator('#nav').getByRole('button', { name, exact: true });
  const screenshot = name => page.screenshot({ path: path.join(output, name + '.png') });
  const checkLayout = async () => {
    const result = await page.evaluate(() => {
      const content = document.querySelector('#content');
      const header = document.querySelector('.page-header').getBoundingClientRect();
      const child = content.firstElementChild.getBoundingClientRect();
      return {
        gap: child.top - header.bottom,
        overflow: document.documentElement.scrollWidth - innerWidth,
        childRight: child.right, width: innerWidth
      };
    });
    assert.ok(result.gap >= 14 && result.gap <= 26, JSON.stringify(result));
    assert.ok(result.overflow <= 1 && result.childRight <= result.width + 1, JSON.stringify(result));
  };
  try {
    await page.goto(base);
    await page.getByRole('heading', { name: '添加第一个配置' }).waitFor();
    await checkLayout();
    await screenshot('01-empty-desktop');
    await page.getByRole('button', { name: '导入 TOML', exact: true }).click();
    await page.getByLabel('TOML 内容').fill('invalid = [');
    await page.getByRole('button', { name: '导入配置', exact: true }).click();
    await page.locator('#import-error:not(.hidden)').waitFor();
    assert.ok((await page.locator('#import-error').innerText()).includes('解析失败'));
    await page.getByLabel('文件名', { exact: true }).fill('家庭远程桌面.toml');
    await page.getByLabel('TOML 内容', { exact: true }).fill(raw);
    await screenshot('02-import-desktop');
    await page.getByRole('button', { name: '导入配置', exact: true }).click();
    await page.locator('#edit-raw').waitFor();
    created.push(...(await (await context.request.get(base + '/api/configs')).json()).map(c => c.id));
    assert.equal(await page.locator('#edit-raw').inputValue(), raw);
    await checkLayout();
    await screenshot('03-config-desktop');

    await page.getByRole('button', { name: '导入 TOML', exact: true }).click();
    await page.getByLabel('文件名', { exact: true }).fill('备用配置-包含较长的中文名称与空格.toml');
    await page.getByLabel('TOML 内容', { exact: true }).fill(raw.replace('33389', '43389'));
    await page.getByRole('button', { name: '导入配置', exact: true }).click();
    await page.locator('.config-item').nth(1).waitFor();
    const all = await (await context.request.get(base + '/api/configs')).json();
    created.splice(0, created.length, ...all.map(c => c.id));
    await page.locator('#edit-name').fill('中文草稿与未保存内容');
    const draft = raw + '\n# 编辑期间等待自动刷新\n';
    await page.locator('#edit-raw').fill(draft);
    await page.waitForTimeout(5500);
    assert.equal(await page.locator('#edit-raw').inputValue(), draft);
    assert.equal(await page.locator('#edit-name').inputValue(), '中文草稿与未保存内容');
    await nav('运行日志').click();
    await nav('配置实例').click();
    assert.equal(await page.locator('#edit-raw').inputValue(), draft);
    await page.getByRole('button', { name: '保存配置', exact: true }).click();
    await page.locator('#draft-status').filter({ hasText: '已保存' }).waitFor();

    await nav('穿透地址').click();
    await page.locator('.address-row').nth(3).waitFor();
    await checkLayout();
    await page.getByLabel('配置', { exact: true }).selectOption(created[0]);
    assert.equal(await page.locator('.address-row').count(), 2);
    await page.getByRole('button', { name: '127.0.0.1:3389', exact: true }).click();
    assert.equal(await page.evaluate(() => navigator.clipboard.readText()), '127.0.0.1:3389');
    await screenshot('04-addresses-desktop');
    await context.route('https://preview.example.com/**', route => route.fulfill({ body: '<h1>Local navigation check</h1>', contentType: 'text/html' }));
    const popupPromise = page.waitForEvent('popup');
    await page.getByRole('link', { name: 'https://preview.example.com/ ↗', exact: true }).click();
    const popup = await popupPromise;
    await popup.waitForLoadState();
    assert.equal(popup.url(), 'https://preview.example.com/');
    await popup.close();

    await nav('配置实例').click();
    await page.getByRole('button', { name: '启动', exact: true }).click();
    await page.waitForTimeout(1200);
    await nav('运行日志').click();
    await page.locator('#log-config').selectOption(created[1]);
    await page.locator('#log-output').filter({ hasText: /frpc|connect|error|启动/i }).waitFor();
    await page.getByRole('button', { name: '复制日志', exact: true }).click();
    assert.ok((await page.evaluate(() => navigator.clipboard.readText())).length > 10);
    await screenshot('05-logs-desktop');

    for (const [width, height] of [[1600, 1000], [900, 650], [720, 650], [390, 844], [320, 700]]) {
      await page.setViewportSize({ width, height });
      for (const name of ['概览', '配置实例', '穿透地址', '运行日志', '设置']) {
        await nav(name).click();
        await page.waitForTimeout(80);
        await checkLayout();
        if (width === 390 || width === 900) await screenshot(`layout-${width}-${name}`);
      }
    }
    assert.deepEqual(errors, []);
    console.log('UI smoke passed: import/error, save, draft retention, filtering, copy, link navigation, logs, five pages and six viewport sizes.');
    console.log('Screenshots: ' + output);
  } finally {
    for (const id of created) await context.request.delete(base + '/api/configs/' + id);
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
