// OHIF viewer smoke test (opt-in, real-browser via Chrome DevTools Protocol).
//
// Purpose: verify the bundled OHIF viewer boots against the server's standard
// DICOMweb endpoints (/dicomweb) and successfully issues QIDO/WADO requests.
//
// Requirements: Node.js >= 21 (global fetch + WebSocket) and a local Chrome/Edge.
//
// Usage:
//   node smoke.mjs --base http://127.0.0.1:5000 --study <StudyInstanceUID> \
//        [--user admin] [--pass <password>] [--chrome "<path to chrome.exe>"]
//
// Notes:
//   - The account must already be past the forced password change (login must
//     return 200 without a must-change-password block).
//   - Exit code 0 = OHIF reached /dicomweb with no failed DICOMweb responses.
//   - Headless layout may report zero-size viewports; this tool therefore treats
//     "OHIF issued /dicomweb requests without errors" as success and reports the
//     canvas/viewport state for information only.

import { spawn } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

function parseArgs(argv) {
  const args = {};
  for (let i = 2; i < argv.length; i += 2) {
    const key = argv[i].replace(/^--/, '');
    args[key] = argv[i + 1];
  }
  return args;
}

const args = parseArgs(process.argv);
const BASE = (args.base || 'http://127.0.0.1:5000').replace(/\/$/, '');
const STUDY = args.study;
const USER = args.user || 'admin';
const PASS = args.pass || 'admin';
const CHROME = args.chrome || process.env.CHROME ||
  'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const DEBUG_PORT = parseInt(args.port || '9222', 10);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const log = (...a) => console.log('[ohif-smoke]', ...a);

if (!STUDY) {
  console.error('Missing --study <StudyInstanceUID>');
  process.exit(2);
}
if (!fs.existsSync(CHROME)) {
  console.error(`Chrome not found: ${CHROME} (pass --chrome "<path>")`);
  process.exit(2);
}

async function login() {
  const cookies = {};
  const res = await fetch(BASE + '/api/Auth/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username: USER, password: PASS })
  });
  for (const c of (res.headers.getSetCookie ? res.headers.getSetCookie() : [])) {
    const pair = c.split(';')[0];
    const i = pair.indexOf('=');
    if (i > 0) cookies[pair.slice(0, i).trim()] = pair.slice(i + 1).trim();
  }
  if (!res.ok) {
    throw new Error(`login failed: HTTP ${res.status} (change the password first?)`);
  }
  log('login ok, cookies:', Object.keys(cookies).join(','));
  return cookies;
}

async function connect(proc) {
  let target = null;
  for (let i = 0; i < 60; i++) {
    try {
      const list = await (await fetch(`http://127.0.0.1:${DEBUG_PORT}/json/list`)).json();
      target = list.find((t) => t.type === 'page');
      if (target) break;
    } catch { /* not ready */ }
    await sleep(500);
  }
  if (!target) { proc.kill(); throw new Error('chrome devtools target not available'); }

  const ws = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });

  let id = 0;
  const pending = new Map();
  const events = [];
  ws.onmessage = (m) => {
    const d = JSON.parse(m.data);
    if (d.id && pending.has(d.id)) { pending.get(d.id)(d); pending.delete(d.id); }
    else if (d.method) { events.push(d); }
  };
  const send = (method, params = {}) => new Promise((res) => {
    const i = ++id; pending.set(i, res); ws.send(JSON.stringify({ id: i, method, params }));
  });
  return { ws, send, events };
}

async function run() {
  const cookies = await login();
  const userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ohif-smoke-'));
  const proc = spawn(CHROME, [
    '--headless=new', `--remote-debugging-port=${DEBUG_PORT}`, `--user-data-dir=${userDataDir}`,
    '--no-first-run', '--no-default-browser-check', '--disable-gpu', '--disable-extensions',
    '--window-size=1600,1000', 'about:blank'
  ], { stdio: 'ignore' });

  const { ws, send, events } = await connect(proc);
  await send('Network.enable');
  await send('Page.enable');
  await send('Runtime.enable');
  await send('Emulation.setDeviceMetricsOverride', { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });
  for (const [name, value] of Object.entries(cookies)) {
    await send('Network.setCookie', { name, value, url: BASE, path: '/' });
  }

  const url = `${BASE}/dicomviewer/viewer?StudyInstanceUIDs=${encodeURIComponent(STUDY)}`;
  log('navigate:', url);
  await send('Page.navigate', { url });
  await sleep(30000);
  await send('Runtime.evaluate', { expression: `window.dispatchEvent(new Event('resize'));` });
  await sleep(10000);

  const dicomwebResps = events
    .filter((e) => e.method === 'Network.responseReceived' && e.params?.response?.url?.includes('/dicomweb'))
    .map((e) => ({ url: e.params.response.url, status: e.params.response.status }));
  const consoleErrors = events
    .filter((e) => e.method === 'Runtime.consoleAPICalled' && e.params.type === 'error')
    .map((e) => (e.params.args || []).map((a) => a.value ?? a.description ?? '').join(' ').slice(0, 160));
  const exceptions = events
    .filter((e) => e.method === 'Runtime.exceptionThrown')
    .map((e) => e.params?.exceptionDetails?.exception?.description?.slice(0, 160) || 'exception');

  const dom = await send('Runtime.evaluate', {
    expression: `(function(){return {canvasCount:document.querySelectorAll('canvas').length, textLen:(document.body?document.body.innerText:'').length};})()`,
    returnByValue: true
  });

  ws.close();
  proc.kill();

  const ok = dicomwebResps.length > 0 && dicomwebResps.every((r) => r.status < 400) && exceptions.length === 0;

  console.log('===== OHIF SMOKE RESULT =====');
  console.log('dicomweb responses:', JSON.stringify(dicomwebResps, null, 2));
  console.log('console errors:', JSON.stringify(consoleErrors, null, 2));
  console.log('page exceptions:', JSON.stringify(exceptions, null, 2));
  console.log('dom:', JSON.stringify(dom.result?.result?.value));
  console.log(ok ? 'PASS' : 'FAIL');
  process.exit(ok ? 0 : 1);
}

run().catch((e) => { console.error('smoke error:', e); process.exit(1); });
