'use strict';
// All destinations are synthetic names or loopback fixtures. No public probes.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const https = require('node:https');
const net = require('node:net');
const dns = require('node:dns');
const dgram = require('node:dgram');
const { spawn } = require('node:child_process');
const { once } = require('node:events');
const { ws: Ws, wsServer: WsServer } = require(process.env.PROXY_TEST_WS);
const certificates = process.env.PROXY_TEST_CERTS;
const pass = message => console.log('PASS ' + message);
const get = (module, url, options = {}) => new Promise((resolve, reject) => {
  const request = module.get(url, options, response => {
    let body = ''; response.on('data', chunk => body += chunk); response.on('end', () => resolve(body));
  });
  request.on('error', reject); request.setTimeout(3000, () => request.destroy(new Error('request timed out')));
});
const rejectCode = async (promise, message, code = 'ERR_CODEX_PROXY_REQUIRED') => {
  await assert.rejects(promise, error => error.code === code); pass(message);
};

async function child() {
  const port = Number(process.env.PROXY_TEST_HTTP);
  const tlsPort = Number(process.env.PROXY_TEST_HTTPS);
  assert.equal(process.env.NODE_USE_ENV_PROXY, '1');
  assert.equal(process.env.BROWSER_USE_AVAILABLE_BACKENDS, 'iab,mcpapps'); pass('filtered runtime restores proxy and only internal browser backends');
  assert.equal(await get(http, `http://fixture.invalid:${port}/plain`), '/plain'); pass('HTTP synthetic remote target via CONNECT');
  const custom = new http.Agent({ keepAlive: true });
  custom.createConnection = () => { throw new Error('direct agent executed'); };
  assert.equal(await get(http, `http://fixture.invalid:${port}/custom`, { agent: custom }), '/custom'); pass('custom HTTP agent cannot bypass CONNECT');
  assert.equal(await get(http, `http://fixture.invalid:${port}/agent-false`, { agent: false }), '/agent-false'); pass('agent=false cannot bypass CONNECT');
  const esmHttp = await import('node:http');
  assert.equal(await get(esmHttp, `http://fixture.invalid:${port}/esm`), '/esm'); pass('ESM HTTP bindings use proxy');
  assert.equal(await get(http, `http://127.0.0.1:${port}/local`), '/local'); pass('local HTTP IPC preserved');
  const pipeName = process.env.PROXY_TEST_PIPE;
  assert.equal(await get(http, { socketPath: pipeName, path: '/pipe' }), '/pipe'); pass('local Windows pipe IPC preserved');
  const ca = fs.readFileSync(path.join(certificates, 'cert.pem'));
  await assert.rejects(get(https, `https://fixture.invalid:${tlsPort}/untrusted`), /self-signed|certificate/); pass('TLS rejects untrusted certificate through proxy');
  assert.equal(await get(https, `https://fixture.invalid:${tlsPort}/trusted`, { ca }), '/trusted'); pass('TLS validates explicit trusted CA through CONNECT');
  await assert.rejects(get(https, `https://wrong.invalid:${tlsPort}/wrong`, { ca }), /Hostname|altname|altnames|certificate/); pass('TLS hostname validation remains enabled');
  const fetchResponse = await fetch(`http://fixture.invalid:${port}/fetch`);
  assert.equal(await fetchResponse.text(), '/fetch'); pass('native fetch uses proxy from restored startup environment');
  for (const [label, ctor, options] of [
    ['bundled ws', Ws, {}], ['bundled ws with custom agent', Ws, { agent: custom }], ['native WebSocket', WebSocket, undefined]
  ]) {
    const socket = options ? new ctor(`ws://fixture.invalid:${port}/ws`, options) : new ctor(`ws://fixture.invalid:${port}/ws`);
    await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error(label + ' timeout')), 4000);
      socket.addEventListener('open', () => socket.send('hello'));
      socket.addEventListener('message', event => { clearTimeout(timer); try { assert.equal(String(event.data), 'echo:hello'); socket.close(); resolve(); } catch (error) { reject(error); } });
      socket.addEventListener('error', event => { clearTimeout(timer); reject(event.error || new Error(label + ' error')); });
    });
    pass(label + ' through CONNECT');
  }
  const wss = new Ws(`wss://fixture.invalid:${tlsPort}/ws`, { ca });
  await once(wss, 'open'); wss.close(); pass('bundled WSS through TLS-validated CONNECT');
  await rejectCode(new Promise((resolve, reject) => { const socket = net.connect({ host: '198.51.100.7', port: 443 }); socket.on('connect', resolve); socket.on('error', reject); }), 'raw remote TCP denied before network');
  await rejectCode(dns.promises.lookup('fixture.invalid'), 'remote OS DNS lookup denied');
  await rejectCode(new dns.promises.Resolver().resolve4('fixture.invalid'), 'custom DNS resolver denied');
  await rejectCode(new Promise((resolve, reject) => dns.resolve4('localhost', error => error ? reject(error) : resolve())), 'explicit localhost DNS query denied');
  assert.ok(await dns.promises.lookup('localhost')); pass('OS loopback lookup preserved');
  const udp = dgram.createSocket('udp4');
  await rejectCode(new Promise((resolve, reject) => udp.send('x', 53, '198.51.100.7', error => error ? reject(error) : resolve())), 'unconnected remote UDP denied');
  await new Promise(resolve => udp.bind(0, '127.0.0.1', resolve));
  await new Promise(resolve => udp.connect(udp.address().port, '127.0.0.1', resolve));
  udp.disconnect();
  await rejectCode(new Promise((resolve, reject) => udp.send('x', 53, '198.51.100.7', error => error ? reject(error) : resolve())), 'UDP disconnect cannot remove direct-network restriction');
  udp.close();
  await assert.rejects(get(http, 'http://rejected.invalid:80/reject'), /rejected/); pass('CONNECT rejection fails without direct fallback');
  await assert.rejects(get(http, 'http://closed.invalid:80/close'), /closed/); pass('CONNECT premature close fails immediately');
  assert.throws(() => http.get({socketPath: '\\\\elsewhere\\pipe\\test', path:'/'}), /direct remote pipe/); pass('remote named pipe denied');
  process.exit(0);
}

async function parent() {
  const build = path.resolve(process.argv[2]);
  const records = [], sockets = new Set();
  const track = socket => { sockets.add(socket); socket.on('error', () => {}); socket.once('close', () => sockets.delete(socket)); };
  const handler = (req, res) => { res.writeHead(200); res.end(req.url); };
  const origin = http.createServer(handler);
  const secure = https.createServer({ cert: fs.readFileSync(path.join(certificates, 'cert.pem')), key: fs.readFileSync(path.join(certificates, 'key.pem')) }, handler);
  const pipe = http.createServer(handler);
  const pipeName = '\\\\.\\pipe\\codex-proxy-fixture-' + process.pid;
  for (const server of [origin, secure, pipe]) { server.on('connection', track); new WsServer({ server }).on('connection', socket => socket.on('message', data => socket.send('echo:' + data))); }
  origin.listen(0, '127.0.0.1'); secure.listen(0, '127.0.0.1'); pipe.listen(pipeName);
  await Promise.all([once(origin, 'listening'), once(secure, 'listening'), once(pipe, 'listening')]);
  const proxy = http.createServer((req, res) => {
    // Node's native undici environment proxy can use absolute-form HTTP.
    const url = new URL(req.url); records.push({ method: req.method, authority: url.host });
    if (url.hostname !== 'fixture.invalid') { res.writeHead(502); res.end(); return; }
    const upstream = http.request({ host:'127.0.0.1', port:origin.address().port, path:url.pathname, method:req.method, headers:req.headers }, reply => { res.writeHead(reply.statusCode, reply.headers); reply.pipe(res); });
    upstream.on('error', () => res.destroy()); req.pipe(upstream);
  });
  proxy.on('connection', track);
  proxy.on('connect', (req, client, head) => {
    records.push({ method:'CONNECT', authority:req.url });
    if (req.url.startsWith('rejected.invalid:')) { client.end('HTTP/1.1 403 Rejected\r\n\r\n'); return; }
    if (req.url.startsWith('closed.invalid:')) { client.destroy(); return; }
    const [hostname, destination] = req.url.split(':');
    if (!['fixture.invalid','wrong.invalid'].includes(hostname) || ![origin.address().port, secure.address().port].includes(Number(destination))) { client.end('HTTP/1.1 502 Invalid fixture\r\n\r\n'); return; }
    const upstream = net.connect({ host:'127.0.0.1', port:Number(destination) }); track(upstream);
    upstream.on('connect', () => { client.write('HTTP/1.1 200 Connection established\r\n\r\n'); if (head.length) upstream.write(head); client.pipe(upstream); upstream.pipe(client); });
    client.on('close', () => upstream.destroy()); upstream.on('error', () => client.destroy());
  });
  proxy.listen(0, '127.0.0.1'); await once(proxy, 'listening');
  fs.writeFileSync(path.join(build, 'control-proxy.port'), String(proxy.address().port));
  const env = { ...process.env, PROXY_TEST_CHILD:'1', PROXY_TEST_HTTP:String(origin.address().port), PROXY_TEST_HTTPS:String(secure.address().port), PROXY_TEST_PIPE:pipeName, BROWSER_USE_AVAILABLE_BACKENDS:'chrome,edge,iab' };
  for (const key of Object.keys(env)) if (/^(https?_proxy|all_proxy|no_proxy|node_use_env_proxy|node_options|node_extra_ca_certs)$/i.test(key)) delete env[key];
  // The manifest is a test fixture, never the release or active launcher manifest.
  const manifest = {PackageExecutable:process.execPath, Node:process.execPath, NodeRepl:path.join(path.dirname(process.execPath),'node_repl.exe'),Codex:process.env.PROXY_TEST_CODEX};
  fs.writeFileSync(path.join(build, 'proxy-runtime.json'), JSON.stringify(manifest));
  const childProcess = spawn(path.join(build, 'ChatGPT-Verge-NodeProxy.exe'), [__filename], { env, stdio:['pipe','pipe','pipe'] });
  childProcess.stdout.pipe(process.stdout); childProcess.stderr.pipe(process.stderr); childProcess.stdin.end();
  const timeout = setTimeout(() => childProcess.kill(), 25000);
  const [code] = await once(childProcess, 'exit'); clearTimeout(timeout);
  const expectedAuthorities = [`fixture.invalid:${origin.address().port}`, `wrong.invalid:${secure.address().port}`, 'rejected.invalid:80', 'closed.invalid:80'];
  for (const socket of sockets) socket.destroy();
  for (const server of [origin, secure, pipe, proxy]) server.close();
  fs.writeFileSync(path.join(build, 'proxy-test-connects.json'), JSON.stringify(records, null, 2));
  assert.equal(code, 0, 'proxy child exit');
  // Keep-alive intentionally reuses tunnels across HTTP and upgrade requests.
  for (const authority of expectedAuthorities) assert.ok(records.some(record => record.method === 'CONNECT' && record.authority === authority));
  pass('fixture proxy observed transport requests; no external test traffic');
  process.exit(0);
}
(process.env.PROXY_TEST_CHILD ? child() : parent()).catch(error => { console.error(error); process.exit(1); });
