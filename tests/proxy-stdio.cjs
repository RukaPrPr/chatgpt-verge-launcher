'use strict';
const assert = require('node:assert/strict');
const {spawn} = require('node:child_process');
const {once} = require('node:events');
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(process.argv[2]);
const nodeProxy = path.join(root, 'ChatGPT-Verge-NodeProxy.exe');
const controlProxy = path.join(root, 'ChatGPT-Verge-ControlProxy.exe');
const cliProxy = path.join(root, 'ChatGPT-Verge-CodexProxy.exe');
const results = [];
const coldStarts = [];
const processes = [];
const env = {...process.env};
for (const key of Object.keys(env)) {
  if (/^(NODE_REPL_|CUA_REPL_)/i.test(key) || /^NODE_OPTIONS$/i.test(key)) delete env[key];
}
env.NODE_REPL_NODE_PATH = nodeProxy;
env.NODE_REPL_JS_BANNER = '';

async function bounded(promise, ms, label) {
  let timer;
  try {
    return await Promise.race([promise, new Promise((_, reject) => {
      timer = setTimeout(() => reject(new Error(label + ' exceeded ' + ms + ' ms')), ms);
    })]);
  } finally { clearTimeout(timer); }
}
function check(condition, label) {
  assert.ok(condition, label);
  results.push({label, passed: true});
  console.log('PASS ' + label);
}
function start(executable, args) {
  const child = spawn(executable, args, {env, cwd: root, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe']});
  const entry = {child, closed: false, stdout: [], stderr: [], started: Date.now()};
  processes.push(entry);
  child.stdout.on('data', bytes => entry.stdout.push(bytes));
  child.stderr.on('data', bytes => entry.stderr.push(bytes));
  // Early-exiting commands intentionally leave the parent's input pipe open.
  child.stdin.on('error', error => { if (error.code !== 'EPIPE' && error.code !== 'ECONNRESET') entry.inputError = error; });
  entry.close = new Promise((resolve, reject) => {
    child.once('error', reject);
    child.once('close', (code, signal) => { entry.closed = true; resolve({code, signal}); });
  });
  // Install a handler immediately even when an individual test has not awaited it yet.
  entry.close.catch(() => {});
  return entry;
}
async function finish(entry, timeout = 4000) {
  const status = await bounded(entry.close, timeout, path.basename(entry.child.spawnfile));
  if (entry.inputError) throw entry.inputError;
  return {...status, stdout: Buffer.concat(entry.stdout), stderr: Buffer.concat(entry.stderr), elapsedMs: Date.now() - entry.started};
}
function mcp(entry) {
  let sequence = 0, buffer = '';
  const pending = new Map();
  const protocolErrors = [];
  entry.child.stdout.setEncoding('utf8');
  // The general collector must continue receiving bytes for the same process.
  entry.child.stdout.removeAllListeners('data');
  entry.child.stdout.on('data', chunk => {
    entry.stdout.push(Buffer.from(chunk));
    buffer += chunk;
    for (;;) {
      const newline = buffer.indexOf('\n');
      if (newline < 0) break;
      const line = buffer.slice(0, newline); buffer = buffer.slice(newline + 1);
      if (!line.trim()) continue;
      let message;
      try { message = JSON.parse(line); }
      catch (error) { protocolErrors.push(line); continue; }
      const request = pending.get(message.id);
      if (request) {
        pending.delete(message.id);
        if (message.error) request.reject(new Error(JSON.stringify(message.error)));
        else request.resolve(message.result);
      }
    }
  });
  entry.close.then(() => {
    for (const request of pending.values()) request.reject(new Error('MCP exited before its reply'));
    pending.clear();
  }, error => { for (const request of pending.values()) request.reject(error); pending.clear(); });
  function send(message) { entry.child.stdin.write(JSON.stringify({jsonrpc: '2.0', ...message}) + '\n'); }
  return {
    protocolErrors,
    notify(method, params) { send({method, params}); },
    async request(method, params) {
      const id = ++sequence;
      try {
        return await bounded(new Promise((resolve, reject) => {
          pending.set(id, {resolve, reject}); send({id, method, params});
        }), 8000, method);
      } finally { pending.delete(id); }
    }
  };
}
function output(result) {
  assert.ok(!result.isError, JSON.stringify(result));
  return (result.content || []).filter(item => item.type === 'text').map(item => item.text).join('\n');
}
async function main() {
  for (const [executable, args, expected] of [
    [nodeProxy, ['--version'], /^v\d+\./],
    [nodeProxy, ['-e', 'process.stdout.write("short");process.exitCode=37'], /^short$/],
    [cliProxy, ['--version'], /codex-cli/],
    [controlProxy, ['--help'], /Usage|usage/]
  ]) {
    const entry = start(executable, args);
    const result = await finish(entry);
    check(result.code === (args[0] === '-e' ? 37 : 0) && expected.test(result.stdout.toString()),
      path.basename(executable) + ' ' + args[0] + ' exits with stdin left open');
  }

  const echo = start(nodeProxy, ['-e', 'process.stdin.on("data", b => process.stdout.write(b));process.stdin.on("end",()=>process.stderr.write("eof"))']);
  const line = Buffer.from('{"message":"中文 héllo"}\n');
  const first = once(echo.child.stdout, 'data');
  echo.child.stdin.write(line);
  await bounded(first, 3000, 'interactive input');
  check(Buffer.concat(echo.stdout).equals(line) && !echo.closed, 'Node proxy flushes interactive UTF-8 before EOF');
  const binary = Buffer.alloc(2 * 1024 * 1024 + 17);
  for (let i = 0; i < binary.length; i++) binary[i] = i % 256;
  echo.child.stdin.end(binary);
  const echoed = await finish(echo);
  check(echoed.code === 0 && echoed.stdout.equals(Buffer.concat([line, binary])) && echoed.stderr.toString() === 'eof',
    'inherited input preserves binary bytes, EOF and final output');

  const largeOutput = start(nodeProxy, ['-e', 'const fs=require("fs");for(const fd of [1,2]){const b=Buffer.alloc(2*1024*1024+19,fd);let n=0;while(n<b.length)n+=fs.writeSync(fd,b,n,b.length-n)}process.exitCode=23']);
  const drained = await finish(largeOutput);
  check(drained.code === 23 && drained.stdout.equals(Buffer.alloc(2 * 1024 * 1024 + 19, 1)) && drained.stderr.equals(Buffer.alloc(2 * 1024 * 1024 + 19, 2)),
    'both output pipes fully drain beyond pipe capacity with nonzero exit code');

  // The generic REPL intentionally does not expose process/env; inspect the
  // Node entrypoint directly and use the existing ControlProxy fixture tests
  // for its environment contract instead of weakening the REPL restrictions.
  const environmentProbe = start(nodeProxy, ['-e', 'console.log(JSON.stringify({proxy:process.env.HTTPS_PROXY,http:process.env.HTTP_PROXY,all:process.env.ALL_PROXY,backends:process.env.BROWSER_USE_AVAILABLE_BACKENDS}))']);
  const environmentResult = await finish(environmentProbe);
  const runtime = JSON.parse(environmentResult.stdout.toString());
  check(environmentResult.code === 0 && [runtime.proxy, runtime.http, runtime.all].every(value => value === 'http://127.0.0.1:17896') && runtime.backends === 'iab,mcpapps',
    'Node runtime retains proxy and browser restrictions');

  for (let attempt = 1; attempt <= 5; attempt++) {
    const entry = start(controlProxy, ['--disable-sandbox']);
    const client = mcp(entry);
    await client.request('initialize', {protocolVersion: '2024-11-05', capabilities: {}, clientInfo: {name: 'proxy-stdio-regression', version: '1.0.6'}});
    client.notify('notifications/initialized', {});
    const listed = await client.request('tools/list', {});
    assert.ok(listed.tools.some(tool => tool.name === 'js'));
    // Deliberately no ping, additional stdin, or version-only bypass while this is pending.
    const first = await client.request('tools/call', {name: 'js', arguments: {code: 'console.log(1 + 1)', timeout_ms: 3000}});
    const elapsedMs = Date.now() - entry.started;
    coldStarts.push({attempt, elapsedMs, output: output(first)});
    check(output(first).trim() === '2' && elapsedMs < 5000, 'real Node REPL cold start ' + attempt + ' completes without extra MCP input');
    if (attempt === 1) {
      const call = code => client.request('tools/call', {name: 'js', arguments: {code, timeout_ms: 3000}});
      await call('let proxyStdioValue = 6; console.log(proxyStdioValue)');
      check(output(await call('console.log(proxyStdioValue * 7)')).trim() === '42', 'real MCP preserves repeated requests and kernel state');
      output(await client.request('tools/call', {name: 'js_reset', arguments: {}}));
      check(output(await call('console.log(typeof proxyStdioValue)')).includes('undefined'), 'real MCP reset and subsequent kernel restart complete');
    }
    entry.child.stdin.end();
    const closed = await finish(entry);
    check(closed.code === 0 && client.protocolErrors.length === 0, 'real MCP session ' + attempt + ' exits cleanly on stdin EOF');
  }
}
(async () => {
  let failure;
  try { await main(); }
  catch (error) { failure = error.stack; console.error(failure); process.exitCode = 1; }
  finally {
    for (const entry of processes) {
      if (!entry.closed) entry.child.kill(); // Only processes created by this test.
      try { await bounded(entry.close, 3000, 'owned fixture cleanup'); }
      catch (error) { failure = [failure, error.message].filter(Boolean).join('\n'); process.exitCode = 1; }
    }
    const report = {at: new Date().toISOString(), passed: results.length, failure, coldStarts,
      results, processes: processes.map(entry => ({pid: entry.child.pid, executable: path.basename(entry.child.spawnfile), closed: entry.closed, stderr: Buffer.concat(entry.stderr).toString().slice(0, 2000)}))};
    fs.writeFileSync(path.join(root, 'proxy-stdio-results.json'), JSON.stringify(report, null, 2));
    console.log(JSON.stringify({passed: results.length, coldStartMs: coldStarts.map(item => item.elapsedMs), failed: !!failure}));
  }
})();
