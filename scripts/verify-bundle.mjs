import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import https from 'node:https';
import path from 'node:path';
import { inflateRawSync } from 'node:zlib';

// 在独立文件目录启动本机服务，只检查原生资源与配置 RPC，不发送对话或模型请求。
const [server, bundle, stateDirectory] = process.argv.slice(2).map(p => path.resolve(p));
assert(server && bundle && stateDirectory, '参数：language_server.exe 中文ZIP 独立状态目录');
fs.mkdirSync(stateDirectory, { recursive: true });
const zip = fs.readFileSync(bundle);
const bundleHash = createHash('sha256').update(zip).digest('hex');
const expanded = path.resolve(path.dirname(server), '..', 'antigravity-chinese', 'web-' + bundleHash);
assert(fs.statSync(expanded).isDirectory(), '请先在验证副本中应用补丁，再验证安装后的资源目录');
function readEntries() {
  const entries = new Map();
  const end = zip.lastIndexOf(Buffer.from('PK\x05\x06', 'latin1'));
  for (let p = zip.readUInt32LE(end + 16); p < end;) {
    assert.equal(zip.readUInt32LE(p), 0x02014b50);
    const n = zip.readUInt16LE(p + 28), x = zip.readUInt16LE(p + 30), c = zip.readUInt16LE(p + 32);
    const name = zip.toString('utf8', p + 46, p + 46 + n);
    if (!name.endsWith('/')) {
      const local = zip.readUInt32LE(p + 42), size = zip.readUInt32LE(p + 20);
      const start = local + 30 + zip.readUInt16LE(local + 26) + zip.readUInt16LE(local + 28);
      const bytes = zip.subarray(start, start + size);
      entries.set(name, zip.readUInt16LE(p + 10) === 8 ? inflateRawSync(bytes) : bytes);
    }
    p += 46 + n + x + c;
  }
  return entries;
}
const entries = readEntries();
const agent = new https.Agent({ keepAlive: true, maxSockets: 1 });
function request(port, url, method = 'GET', body) {
  return new Promise((resolve, reject) => {
    const req = https.request({ hostname: '127.0.0.1', port, path: url, method, agent, rejectUnauthorized: false, timeout: 15000,
      headers: { 'X-Codeium-Csrf-Token': 'native-bundle-check', 'Content-Type': 'application/json', 'Connect-Protocol-Version': '1' } }, res => {
      const chunks = []; res.on('data', c => chunks.push(c)); res.on('end', () => resolve({
        status: res.statusCode, bytes: Buffer.concat(chunks), contentType: res.headers['content-type']?.split(';')[0]
      }));
    });
    req.on('error', reject); req.on('timeout', () => req.destroy(new Error('本机请求超时：' + url))); req.end(body);
  });
}
const child = spawn(server, ['--standalone', '--subclient_type', 'hub', '--override_ide_name', 'antigravity',
  '--override_ide_version', '2.24.0', '--https_server_port', '0', '--csrf_token', 'native-bundle-check',
  '--app_data_dir', 'antigravity', '--web_bundle_path', expanded, '--exit_on_stdin_close'], {
  cwd: stateDirectory, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'],
  env: { ...process.env, USERPROFILE: stateDirectory, HOME: stateDirectory, APPDATA: stateDirectory,
    LOCALAPPDATA: stateDirectory, ANTIGRAVITY_VSCODE_HOST: '1' }
});
let logs = '';
try {
  const port = await new Promise((resolve, reject) => {
    const timeout = setTimeout(() => reject(new Error('本机服务启动超时：' + logs.slice(-500))), 30000);
    const inspect = data => {
      logs += data.toString();
      const match = logs.match(/listening on \w+ port at (\d+) for HTTPS\b/i);
      if (match) { clearTimeout(timeout); resolve(Number(match[1])); }
    };
    child.stdout.on('data', inspect); child.stderr.on('data', inspect);
    child.once('error', reject); child.once('exit', code => { clearTimeout(timeout); reject(new Error(`服务提前退出 ${code}：${logs.slice(-500)}`)); });
  });
  const root = await request(port, '/'); assert.equal(root.status, 200);
  const entryUrl = root.bytes.toString().match(/\bsrc=["']([^"']*main\.js\?[^"']*antigravity_chinese=[^"']*)["']/)?.[1];
  assert(entryUrl, '中文入口没有独立缓存标识');
  assert(entries.get('index.html').toString().includes(entryUrl), '入口没有使用生成的脚本地址');
  const main = await request(port, entryUrl.startsWith('/') ? entryUrl : '/' + entryUrl); assert.equal(main.status, 200);
  assert(main.bytes.equals(entries.get('main.js')), '实际服务未返回生成的中文脚本');
  assert(main.bytes.includes(Buffer.from('新建对话')) && main.bytes.includes(Buffer.from('复制')), '缺少实际中文文案');
  // HTTP 200 也可能是首页回退；逐文件检查正文，覆盖任意深度的图标、图片和 worker。
  const resourceTypes = {};
  for (const [name, expected] of entries) {
    // 首页由服务注入启动配置，单独验证脚本引用；其余资源必须字节一致。
    if (name === 'index.html') continue;
    const response = name === 'main.js' ? main : await request(port, '/' + name.split('/').map(encodeURIComponent).join('/'));
    assert.equal(response.status, 200, `资源请求失败：${name}`);
    assert(response.bytes.equals(expected), `资源返回内容不匹配（可能被首页替代）：${name}`);
    const extension = path.extname(name).toLowerCase();
    const mime = { '.svg': 'image/svg+xml', '.png': 'image/png', '.jpg': 'image/jpeg', '.jpeg': 'image/jpeg',
      '.gif': 'image/gif', '.webp': 'image/webp', '.css': 'text/css' }[extension];
    if (mime) assert.equal(response.contentType, mime, `资源 MIME 不正确：${name}`);
    resourceTypes[extension] = (resourceTypes[extension] ?? 0) + 1;
  }
  const settings = await request(port, '/exa.language_server_pb.LanguageServerService/GetServerConfiguration', 'POST', '{}');
  assert.equal(settings.status, 200, '本机配置 RPC 未正常工作：' + settings.bytes.toString().slice(0, 300));
  console.log(JSON.stringify({ nativeBundle: 'passed', staticResources: entries.size - 1, resourceTypes, mainBytes: main.bytes.length,
    sha256: createHash('sha256').update(main.bytes).digest('hex'), configurationRpc: settings.status }, null, 2));
} finally {
  agent.destroy();
  child.stdin.end();
  await Promise.race([new Promise(resolve => child.once('exit', resolve)), new Promise(resolve => setTimeout(resolve, 3000))]);
  if (child.exitCode === null) child.kill();
}
