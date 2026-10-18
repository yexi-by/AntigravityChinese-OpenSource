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
function readEntry(name) {
  const end = zip.lastIndexOf(Buffer.from('PK\x05\x06', 'latin1'));
  for (let p = zip.readUInt32LE(end + 16); p < end;) {
    assert.equal(zip.readUInt32LE(p), 0x02014b50);
    const n = zip.readUInt16LE(p + 28), x = zip.readUInt16LE(p + 30), c = zip.readUInt16LE(p + 32);
    if (zip.toString('utf8', p + 46, p + 46 + n) === name) {
      const local = zip.readUInt32LE(p + 42), size = zip.readUInt32LE(p + 20);
      const start = local + 30 + zip.readUInt16LE(local + 26) + zip.readUInt16LE(local + 28);
      const bytes = zip.subarray(start, start + size);
      return zip.readUInt16LE(p + 10) === 8 ? inflateRawSync(bytes) : bytes;
    }
    p += 46 + n + x + c;
  }
  throw new Error(`ZIP 中缺少 ${name}`);
}
function request(port, url, method = 'GET', body) {
  return new Promise((resolve, reject) => {
    const req = https.request({ hostname: '127.0.0.1', port, path: url, method, rejectUnauthorized: false, timeout: 10000,
      headers: { 'X-Codeium-Csrf-Token': 'native-bundle-check', 'Content-Type': 'application/json', 'Connect-Protocol-Version': '1' } }, res => {
      const chunks = []; res.on('data', c => chunks.push(c)); res.on('end', () => resolve({ status: res.statusCode, bytes: Buffer.concat(chunks) }));
    });
    req.on('error', reject); req.on('timeout', () => req.destroy(new Error('本机请求超时'))); req.end(body);
  });
}
const child = spawn(server, ['--standalone', '--subclient_type', 'hub', '--override_ide_name', 'antigravity',
  '--override_ide_version', '2.24.0', '--https_server_port', '0', '--csrf_token', 'native-bundle-check',
  '--app_data_dir', 'antigravity', '--web_bundle_path', bundle, '--exit_on_stdin_close'], {
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
  assert(readEntry('index.html').toString().includes(entryUrl), '入口没有使用生成的脚本地址');
  const main = await request(port, entryUrl.startsWith('/') ? entryUrl : '/' + entryUrl); assert.equal(main.status, 200);
  assert.deepEqual(main.bytes, readEntry('main.js'), '实际服务未返回生成的中文脚本');
  assert(main.bytes.includes(Buffer.from('新建对话')) && main.bytes.includes(Buffer.from('复制')), '缺少实际中文文案');
  const css = await request(port, '/jetbox.css'); assert.equal(css.status, 200); assert.deepEqual(css.bytes, readEntry('jetbox.css'));
  const settings = await request(port, '/exa.language_server_pb.LanguageServerService/GetServerConfiguration', 'POST', '{}');
  assert.equal(settings.status, 200, '本机配置 RPC 未正常工作：' + settings.bytes.toString().slice(0, 300));
  console.log(JSON.stringify({ nativeBundle: 'passed', mainBytes: main.bytes.length,
    sha256: createHash('sha256').update(main.bytes).digest('hex'), configurationRpc: settings.status }, null, 2));
} finally {
  child.stdin.end();
  await Promise.race([new Promise(resolve => child.once('exit', resolve)), new Promise(resolve => setTimeout(resolve, 3000))]);
  if (child.exitCode === null) child.kill();
}
