// CDP helper for the Bilibili PC client (Electron, remote debugging on 127.0.0.1:9222)
//
//   node cdp.mjs list
//   node cdp.mjs eval   <urlSubstring> <jsExpression>
//   node cdp.mjs shot   <urlSubstring> <outFile.png>
//   node cdp.mjs click  <urlSubstring> <x> <y>
//   node cdp.mjs key    <urlSubstring> <key>
//   node cdp.mjs front  <urlSubstring>
//
// <urlSubstring> may be "-" to pick the first page target.
import { writeFileSync, readFileSync } from 'node:fs';

const HOST = 'http://127.0.0.1:9222';

async function listTargets() {
  const r = await fetch(HOST + '/json/list');
  if (!r.ok) throw new Error('GET /json/list -> ' + r.status);
  return r.json();
}

async function pick(match) {
  const targets = (await listTargets()).filter(t => t.type === 'page');
  if (!targets.length) throw new Error('no page targets');
  if (!match || match === '-') return targets[0];
  const hit = targets.find(t => (t.url || '').includes(match) || (t.title || '').includes(match));
  if (!hit) throw new Error('no target matching ' + match + '; have:\n' + targets.map(t => '  ' + t.url).join('\n'));
  return hit;
}

function connect(wsUrl) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(wsUrl);
    const pending = new Map();
    let seq = 0;
    ws.addEventListener('open', () => resolve({
      send(method, params = {}) {
        return new Promise((res, rej) => {
          const id = ++seq;
          pending.set(id, { res, rej });
          ws.send(JSON.stringify({ id, method, params }));
        });
      },
      close() { try { ws.close(); } catch {} },
    }));
    ws.addEventListener('error', () => reject(new Error('websocket error for ' + wsUrl)));
    ws.addEventListener('message', ev => {
      let msg;
      try { msg = JSON.parse(ev.data); } catch { return; }
      if (msg.id && pending.has(msg.id)) {
        const p = pending.get(msg.id);
        pending.delete(msg.id);
        if (msg.error) p.rej(new Error(msg.method + ' ' + JSON.stringify(msg.error)));
        else p.res(msg.result);
      }
    });
  });
}

export async function withTarget(match, fn) {
  const target = await pick(match);
  const conn = await connect(target.webSocketDebuggerUrl);
  try { return await fn(conn, target); } finally { conn.close(); }
}

export async function evaluate(match, expression) {
  return withTarget(match, async (conn) => {
    const r = await conn.send('Runtime.evaluate', {
      expression,
      returnByValue: true,
      awaitPromise: true,
      userGesture: true,
    });
    if (r.exceptionDetails) throw new Error('JS exception: ' + JSON.stringify(r.exceptionDetails));
    return r.result.value;
  });
}

const [cmd, ...args] = process.argv.slice(2);

if (cmd === 'list') {
  const targets = await listTargets();
  for (const t of targets) console.log(`${t.type}\t${t.title}\n\t${t.url}\n\t${t.webSocketDebuggerUrl}`);
} else if (cmd === 'eval') {
  let expr = args.slice(1).join(' ');
  if (expr.startsWith('@')) expr = readFileSync(expr.slice(1), 'utf8');
  console.log(JSON.stringify(await evaluate(args[0], expr), null, 2));
} else if (cmd === 'setrate') {
  const v = Number(args[1]);
  console.log(JSON.stringify(await evaluate(args[0],
    `(()=>{const v=document.querySelector('video');if(!v)return 'novideo';v.playbackRate=${v};return JSON.stringify({rate:v.playbackRate,paused:v.paused,base:window.__bliplpRate,active:window.__bliplpActive});})()`), null, 2));
} else if (cmd === 'shot') {
  const data = await withTarget(args[0], async (conn) => {
    await conn.send('Page.enable');
    const r = await conn.send('Page.captureScreenshot', { format: 'png' });
    return r.data;
  });
  writeFileSync(args[1], Buffer.from(data, 'base64'));
  console.log('wrote ' + args[1]);
} else if (cmd === 'click') {
  const x = Number(args[1]), y = Number(args[2]);
  await withTarget(args[0], async (conn) => {
    const base = { x, y, button: 'left', clickCount: 1, buttons: 1 };
    await conn.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x, y, buttons: 0 });
    await conn.send('Input.dispatchMouseEvent', { type: 'mousePressed', ...base });
    await new Promise(r => setTimeout(r, 60));
    await conn.send('Input.dispatchMouseEvent', { type: 'mouseReleased', ...base, buttons: 0 });
  });
  console.log(`clicked ${x},${y}`);
} else if (cmd === 'key') {
  await withTarget(args[0], async (conn) => {
    const key = args[1];
    await conn.send('Input.dispatchKeyEvent', { type: 'rawKeyDown', key, code: key, windowsVirtualKeyCode: 0 });
    await conn.send('Input.dispatchKeyEvent', { type: 'keyUp', key, code: key });
  });
  console.log('sent key ' + args[1]);
} else if (cmd === 'front') {
  await withTarget(args[0], (conn) => conn.send('Page.bringToFront'));
  console.log('brought to front');
} else {
  console.error('unknown command: ' + cmd);
  process.exit(2);
}
