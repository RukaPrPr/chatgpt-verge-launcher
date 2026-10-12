'use strict';
// Loaded only by the launcher's dedicated Node entry point. No certificate
// checks are disabled and a failed CONNECT never falls back to a direct socket.
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const https = require('node:https');
const net = require('node:net');
const tls = require('node:tls');
const dns = require('node:dns');
const dgram = require('node:dgram');
const { urlToHttpOptions } = require('node:url');
const { syncBuiltinESMExports } = require('node:module');

const proxyPort = Number(fs.readFileSync(path.join(__dirname, 'control-proxy.port'), 'utf8').trim());
if (!Number.isInteger(proxyPort) || proxyPort < 1 || proxyPort > 65535) throw new Error('Invalid Codex proxy port');
const proxyUrl = `http://127.0.0.1:${proxyPort}`;
for (const key of ['HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'http_proxy', 'https_proxy', 'all_proxy']) process.env[key] = proxyUrl;
process.env.NO_PROXY = process.env.no_proxy = 'localhost,127.0.0.1,::1,[::1]';
process.env.BROWSER_USE_AVAILABLE_BACKENDS = 'iab,mcpapps';

function local(host) {
  host = String(host || 'localhost').replace(/^\[|\]$/g, '').toLowerCase();
  return host === 'localhost' || host === '::1' || (net.isIP(host) === 4 && host.startsWith('127.'));
}
function denied(kind) {
  const error = new Error(`Codex proxy: direct ${kind} is disabled; use the configured HTTP proxy`);
  error.code = 'ERR_CODEX_PROXY_REQUIRED';
  return error;
}
function localPipe(value) {
  return typeof value === 'string' && /^\\\\[.?]\\pipe\\/i.test(value);
}

// Block unsupported raw transports before DNS lookup or socket creation.
// This supplements the OS guard; it is not a sandbox for hostile JavaScript.
const socketConnect = net.Socket.prototype.connect;
net.Socket.prototype.connect = function (...args) {
  let options = Array.isArray(args[0]) ? args[0][0] : args[0];
  if (typeof options !== 'object' || options === null) {
    options = (typeof args[0] === 'number' || /^\d+$/.test(String(args[0])))
      ? { host: typeof args[1] === 'string' ? args[1] : 'localhost' }
      : { path: args[0] };
  }
  if (options.path != null ? !localPipe(options.path) : !local(options.host || options.hostname)) {
    queueMicrotask(() => this.destroy(denied('TCP')));
    return this;
  }
  return socketConnect.apply(this, args);
};
const udpConnect = dgram.Socket.prototype.connect;
dgram.Socket.prototype.connect = function (port, address, callback) {
  if (typeof address === 'function') { callback = address; address = undefined; }
  if (!local(address)) { queueMicrotask(() => this.emit('error', denied('UDP'))); return this; }
  return udpConnect.call(this, port, address, callback);
};
const udpSend = dgram.Socket.prototype.send;
dgram.Socket.prototype.send = function (...args) {
  const address = args.slice(1).filter(value => typeof value === 'string').at(-1);
  if (address != null && !local(address)) {
    const callback = typeof args.at(-1) === 'function' ? args.at(-1) : null;
    queueMicrotask(() => callback ? callback(denied('UDP')) : this.emit('error', denied('UDP')));
    return;
  }
  return udpSend.apply(this, args);
};
for (const name of ['lookup', 'lookupService', 'resolve', 'resolve4', 'resolve6', 'resolveAny', 'resolveCaa', 'resolveCname', 'resolveMx', 'resolveNaptr', 'resolveNs', 'resolvePtr', 'resolveSoa', 'resolveSrv', 'resolveTxt', 'reverse']) {
 for (const object of [dns, dns.Resolver.prototype]) {
  const original = object[name];
  if (typeof original === 'function') object[name] = function (host, ...args) {
    // Only OS lookup of a loopback name is needed for local IPC. Explicit
    // resolver methods can send packets even for 'localhost'.
    if (object === dns && name === 'lookup' && local(host)) return original.call(this, host, ...args);
    const callback = args.at(-1);
    if (typeof callback !== 'function') throw new TypeError('DNS callback is required');
    queueMicrotask(() => callback(denied('DNS')));
  };
 }
 for (const object of [dns.promises, dns.promises.Resolver.prototype]) {
  const promised = object[name];
  if (typeof promised === 'function') object[name] = function (host, ...args) {
    return object === dns.promises && name === 'lookup' && local(host) ? promised.call(this, host, ...args) : Promise.reject(denied('DNS'));
  };
 }
}

function tunnel(options, callback) {
  const host = String(options.hostname || options.host || '').replace(/^\[|\]$/g, '');
  const port = Number(options.port || options.defaultPort || 80);
  if (!host || /[\s\x00-\x1f\x7f/@?#\\]/.test(host) || !Number.isInteger(port) || port < 1 || port > 65535) {
    queueMicrotask(() => callback(new Error('Invalid proxy CONNECT destination'))); return;
  }
  const authority = `${net.isIP(host) === 6 ? `[${host}]` : host}:${port}`;
  const socket = net.connect({ host: '127.0.0.1', port: proxyPort });
  let data = Buffer.alloc(0), done = false;
  const timer = setTimeout(() => finish(new Error('Codex proxy CONNECT timed out')), 10000);
  const finish = (error) => {
    if (done) return;
    done = true; clearTimeout(timer); socket.removeListener('data', receive); socket.removeListener('error', fail);
    if (error) { socket.destroy(); callback(error); }
    else { callback(null, socket); socket.resume(); }
  };
  const fail = error => finish(error);
  const receive = chunk => {
    data = Buffer.concat([data, chunk]);
    const end = data.indexOf('\r\n\r\n');
    if (end < 0) { if (data.length > 16384) finish(new Error('Proxy CONNECT headers too large')); return; }
    if (end > 16384) { finish(new Error('Proxy CONNECT headers too large')); return; }
    const match = /^HTTP\/1\.[01] (\d{3})(?: |\r\n)/.exec(data.toString('latin1', 0, end + 2));
    if (!match || match[1] !== '200') { finish(new Error('Proxy CONNECT was rejected')); return; }
    socket.pause();
    if (data.length > end + 4) socket.unshift(data.subarray(end + 4));
    finish();
  };
  socket.once('error', fail);
  socket.once('connect', () => socket.write(`CONNECT ${authority} HTTP/1.1\r\nHost: ${authority}\r\n\r\n`));
  socket.on('data', receive);
  socket.once('end', () => { if (!done) finish(new Error('Proxy closed before CONNECT completed')); });
  socket.once('close', () => { if (!done) finish(new Error('Proxy closed before CONNECT completed')); });
}
class HttpTunnelAgent extends http.Agent {
  createConnection(options, callback) { tunnel(options, callback); }
}
class HttpsTunnelAgent extends https.Agent {
  createConnection(options, callback) {
    tunnel({ ...options, defaultPort: 443 }, (error, socket) => {
      if (error) { callback(error); return; }
      const hostname = String(options.hostname || options.host || '').replace(/^\[|\]$/g, '');
      try {
        const secure = tls.connect({ ...options, socket, servername: options.servername || (net.isIP(hostname) ? undefined : hostname) });
        let settled = false;
        secure.once('secureConnect', () => { if (!settled) { settled = true; callback(null, secure); } });
        secure.once('error', failure => { if (!settled) { settled = true; callback(failure); } });
      } catch (failure) { socket.destroy(); callback(failure); }
    });
  }
}
const agents = { 'http:': new HttpTunnelAgent({ keepAlive: true }), 'https:': new HttpsTunnelAgent({ keepAlive: true }) };
for (const [protocol, module] of [['http:', http], ['https:', https]]) {
  const original = module.request;
  module.request = function (input, options, callback) {
    let values;
    if (typeof input === 'string' || input instanceof URL) {
      values = { ...urlToHttpOptions(new URL(input)), ...(options && typeof options === 'object' ? options : {}) };
      if (typeof options === 'function') callback = options;
    } else { values = { ...input }; callback = typeof options === 'function' ? options : callback; }
    if (values.socketPath != null) {
      if (!localPipe(values.socketPath)) throw denied('remote pipe');
    } else if (!local(values.hostname || values.host)) {
      values.agent = agents[protocol];
      delete values.createConnection;
    }
    return original.call(module, values, callback);
  };
  module.get = function (...args) { const request = module.request(...args); request.end(); return request; };
}
syncBuiltinESMExports();
