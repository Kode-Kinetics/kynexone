#!/usr/bin/env node
/**
 * verify-login.mjs — reusable verification harness for the KynexOne login page.
 *
 * Run from the frontend dir so playwright/sharp resolve:
 *   cd /Users/zackkhan/Downloads/KynexOne/frontend && node /tmp/verify-login.mjs [url]
 *
 * Env knobs:
 *   VERIFY_BASE=/path/to/frontend        where to resolve playwright + sharp from
 *   VERIFY_CONTRAST_VIEWPORTS=1288x717,390x844
 *   VERIFY_ALLOW_VSCROLL=1               treat vertical scroll as OK (still reported)
 *   VERIFY_RAF_MS=8000  VERIFY_RM_MS=5000
 *   VERIFY_HEADED=1                      watch it run
 *   VERIFY_ARTIFACTS=/tmp/verify-login-out   where PNGs are written
 */

import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';
import fs from 'node:fs';
import path from 'node:path';

// ---------------------------------------------------------------- module load
const BASE = process.env.VERIFY_BASE || process.cwd();
const req = createRequire(path.join(BASE, 'package.json'));
async function load(name) {
  let resolved;
  try {
    resolved = req.resolve(name);
  } catch {
    console.error(`FATAL: cannot resolve "${name}" from ${BASE}.`);
    console.error(`Run from the frontend dir, or set VERIFY_BASE=/path/to/frontend.`);
    process.exit(2);
  }
  const m = await import(pathToFileURL(resolved).href);
  return m?.default?.[name] ? m.default : (m.chromium || m.default ? m : m);
}
const pwMod = await load('playwright');
const chromium = pwMod.chromium || pwMod.default?.chromium;
const sharpMod = await load('sharp');
const sharp = sharpMod.default || sharpMod;

// ---------------------------------------------------------------------- setup
const URL_ = process.argv[2] || process.env.VERIFY_URL || 'http://localhost:3111/login';
const OUT = process.env.VERIFY_ARTIFACTS || '/tmp/verify-login-out';
fs.mkdirSync(OUT, { recursive: true });
const HEADED = process.env.VERIFY_HEADED === '1';
const RAF_MS = Number(process.env.VERIFY_RAF_MS || 8000);
const RM_MS = Number(process.env.VERIFY_RM_MS || 5000);
const ALLOW_VSCROLL = process.env.VERIFY_ALLOW_VSCROLL === '1';
const CONTRAST_VPS = (process.env.VERIFY_CONTRAST_VIEWPORTS || '1288x717,390x844')
  .split(',').map(s => s.trim()).filter(Boolean)
  .map(s => { const [w, h] = s.split('x').map(Number); return { w, h }; });

const VIEWPORTS = [
  { name: 'desktop-1288x717', w: 1288, h: 717 },
  { name: 'desktop-1440x900', w: 1440, h: 900 },
  { name: 'laptop-1024x768', w: 1024, h: 768 },
  { name: 'mobile-390x844', w: 390, h: 844 },
];

const results = [];   // {id, name, status: PASS|FAIL|WARN|SKIP|ERROR, detail}
const sections = [];  // {title, rows:[[col,...]]}
function record(id, name, status, detail = '') {
  results.push({ id, name, status, detail });
}
function section(title, header, rows) {
  sections.push({ title, header, rows });
}
async function step(id, name, fn) {
  try {
    await fn();
  } catch (e) {
    record(id, name, 'ERROR', String(e && e.message || e).split('\n')[0].slice(0, 200));
  }
}

// ------------------------------------------------------------ colour helpers
const srgb = c => { c /= 255; return c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
const lum = (r, g, b) => 0.2126 * srgb(r) + 0.7152 * srgb(g) + 0.0722 * srgb(b);
function contrast(a, b) {
  const l1 = lum(a[0], a[1], a[2]), l2 = lum(b[0], b[1], b[2]);
  const hi = Math.max(l1, l2), lo = Math.min(l1, l2);
  return (hi + 0.05) / (lo + 0.05);
}
const hex = c => '#' + c.map(v => Math.round(v).toString(16).padStart(2, '0')).join('');
const pct = (arr, p) => {
  if (!arr.length) return NaN;
  const s = [...arr].sort((a, b) => a - b);
  return s[Math.min(s.length - 1, Math.max(0, Math.round((p / 100) * (s.length - 1))))];
};

// ------------------------------------------------------------- in-page probes
const COLLECT_RUNS = `(() => {
  const out = [];
  const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
  let n, i = 0;
  while ((n = walker.nextNode())) {
    const raw = n.nodeValue;
    if (!raw || !raw.trim()) continue;
    const el = n.parentElement;
    if (!el) continue;
    if (['SCRIPT','STYLE','NOSCRIPT','TEMPLATE','TITLE'].includes(el.tagName)) continue;
    const cs = getComputedStyle(el);
    if (cs.visibility === 'hidden' || cs.display === 'none') continue;
    if (parseFloat(cs.opacity) < 0.05) continue;
    const r = document.createRange();
    r.selectNodeContents(n);
    let rects = Array.from(r.getClientRects()).filter(x => x.width >= 3 && x.height >= 5);
    if (!rects.length) continue;
    // overflow:hidden does NOT change getClientRects, so a clipped run still
    // reports geometry. Sampling it measures bare background and invents a
    // contrast failure for text that is never painted (the marquee tail did
    // exactly this: 1.02:1 on words sitting outside their clip window).
    // Keep only rects whose centre hit-tests back to this element.
    rects = rects.filter(x => {
      const cx = x.x + x.width / 2, cy = x.y + x.height / 2;
      if (cx < 0 || cy < 0 || cx > innerWidth || cy > innerHeight) return false;
      const hit = document.elementFromPoint(cx, cy);
      return !!hit && (hit === el || el.contains(hit) || hit.contains(el));
    });
    if (!rects.length) continue;
    const fs = parseFloat(cs.fontSize);
    let fw = parseInt(cs.fontWeight, 10); if (!fw) fw = cs.fontWeight === 'bold' ? 700 : 400;
    const large = fs >= 24 || (fs >= 18.66 && fw >= 700);
    out.push({
      i: i++,
      text: raw.trim().replace(/\\s+/g, ' ').slice(0, 44),
      tag: el.tagName.toLowerCase(),
      cls: (el.className && el.className.baseVal !== undefined ? el.className.baseVal : String(el.className || '')).split(/\\s+/).filter(Boolean).slice(0,2).join('.'),
      fontSize: Math.round(fs * 10) / 10,
      fontWeight: fw,
      large,
      cssColor: cs.color,
      rects: rects.map(x => ({ x: x.x, y: x.y, w: x.width, h: x.height })),
    });
  }
  return out;
})()`;

const HIDE_INK_CSS = `*, *::before, *::after {
  color: transparent !important;
  -webkit-text-fill-color: transparent !important;
  text-shadow: none !important;
  text-decoration-color: transparent !important;
  caret-color: transparent !important;
}`;

// Named per the HTML-AAM precedence order. Passed as a real function so
// Playwright forwards the selector argument (a string body would not).
const NAME_PROBE = (sel) => {
  const el = document.querySelector(sel);
  if (!el) return null;
  const txt = (e) => (e ? (e.innerText || e.textContent || '').trim().replace(/\s+/g, ' ') : '');
  const lb = el.getAttribute('aria-labelledby');
  if (lb) {
    const parts = lb.split(/\s+/).map(id => txt(document.getElementById(id))).filter(Boolean);
    if (parts.length) return { name: parts.join(' '), technique: 'aria-labelledby' };
  }
  const al = el.getAttribute('aria-label');
  if (al && al.trim()) return { name: al.trim(), technique: 'aria-label' };
  if (el.id) {
    const l = document.querySelector('label[for="' + CSS.escape(el.id) + '"]');
    if (l && txt(l)) return { name: txt(l), technique: 'label-for' };
  }
  const wrap = el.closest('label');
  if (wrap && txt(wrap)) return { name: txt(wrap), technique: 'label-wrapping' };
  // Controls named from their own contents (button, link, summary).
  const tag = el.tagName.toLowerCase();
  const role = (el.getAttribute('role') || '').toLowerCase();
  if (['button', 'a', 'summary'].includes(tag) || ['button', 'link'].includes(role)) {
    if (txt(el)) return { name: txt(el), technique: 'element-contents' };
    if (el.value && String(el.value).trim()) return { name: String(el.value).trim(), technique: 'value' };
  }
  const t = el.getAttribute('title');
  if (t && t.trim()) return { name: t.trim(), technique: 'title' };
  const ph = el.getAttribute('placeholder');
  if (ph && ph.trim()) return { name: ph.trim(), technique: 'placeholder-only' };
  return { name: '', technique: 'none' };
};

const DESCRIBE_ACTIVE = `(() => {
  const a = document.activeElement;
  if (!a || a === document.body) return { tag: 'body' };
  return {
    tag: a.tagName.toLowerCase(),
    id: a.id || '',
    type: a.getAttribute('type') || '',
    role: a.getAttribute('role') || '',
    text: (a.innerText || a.value || '').trim().replace(/\\s+/g,' ').slice(0, 30),
  };
})()`;

const INIT_INSTRUMENT = () => {
  window.__rafCount = 0;
  const orig = window.requestAnimationFrame.bind(window);
  window.requestAnimationFrame = function (cb) { window.__rafCount++; return orig(cb); };
  window.__ctxCalls = [];
  const og = HTMLCanvasElement.prototype.getContext;
  HTMLCanvasElement.prototype.getContext = function (type, ...rest) {
    let ctx = null, err = null;
    try { ctx = og.call(this, type, ...rest); } catch (e) { err = String(e && e.message || e); }
    try { window.__ctxCalls.push({ type: String(type), ok: !!ctx, err }); } catch {}
    return ctx;
  };
};

const INIT_NO_WEBGL = () => {
  const og = HTMLCanvasElement.prototype.getContext;
  HTMLCanvasElement.prototype.getContext = function (type, ...rest) {
    const t = String(type).toLowerCase();
    if (t === 'webgl' || t === 'webgl2' || t === 'experimental-webgl' || t === 'webgpu') return null;
    return og.call(this, type, ...rest);
  };
};

// ------------------------------------------------------------------ utilities
async function settle(page) {
  await page.waitForLoadState('domcontentloaded');
  try { await page.waitForLoadState('networkidle', { timeout: 8000 }); } catch {}
  try { await page.waitForSelector('#li-em', { timeout: 15000, state: 'visible' }); } catch {}
  await page.waitForTimeout(900);
}
function watch(page, bag) {
  page.on('console', m => { if (m.type() === 'error') bag.console.push(m.text().slice(0, 300)); });
  page.on('pageerror', e => bag.pageerrors.push(String(e && e.message || e).slice(0, 300)));
  page.on('requestfailed', r => {
    const f = r.failure(); const t = f && f.errorText;
    if (t && /ERR_ABORTED/.test(t)) return;
    bag.netfail.push(`${r.url().slice(0, 110)} :: ${t}`);
  });
}
async function raw(buf) {
  const { data, info } = await sharp(buf).removeAlpha().raw().toBuffer({ resolveWithObject: true });
  return { data, w: info.width, h: info.height, ch: info.channels };
}

// ==================================================================== RUN ====
const browser = await chromium.launch({ headless: !HEADED });
const started = Date.now();
let mainBag, mainPage, mainCtx;

console.log(`\nverify-login  ->  ${URL_}`);
const ver = n => { try { return JSON.parse(fs.readFileSync(path.join(BASE, 'node_modules', n, 'package.json'), 'utf8')).version; } catch { return '?'; } };
console.log(`playwright ${ver('playwright')} · sharp ${ver('sharp')} · ${new Date().toISOString()}`);

// ---- 1. console / page errors ------------------------------------------------
await step('1', 'No console errors / page errors', async () => {
  mainCtx = await browser.newContext({ viewport: { width: 1288, height: 717 }, deviceScaleFactor: 1 });
  mainBag = { console: [], pageerrors: [], netfail: [] };
  mainPage = await mainCtx.newPage();
  await mainPage.addInitScript(INIT_INSTRUMENT);
  watch(mainPage, mainBag);
  await mainPage.goto(URL_, { waitUntil: 'commit', timeout: 45000 });
  await settle(mainPage);
  const bad = mainBag.console.length + mainBag.pageerrors.length;
  const detail = bad === 0
    ? `0 console errors, 0 page errors${mainBag.netfail.length ? ` (${mainBag.netfail.length} failed requests, informational)` : ''}`
    : `${mainBag.console.length} console error(s), ${mainBag.pageerrors.length} page error(s)`;
  record('1', 'No console errors / page errors', bad === 0 ? 'PASS' : 'FAIL', detail);
  const rows = [];
  for (const t of mainBag.pageerrors) rows.push(['pageerror', t]);
  for (const t of mainBag.console) rows.push(['console.error', t]);
  for (const t of mainBag.netfail) rows.push(['requestfailed', t]);
  if (rows.length) section('1 · errors observed', ['kind', 'message'], rows);
});

// ---- 2. overflow -------------------------------------------------------------
await step('2', 'No overflow at 4 viewports', async () => {
  const rows = [];
  let fails = 0, warns = 0;
  for (const vp of VIEWPORTS) {
    await mainPage.setViewportSize({ width: vp.w, height: vp.h });
    await mainPage.evaluate(() => window.scrollTo(0, 0));
    await mainPage.waitForTimeout(700);
    const o = await mainPage.evaluate(() => {
      const de = document.documentElement, b = document.body;
      const vw = de.clientWidth, vh = de.clientHeight;
      const sw = Math.max(de.scrollWidth, b.scrollWidth), sh = Math.max(de.scrollHeight, b.scrollHeight);
      const offenders = [];
      for (const el of document.querySelectorAll('body *')) {
        const cs = getComputedStyle(el);
        if (cs.display === 'none' || cs.visibility === 'hidden') continue;
        const r = el.getBoundingClientRect();
        if (r.width < 1 || r.height < 1) continue;
        if (r.right > vw + 1 || r.left < -1) {
          offenders.push({
            sel: el.tagName.toLowerCase() + (el.id ? '#' + el.id : '') +
                 (el.className && typeof el.className === 'string' ? '.' + el.className.split(/\s+/).filter(Boolean).slice(0,2).join('.') : ''),
            left: Math.round(r.left), right: Math.round(r.right), pos: cs.position,
          });
        }
      }
      return { vw, vh, sw, sh, offenders: offenders.slice(0, 6) };
    });
    const hx = o.sw - o.vw, vy = o.sh - o.vh;
    const hOver = hx > 1, vOver = vy > 1;
    let status = 'PASS';
    if (hOver) { status = 'FAIL'; fails++; }
    else if (vOver) { if (ALLOW_VSCROLL) { status = 'WARN'; warns++; } else { status = 'FAIL'; fails++; } }
    rows.push([vp.name, `${o.sw}x${o.sh} vs ${o.vw}x${o.vh}`,
      hOver ? `+${hx}px H` : '-', vOver ? `+${vy}px V` : '-', status,
      o.offenders.length ? o.offenders.map(x => `${x.sel}[${x.left}..${x.right}]`).join(' ') : '']);
  }
  section('2 · overflow by viewport', ['viewport', 'scroll vs client', 'h-overflow', 'v-overflow', '', 'x-offenders'], rows);
  record('2', 'No overflow at 4 viewports', fails ? 'FAIL' : (warns ? 'WARN' : 'PASS'),
    fails ? `${fails} viewport(s) overflow` : (warns ? `${warns} viewport(s) scroll vertically (allowed)` : 'all 4 viewports clean'));
  await mainPage.setViewportSize({ width: 1288, height: 717 });
  await mainPage.waitForTimeout(400);
});

// ---- 3. required fields ------------------------------------------------------
const FIELDS = [
  { sel: '#li-em', type: 'email', ac: 'email' },
  { sel: '#li-pw', type: 'password', ac: 'current-password' },
  { sel: '#li-ws', type: null, ac: 'organization' },
];
await step('3', 'Contract: ids / type / autocomplete / shell', async () => {
  const rows = [];
  let fails = 0;
  for (const f of FIELDS) {
    const info = await mainPage.evaluate((sel) => {
      const el = document.querySelector(sel);
      if (!el) return null;
      const r = el.getBoundingClientRect();
      const cs = getComputedStyle(el);
      return {
        tag: el.tagName.toLowerCase(),
        type: el.getAttribute('type') || '',
        ac: el.getAttribute('autocomplete') || '',
        disabled: !!el.disabled, readOnly: !!el.readOnly,
        visible: r.width > 0 && r.height > 0 && cs.visibility !== 'hidden' && cs.display !== 'none' && parseFloat(cs.opacity) > 0.01,
        w: Math.round(r.width), h: Math.round(r.height),
      };
    }, f.sel);
    if (!info) { rows.push([f.sel, 'MISSING', '-', '-', '-', 'FAIL']); fails++; continue; }
    const typeOk = f.type ? info.type === f.type : true;
    const acOk = info.ac === f.ac;
    const ok = info.visible && !info.disabled && !info.readOnly && typeOk && acOk;
    if (!ok) fails++;
    rows.push([f.sel, `<${info.tag}> ${info.w}x${info.h}`,
      `type=${info.type || '(none)'}${typeOk ? '' : ` want ${f.type}`}`,
      `ac=${info.ac || '(none)'}${acOk ? '' : ` want ${f.ac}`}`,
      `${info.visible ? 'visible' : 'HIDDEN'}${info.disabled ? ' DISABLED' : ''}${info.readOnly ? ' READONLY' : ''}`,
      ok ? 'PASS' : 'FAIL']);
  }
  const shell = await mainPage.evaluate(() => {
    const els = document.querySelectorAll('.tenant-login-shell');
    if (!els.length) return { n: 0 };
    const r = els[0].getBoundingClientRect();
    return { n: els.length, w: Math.round(r.width), h: Math.round(r.height) };
  });
  if (!shell.n) fails++;
  rows.push(['.tenant-login-shell', shell.n ? `${shell.n} node(s) ${shell.w}x${shell.h}` : 'MISSING', '-', '-', '-', shell.n ? 'PASS' : 'FAIL']);
  section('3 · field contract', ['target', 'element', 'type', 'autocomplete', 'state', ''], rows);
  record('3', 'Contract: ids / type / autocomplete / shell', fails ? 'FAIL' : 'PASS',
    fails ? `${fails} contract violation(s)` : 'all three inputs + shell correct');
});

// ---- 4. accessible names -----------------------------------------------------
await step('4', 'Accessible names (and technique)', async () => {
  const rows = [];
  let fails = 0, phOnly = 0;
  const targets = [...FIELDS.map(f => f.sel), 'button[type="submit"]'];
  for (const sel of targets) {
    const r = await mainPage.evaluate(NAME_PROBE, sel).catch(() => null);
    if (!r) { rows.push([sel, '(element not found)', '-', 'FAIL']); fails++; continue; }
    const has = !!r.name;
    if (!has) fails++;
    if (r.technique === 'placeholder-only') phOnly++;
    const status = !has ? 'FAIL' : (r.technique === 'placeholder-only' ? 'WARN' : 'PASS');
    rows.push([sel, r.name ? `"${r.name.slice(0, 40)}"` : '(empty)', r.technique,
      status + (r.technique === 'placeholder-only' ? '  <- placeholder-only' : '')]);
  }
  section('4 · accessible names', ['target', 'accessible name', 'technique', ''], rows);
  record('4', 'Accessible names (and technique)', fails ? 'FAIL' : (phOnly ? 'WARN' : 'PASS'),
    fails ? `${fails} element(s) with no accessible name`
          : (phOnly ? `${phOnly} named by placeholder only` : 'all named by label/aria'));
});

// ---- 5. tab order ------------------------------------------------------------
await step('5', 'Tab order email -> password -> workspace -> submit', async () => {
  await mainPage.evaluate(() => { window.scrollTo(0, 0); document.body.focus(); if (document.activeElement) document.activeElement.blur(); });
  await mainPage.click('body', { position: { x: 3, y: 3 } }).catch(() => {});
  const stops = [];
  for (let i = 0; i < 25; i++) {
    await mainPage.keyboard.press('Tab');
    const d = await mainPage.evaluate(DESCRIBE_ACTIVE);
    stops.push(d);
    if (d.id === 'li-ws') { // grab a few more to find submit
      for (let j = 0; j < 4; j++) {
        await mainPage.keyboard.press('Tab');
        stops.push(await mainPage.evaluate(DESCRIBE_ACTIVE));
      }
      break;
    }
  }
  const idx = id => stops.findIndex(s => s.id === id);
  const iEm = idx('li-em'), iPw = idx('li-pw'), iWs = idx('li-ws');
  const iSub = stops.findIndex((s, k) => k > iWs && iWs >= 0 && s.tag === 'button' &&
    (s.type === 'submit' || /sign\s*in/i.test(s.text)));
  const ok = iEm >= 0 && iPw > iEm && iWs > iPw && iSub > iWs;
  section('5 · tab stops', ['#', 'element', 'id/type', 'text'],
    stops.map((s, k) => [String(k + 1), s.tag, s.id || s.type || s.role || '-', s.text || '']));
  record('5', 'Tab order email -> password -> workspace -> submit', ok ? 'PASS' : 'FAIL',
    `email@${iEm + 1} password@${iPw + 1} workspace@${iWs + 1} submit@${iSub + 1}` +
    (ok ? '' : ' — order broken or a stop never reached'));
});

// ---- 6. MEASURED contrast ----------------------------------------------------
async function contrastPass(vp) {
  const ctx = await browser.newContext({ viewport: { width: vp.w, height: vp.h }, deviceScaleFactor: 1 });
  const page = await ctx.newPage();
  const bag = { console: [], pageerrors: [], netfail: [] };
  watch(page, bag);
  await page.goto(URL_, { waitUntil: 'commit', timeout: 45000 });
  await settle(page);

  // Freeze the animated background so the ink plate and the live shot agree.
  const cdp = await ctx.newCDPSession(page);
  let freeze = 'virtual-time';
  try {
    await cdp.send('Emulation.setVirtualTimePolicy', { policy: 'pause' });
  } catch {
    freeze = 'raf-override';
    await page.evaluate(() => { window.requestAnimationFrame = () => 0; });
  }
  await page.evaluate(() => {
    const s = document.createElement('style');
    s.id = '__freeze';
    s.textContent = '*,*::before,*::after{animation-play-state:paused !important;transition:none !important}';
    document.head.appendChild(s);
  });

  const shotA = await page.screenshot({ type: 'png' });
  const shotA2 = await page.screenshot({ type: 'png' });
  await page.addStyleTag({ content: HIDE_INK_CSS });
  const shotB = await page.screenshot({ type: 'png' });

  const A = await raw(shotA), A2 = await raw(shotA2), B = await raw(shotB);
  fs.writeFileSync(path.join(OUT, `contrast-${vp.w}x${vp.h}-live.png`), shotA);
  fs.writeFileSync(path.join(OUT, `contrast-${vp.w}x${vp.h}-plate.png`), shotB);

  // How much did the background drift between two identical captures? (freeze quality)
  let drift = 0;
  for (let i = 0; i < A.data.length; i += 3 * 97) drift = Math.max(drift, Math.abs(A.data[i] - A2.data[i]));

  // A marquee keeps moving between geometry collection and pixel capture, so
  // the rects stop matching where the glyphs actually are and the sampler
  // reads bare background. Freeze all CSS animation first so geometry and
  // pixels describe the same instant.
  await page.addStyleTag({ content: '*, *::before, *::after { animation-play-state: paused !important; }' });
  await page.waitForTimeout(250);
  const runs = await page.evaluate(COLLECT_RUNS);
  await ctx.close();

  const px = (img, x, y) => { const o = (y * img.w + x) * 3; return [img.data[o], img.data[o + 1], img.data[o + 2]]; };
  const rows = [];
  let fails = 0, skips = 0;
  for (const run of runs) {
    const cand = [];
    for (const r of run.rects) {
      const x0 = Math.max(0, Math.floor(r.x)), y0 = Math.max(0, Math.floor(r.y));
      const x1 = Math.min(A.w - 1, Math.ceil(r.x + r.w)), y1 = Math.min(A.h - 1, Math.ceil(r.y + r.h));
      if (x1 <= x0 || y1 <= y0) continue;
      for (let y = y0; y <= y1; y++) for (let x = x0; x <= x1; x++) {
        const a = px(A, x, y), b = px(B, x, y);
        const d = Math.abs(a[0] - b[0]) + Math.abs(a[1] - b[1]) + Math.abs(a[2] - b[2]);
        cand.push({ d, a, b });
      }
    }
    if (!cand.length) { rows.push([run.text, `${run.fontSize}px/${run.fontWeight}`, '-', '-', '-', '-', 'SKIP off-screen']); skips++; continue; }
    const maxD = Math.max(...cand.map(c => c.d));
    if (maxD < 24) { // ink never disappeared: canvas text, bg-clip:text, or an image
      rows.push([run.text, `${run.fontSize}px/${run.fontWeight}`, '-', '-', '-', `d=${maxD}`, 'SKIP ink not isolable']);
      skips++; continue;
    }
    // Glyph core = the pixels that changed most when ink was removed. Taking only
    // the top of that distribution keeps antialiased edge pixels out of the sample.
    cand.sort((p, q) => q.d - p.d);
    const core = cand.filter(c => c.d >= maxD * 0.80);
    const use = (core.length >= 4 ? core : cand.slice(0, 6)).slice(0, 4000);
    const ink = [0, 1, 2].map(k => use.reduce((s, c) => s + c.a[k], 0) / use.length);
    const ratios = use.map(c => contrast(ink, c.b));
    const worst = pct(ratios, 5), med = pct(ratios, 50);
    const need = run.large ? 3 : 4.5;
    const ok = worst >= need;
    if (!ok) fails++;
    const bgMed = [0, 1, 2].map(k => pct(use.map(c => c.b[k]), 50));
    rows.push([
      run.text,
      `${run.fontSize}px/${run.fontWeight}${run.large ? ' L' : ''}`,
      `${hex(ink)} on ${hex(bgMed)}`,
      `${worst.toFixed(2)}:1`,
      `${med.toFixed(2)}:1`,
      `${use.length}px d=${maxD}${maxD < 90 ? ' LOW' : ''}`,
      `${ok ? 'PASS' : 'FAIL'} (needs ${need}:1)`,
    ]);
  }
  return { rows, fails, skips, freeze, drift, runs: runs.length, bag };
}

let contrastFails = 0, contrastSkips = 0, contrastRuns = 0;
await step('6', 'MEASURED WCAG contrast from rendered pixels', async () => {
  for (const vp of CONTRAST_VPS) {
    const r = await contrastPass(vp);
    contrastFails += r.fails; contrastSkips += r.skips; contrastRuns += r.runs;
    section(`6 · measured contrast @ ${vp.w}x${vp.h}  (freeze=${r.freeze}, bg drift between two captures=${r.drift})`,
      ['text run', 'size/weight', 'sampled ink on bg', 'worst(p5)', 'median', 'ink sample', ''], r.rows);
  }
  record('6', 'MEASURED WCAG contrast from rendered pixels', contrastFails ? 'FAIL' : (contrastRuns ? 'PASS' : 'FAIL'),
    contrastRuns ? `${contrastRuns} text runs sampled, ${contrastFails} below threshold, ${contrastSkips} unmeasurable`
                 : 'no text runs found to sample');
});

// ---- 7. animation cost -------------------------------------------------------
await step('7', 'Animation cost (rAF deltas, idle)', async () => {
  await mainPage.evaluate(() => window.scrollTo(0, 0));
  await mainPage.waitForTimeout(500);
  const d = await mainPage.evaluate(ms => new Promise(res => {
    const out = []; let last = performance.now(); const end = last + ms;
    (function f(t) { out.push(t - last); last = t; (t < end) ? requestAnimationFrame(f) : res(out); })(performance.now());
  }), RAF_MS);
  const s = d.slice(2).filter(x => x > 0 && x < 2000);
  const mean = s.reduce((a, b) => a + b, 0) / s.length;
  const p95 = pct(s, 95), p99 = pct(s, 99), max = Math.max(...s);
  const dropped = s.reduce((a, x) => a + Math.max(0, Math.round(x / 16.667) - 1), 0);
  const fps = 1000 / mean;
  section('7 · frame timing', ['metric', 'value'], [
    ['samples', `${s.length} over ${(RAF_MS / 1000).toFixed(0)}s`],
    ['mean frame', `${mean.toFixed(2)} ms  (~${fps.toFixed(1)} fps)`],
    ['p95 frame', `${p95.toFixed(2)} ms`],
    ['p99 frame', `${p99.toFixed(2)} ms`],
    ['max frame', `${max.toFixed(2)} ms`],
    ['dropped @60Hz', `${dropped} frame(s) (${(dropped / s.length * 100).toFixed(1)}% of budget)`],
  ]);
  const ok = p95 <= 22 && dropped / s.length < 0.10;
  record('7', 'Animation cost (rAF deltas, idle)', ok ? 'PASS' : 'WARN',
    `mean ${mean.toFixed(1)}ms, p95 ${p95.toFixed(1)}ms, ${dropped} dropped of ${s.length}`);
});

// ---- 8. prefers-reduced-motion ----------------------------------------------
await step('8', 'prefers-reduced-motion stops the loop', async () => {
  const ctx = await browser.newContext({ viewport: { width: 1288, height: 717 }, deviceScaleFactor: 1, reducedMotion: 'reduce' });
  const page = await ctx.newPage();
  const bag = { console: [], pageerrors: [], netfail: [] };
  watch(page, bag);
  await page.addInitScript(INIT_INSTRUMENT);
  await page.goto(URL_, { waitUntil: 'commit', timeout: 45000 });
  await settle(page);
  await page.waitForTimeout(800);
  const c0 = await page.evaluate(() => window.__rafCount);
  await page.waitForTimeout(RM_MS);
  const c1 = await page.evaluate(() => window.__rafCount);
  const delta = c1 - c0;
  const shot = await page.screenshot({ type: 'png' });
  fs.writeFileSync(path.join(OUT, 'reduced-motion.png'), shot);
  const st = await sharp(shot).stats();
  const stdev = st.channels.reduce((a, c) => a + c.stdev, 0) / st.channels.length;
  const composed = await page.evaluate(() => {
    const em = document.querySelector('#li-em');
    const shell = document.querySelector('.tenant-login-shell');
    const canvases = Array.from(document.querySelectorAll('canvas')).map(c => `${c.width}x${c.height}`);
    return { em: !!(em && em.getBoundingClientRect().height > 0), shell: !!shell, canvases };
  });
  const loopStopped = delta <= 10;
  const renders = composed.em && composed.shell && stdev > 2;
  const mq = await page.evaluate(() => matchMedia('(prefers-reduced-motion: reduce)').matches);
  section('8 · reduced motion', ['metric', 'value'], [
    ['media query matches', String(mq)],
    ['rAF calls in ' + (RM_MS / 1000) + 's', `${delta}  (${loopStopped ? 'loop stopped' : 'STILL ANIMATING'})`],
    ['frame composed', `#li-em ${composed.em ? 'visible' : 'MISSING'}, shell ${composed.shell ? 'present' : 'MISSING'}, pixel stdev ${stdev.toFixed(1)}`],
    ['canvases', composed.canvases.join(', ') || '(none)'],
    ['errors in this pass', `${bag.pageerrors.length} page, ${bag.console.length} console`],
    ['screenshot', path.join(OUT, 'reduced-motion.png')],
  ]);
  record('8', 'prefers-reduced-motion stops the loop', (loopStopped && renders) ? 'PASS' : 'FAIL',
    `${delta} rAF calls in ${RM_MS / 1000}s${loopStopped ? '' : ' (expected <=10)'}; ` +
    `${renders ? 'still renders a composed frame' : 'frame did NOT compose'}`);
  await ctx.close();
});

// ---- 9. WebGL ----------------------------------------------------------------
await step('9', 'WebGL context + graceful fallback', async () => {
  const calls = await mainPage.evaluate(() => window.__ctxCalls || []);
  const gpu = await mainPage.evaluate(() => {
    const c = document.createElement('canvas');
    const gl = c.getContext('webgl2') || c.getContext('webgl');
    if (!gl) return { none: true };
    const ext = gl.getExtension('WEBGL_debug_renderer_info');
    return {
      api: gl instanceof WebGL2RenderingContext ? 'webgl2' : 'webgl',
      unmaskedRenderer: ext ? gl.getParameter(ext.UNMASKED_RENDERER_WEBGL) : '(WEBGL_debug_renderer_info unavailable)',
      unmaskedVendor: ext ? gl.getParameter(ext.UNMASKED_VENDOR_WEBGL) : '',
      renderer: gl.getParameter(gl.RENDERER), vendor: gl.getParameter(gl.VENDOR),
      version: gl.getParameter(gl.VERSION),
    };
  });

  // Fallback pass: WebGL denied before load.
  const ctx = await browser.newContext({ viewport: { width: 1288, height: 717 }, deviceScaleFactor: 1 });
  const page = await ctx.newPage();
  const bag = { console: [], pageerrors: [], netfail: [] };
  watch(page, bag);
  await page.addInitScript(INIT_NO_WEBGL);
  await page.goto(URL_, { waitUntil: 'commit', timeout: 45000 });
  await settle(page);
  const shot = await page.screenshot({ type: 'png' });
  fs.writeFileSync(path.join(OUT, 'no-webgl.png'), shot);
  const st = await sharp(shot).stats();
  const stdev = st.channels.reduce((a, c) => a + c.stdev, 0) / st.channels.length;
  const dom = await page.evaluate(() => {
    const vis = s => { const e = document.querySelector(s); if (!e) return false; const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
    return { em: vis('#li-em'), pw: vis('#li-pw'), ws: vis('#li-ws'), shell: !!document.querySelector('.tenant-login-shell'),
             btn: !!document.querySelector('button[type="submit"]') };
  });
  await ctx.close();

  const fallbackOk = dom.em && dom.pw && dom.ws && dom.shell && dom.btn && bag.pageerrors.length === 0 && stdev > 2;
  section('9 · WebGL', ['metric', 'value'], [
    ['contexts the page asked for', calls.length ? calls.map(c => `${c.type}${c.ok ? ' OK' : ' FAILED'}`).join(', ') : '(page requested none)'],
    ['probe context', gpu.none ? 'none available' : gpu.api],
    ['unmasked renderer', gpu.none ? '-' : String(gpu.unmaskedRenderer)],
    ['unmasked vendor', gpu.none ? '-' : String(gpu.unmaskedVendor)],
    ['GL version', gpu.none ? '-' : String(gpu.version)],
    ['--- WebGL denied ---', ''],
    ['inputs visible', `em ${dom.em}, pw ${dom.pw}, ws ${dom.ws}`],
    ['shell / submit', `${dom.shell} / ${dom.btn}`],
    ['pixel stdev (non-blank)', stdev.toFixed(1)],
    ['errors', `${bag.pageerrors.length} page, ${bag.console.length} console`],
    ['screenshot', path.join(OUT, 'no-webgl.png')],
  ]);
  record('9', 'WebGL context + graceful fallback', fallbackOk ? 'PASS' : 'FAIL',
    `${gpu.none ? 'no context' : gpu.api} / ${gpu.none ? '-' : String(gpu.unmaskedRenderer).slice(0, 48)}; ` +
    `without WebGL: ${fallbackOk ? 'page still usable' : 'DEGRADED'}`);
});

await browser.close();

// ------------------------------------------------------------------- printing
function table(header, rows) {
  const all = header ? [header, ...rows] : rows;
  const cols = Math.max(...all.map(r => r.length));
  const w = [];
  for (let c = 0; c < cols; c++) w[c] = Math.min(62, Math.max(...all.map(r => String(r[c] ?? '').length)));
  const line = r => '  ' + r.map((v, c) => String(v ?? '').slice(0, 62).padEnd(c === cols - 1 ? 0 : w[c])).join('  ').trimEnd();
  const out = [];
  if (header) { out.push(line(header)); out.push('  ' + w.map(x => '-'.repeat(x)).join('  ')); }
  for (const r of rows) out.push(line(r));
  return out.join('\n');
}

for (const s of sections) {
  console.log(`\n${s.title}`);
  console.log(table(s.header, s.rows));
}

console.log('\n================================ SUMMARY ================================');
console.log(table(['', 'check', 'result'], results.map(r => [
  r.status.padEnd(5), `${r.id}. ${r.name}`, r.detail,
])));

const failed = results.filter(r => r.status === 'FAIL' || r.status === 'ERROR');
const warned = results.filter(r => r.status === 'WARN');
console.log(`\n${results.length} checks · ${results.filter(r => r.status === 'PASS').length} PASS · ` +
  `${warned.length} WARN · ${failed.length} FAIL/ERROR · ${((Date.now() - started) / 1000).toFixed(0)}s`);
console.log(`artifacts: ${OUT}`);
if (failed.length) console.log(`FAILING: ${failed.map(f => f.id).join(', ')}`);
process.exit(failed.length ? 1 : 0);
