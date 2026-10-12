// No production port, DNS query, remote connection, hosts, firewall or app activation.
const fs = require('node:fs');
const path = require('node:path');
const net = require('node:net');
const crypto = require('node:crypto');
const {spawn} = require('node:child_process');
const assert = require('node:assert/strict');
const exe = path.resolve(process.argv[2]), root = path.resolve(process.argv[3]);
const results = [], owned = new Set();
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(test, timeout=6000) {
  const end=Date.now()+timeout;
  while (!test()) { if (Date.now()>end) throw Error('fixture deadline exceeded'); await sleep(20); }
}
function log(ctx) { try {return fs.readFileSync(ctx.log,'utf8');} catch {return '';} }
function fields(line) {return Object.fromEntries([...line.matchAll(/([A-Za-z][A-Za-z0-9]*)=([^ ]+)/g)].map(m=>[m[1],m[2]]));}
function record(name, detail={}) {results.push({name,passed:true,...detail});console.log('PASS '+name);}
async function start(name, mode='normal') {
  const directory=path.join(root,name); fs.mkdirSync(directory);
  const ctx={name,directory,instance:crypto.randomUUID(),log:path.join(directory,'relay.log'),ready:path.join(directory,'ready.json'),sockets:new Set(),received:[],receivedLength:0,clientReceived:[],clientLength:0,headers:[]};
  ctx.server=net.createServer({allowHalfOpen:true}, socket=>{
    ctx.sockets.add(socket);ctx.peer=socket;socket.on('error',()=>{});socket.on('close',()=>ctx.sockets.delete(socket));
    let header=Buffer.alloc(0),connected=false;
    socket.on('data',chunk=>{
      if (!connected) {
        header=Buffer.concat([header,chunk]);const end=header.indexOf('\r\n\r\n');if(end<0)return;
        assert.equal(end+4,header.length,'only CONNECT precedes acknowledgement');
        ctx.headers.push(header.toString('ascii'));connected=true;
        if(mode==='reject')return socket.end('HTTP/1.1 407 Proxy Authentication Required\r\nX-Secret: RESPONSE_SECRET\r\n\r\n');
        if(mode==='early_eof')return socket.end();
        if(mode==='timeout')return;
        socket.write(Buffer.concat([Buffer.from('HTTP/1.1 200 Connection established\r\n\r\n'),Buffer.from('SERVER_GREETING')]));
      } else {ctx.received.push(chunk);ctx.receivedLength+=chunk.length;}
    });
  });
  await new Promise(resolve=>ctx.server.listen(0,'127.0.0.1',resolve));
  const proxyPort=ctx.server.address().port; assert(![443,7896].includes(proxyPort));
  if(mode==='refused')await new Promise(resolve=>ctx.server.close(resolve));
  ctx.child=spawn(exe,['--instance',ctx.instance,'--listen-port','0','--proxy-port',String(proxyPort),'--connect-ip','203.0.113.1','--ready-file',ctx.ready,'--log-file',ctx.log],{windowsHide:true,stdio:['ignore','pipe','pipe']});
  ctx.stderr='';ctx.child.stderr.on('data',chunk=>{ctx.stderr+=chunk;});ctx.child.stdout.resume();
  owned.add(ctx);
  await until(()=>fs.existsSync(ctx.ready)||ctx.child.exitCode!==null);
  assert(fs.existsSync(ctx.ready),'relay ready');const ready=JSON.parse(fs.readFileSync(ctx.ready,'utf8'));ctx.port=ready.port;
  assert.equal(ready.pid,ctx.child.pid);assert(![443,7896].includes(ctx.port));
  ctx.client=net.connect({host:'127.0.0.1',port:ctx.port,allowHalfOpen:true});
  ctx.client.on('error',()=>{});ctx.client.on('data',chunk=>{ctx.clientReceived.push(chunk);ctx.clientLength+=chunk.length;});
  ctx.client.on('end',()=>{ctx.clientEnded=true;});ctx.client.on('close',()=>{ctx.clientClosed=true;});
  return ctx;
}
async function stop(ctx) {
  if(ctx.child.exitCode===null) {
    const control=spawn(exe,['--instance',ctx.instance,'--stop'],{windowsHide:true,stdio:'ignore'});
    await new Promise((resolve,reject)=>{control.once('error',reject);control.once('exit',code=>code===0?resolve():reject(Error('own stop failed '+code)));});
    await until(()=>ctx.child.exitCode!==null);assert.equal(ctx.child.exitCode,0);
  }
}
async function cleanup(ctx) {
  try {await stop(ctx);} finally {
    ctx.client?.destroy();for(const socket of ctx.sockets)socket.destroy();
    if(ctx.server.listening)await new Promise(resolve=>ctx.server.close(resolve));
    if(ctx.child.exitCode===null)ctx.child.kill();owned.delete(ctx);
  }
}
async function exchange(ctx, large=false) {
  await until(()=>log(ctx).includes('CONNECT_OK')&&ctx.clientLength>=15);
  assert.equal(Buffer.concat(ctx.clientReceived).toString(),'SERVER_GREETING');
  const upload=large?crypto.randomBytes(1048593):Buffer.from('AUTH_TOKEN_MUST_NOT_APPEAR_IN_LOG');
  const download=large?crypto.randomBytes(1048639):Buffer.from('RESPONSE_BODY_MUST_NOT_APPEAR_IN_LOG');
  ctx.client.write(upload);ctx.peer.write(download);
  await until(()=>ctx.receivedLength===upload.length&&ctx.clientLength===15+download.length);
  assert.deepEqual(Buffer.concat(ctx.received),upload);
  assert.deepEqual(Buffer.concat(ctx.clientReceived),Buffer.concat([Buffer.from('SERVER_GREETING'),download]));
  assert(ctx.headers[0].startsWith('CONNECT 203.0.113.1:443 HTTP/1.1\r\n'));
  return {up:upload.length,down:15+download.length};
}
async function completed(ctx) {
  await until(()=>/tunnel=1 (CLOSED|FAILED) /.test(log(ctx)));
  return fields(log(ctx).split(/\r?\n/).find(line=>/tunnel=1 (CLOSED|FAILED) /.test(line)));
}
async function transferEnd(name, ending, large=false) {
  const ctx=await start(name);
  try {
    const counts=await exchange(ctx,large);
    if(ending==='local_eof')ctx.client.end();
    if(ending==='proxy_eof')ctx.peer.end();
    if(ending==='local_reset')ctx.client.resetAndDestroy();
    if(ending==='proxy_reset')ctx.peer.resetAndDestroy();
    const data=await completed(ctx);
    assert.equal(Number(data.upBytes),counts.up);assert.equal(Number(data.downBytes),counts.down);
    const local=ending.startsWith('local');assert.equal(data.firstObserved,local?'local_to_proxy':'proxy_to_local');
    assert.equal(data[local?'upAtClose':'downAtClose'],ending.endsWith('eof')?'eof':'faulted');
    assert.equal(data[local?'downAtClose':'upAtClose'],'pending');
    if(ending.endsWith('reset'))assert.equal(data[local?'upNativeError':'downNativeError'],'10054');
    assert.equal(data.cleanupReason,'transfer_completed');
    assert(!log(ctx).includes('AUTH_TOKEN_MUST_NOT_APPEAR_IN_LOG'));
    assert(!log(ctx).includes('RESPONSE_BODY_MUST_NOT_APPEAR_IN_LOG'));
    fs.writeFileSync(path.join(ctx.directory,'checked-summary.json'),JSON.stringify(data,null,2));
    record(name,{upBytes:counts.up,downBytes:counts.down,firstObserved:data.firstObserved});
  } finally {await cleanup(ctx);}
}
async function handshake(name, mode) {
  const started=Date.now(),ctx=await start(name,mode);
  try {
    const data=await completed(ctx),elapsed=Date.now()-started;
    assert.equal(data.phase,mode==='refused'?'proxy_connect':'connect_response');
    assert.equal(data.upBytes,'0');assert.equal(data.downBytes,'0');
    assert.equal(data.cleanupReason,'failure');assert(!log(ctx).includes('CONNECT_OK'));
    if(mode==='reject'){assert.equal(data.connectStatus,'407');assert(!log(ctx).includes('RESPONSE_SECRET'));}
    if(mode==='refused')assert.equal(data.failureNativeError,'10061');
    if(mode==='timeout'){assert.equal(data.failureErrorType,'TimeoutException');assert(elapsed>=4900&&elapsed<10000);}
    record(name,{elapsedMs:elapsed,errorType:data.failureErrorType});
  } finally {await cleanup(ctx);}
}
async function idleAndStop() {
  const ctx=await start('idle_and_explicit_stop');
  try {
    const counts=await exchange(ctx);
    await sleep(21000);
    assert.equal(ctx.child.exitCode,null);assert(!ctx.clientEnded);assert(!log(ctx).includes(' CLOSED '));
    ctx.client.write('AFTER_IDLE');ctx.peer.write('AFTER_IDLE');
    await until(()=>ctx.receivedLength===counts.up+10&&ctx.clientLength===counts.down+10);
    await stop(ctx);
    const line=log(ctx).split(/\r?\n/).find(line=>line.includes(' SHUTDOWN_SNAPSHOT '));assert(line,'shutdown evidence persisted');
    const data=fields(line);assert.equal(data.cleanupReason,'relay_shutdown');assert.equal(data.stopReason,'stop_event');
    assert.equal(Number(data.upBytes),counts.up+10);assert.equal(Number(data.downBytes),counts.down+10);
    record('idle_over_20_seconds_still_forwards');record('explicit_stop_records_active_tunnel_snapshot');
  } finally {await cleanup(ctx);}
}
async function loggingFailure() {
  const ctx=await start('primary_log_failure');
  try {
    await exchange(ctx);
    fs.renameSync(ctx.log,ctx.log+'.before');fs.mkdirSync(ctx.log);
    ctx.client.write('STILL_FORWARDS');await until(()=>Buffer.concat(ctx.received).includes('STILL_FORWARDS'));
    assert.equal(ctx.child.exitCode,null);ctx.client.end();
    await until(()=>ctx.stderr.includes('LOG_WRITE_FAILED'));
    const fallbackDir=path.join(path.dirname(exe),'diagnostics');
    const fallback=fs.readdirSync(fallbackDir).find(name=>name.startsWith('relay-'+ctx.child.pid+'-'));
    assert(fallback);const text=fs.readFileSync(path.join(fallbackDir,fallback),'utf8');
    assert(text.includes('CLOSED phase=forward'));assert(text.includes('upBytes='));
    fs.writeFileSync(path.join(ctx.directory,'fallback-evidence.txt'),text);
    record('log_write_failure_preserves_forwarding_and_fallback_summary');
  } finally {await cleanup(ctx);}
}
(async()=>{
  await transferEnd('local_eof_large_opaque_bytes','local_eof',true);
  await transferEnd('proxy_eof','proxy_eof');
  await transferEnd('local_tcp_reset','local_reset');
  await transferEnd('proxy_tcp_reset','proxy_reset');
  await handshake('connect_407','reject');
  await handshake('connect_early_eof','early_eof');
  await handshake('connect_refused','refused');
  await handshake('existing_5_second_connect_deadline','timeout');
  await idleAndStop();
  await loggingFailure();
  fs.writeFileSync(path.join(root,'relay-diagnostics-results.json'),JSON.stringify({at:new Date().toISOString(),passed:results.length,externalNetworkUsed:false,productionPortUsed:false,results},null,2));
})().catch(async error=>{
  console.error(error);for(const ctx of owned)try{await cleanup(ctx);}catch{}process.exitCode=1;
});
