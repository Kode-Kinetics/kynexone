import { test } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { mkdtempSync, writeFileSync, chmodSync, readFileSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createHmac } from 'node:crypto';
import { requestJson, backoffMs, HttpError } from '../src/http.js';
import { signBody, signedHeaders } from '../src/signing.js';
import { wallClockToUtcIso, hostLocalDateToUtcIso, parseDeviceTimestamp } from '../src/tz.js';
import { validateConfig, permissionWarnings, cursorOf, saveCursors, loadCursors } from '../src/config.js';
import { pollDevice, forwardChunk } from '../src/poller.js';

const quiet = { log() {}, warn() {}, error() {} };
const noSleep = { sleep: async () => {}, random: () => 0.5 };

function server(handler) {
  return new Promise((resolve) => {
    const calls = [];
    const s = http.createServer(async (req, res) => {
      let body = ''; for await (const c of req) body += c;
      const call = { url: req.url, headers: req.headers, body: body ? JSON.parse(body) : null, raw: body };
      calls.push(call);
      await handler(call, res, calls);
    });
    s.listen(0, '127.0.0.1', () => resolve({ s, calls, url: `http://127.0.0.1:${s.address().port}` }));
  });
}
const json = (res, status, obj, headers = {}) => { res.writeHead(status, { 'content-type': 'application/json', ...headers }); res.end(JSON.stringify(obj)); };
const ingestAck = (received, over = {}) => ({
  received, accepted: received, duplicates: 0, unmatched: 0, processed: 0,
  syncLogId: '00000000-0000-0000-0000-000000000001', rejected: 0, rejections: [], ...over,
});

function cfgFor(url, over = {}) {
  const dir = mkdtempSync(join(tmpdir(), 'kb-'));
  return { apiBaseUrl: url, chunkSize: 3, maxChunksPerPoll: 100, overlapSeconds: 300, requestTimeoutMs: 500, maxRetries: 3,
    cursorFile: join(dir, 'cursor.json'), rejectedFile: join(dir, 'rejected.jsonl'), devices: [], ...over };
}
const dev = (over = {}) => ({ name: 'D1', connector: 'fake', deviceKey: 'knx_test', ...over });
const punch = (i) => ({ employeeCode: 'E1', punchTimestampUtc: new Date(Date.UTC(2026, 0, 1, 0, i)).toISOString(), punchDirection: 'In' });

// the poller resolves connectors by name; register a fake one
import * as registry from '../src/connectors/index.js';
let fakePunches = [];
registry.registerConnector?.('fake', { pull: async () => fakePunches });

// ── http ────────────────────────────────────────────────────────────────────────────────────────
test('requestJson times out a hung connection instead of stalling forever', async () => {
  const { s, url } = await server(() => { /* never answer */ });
  const t0 = Date.now();
  await assert.rejects(requestJson(url, { method: 'POST', body: '{}' }, { timeoutMs: 80, retries: 1, ...noSleep }), /timed out/);
  assert.ok(Date.now() - t0 < 2000);
  s.closeAllConnections(); s.close();
});

test('requestJson retries 503 then succeeds, honouring the attempt budget', async () => {
  let n = 0;
  const { s, url, calls } = await server((_c, res) => (++n < 3 ? json(res, 503, {}) : json(res, 200, { ok: 1 })));
  assert.deepEqual(await requestJson(url, { method: 'POST', body: '{}' }, { timeoutMs: 500, retries: 4, ...noSleep }), { ok: 1 });
  assert.equal(calls.length, 3);
  s.close();
});

test('requestJson does not retry a 400', async () => {
  const { s, url, calls } = await server((_c, res) => json(res, 400, { message: 'bad' }));
  await assert.rejects(requestJson(url, { method: 'POST', body: '{}' }, { retries: 4, ...noSleep }), (e) => e instanceof HttpError && e.status === 400 && !e.retryable);
  assert.equal(calls.length, 1);
  s.close();
});

test('requestJson honours Retry-After on 429', async () => {
  let n = 0; const waits = [];
  const { s, url } = await server((_c, res) => (++n < 2 ? json(res, 429, {}, { 'retry-after': '7' }) : json(res, 200, {})));
  await requestJson(url, { method: 'POST', body: '{}' }, { retries: 2, sleep: async (ms) => waits.push(ms) });
  assert.deepEqual(waits, [7000]);
  s.close();
});

test('backoff grows exponentially, is capped and jittered', () => {
  assert.equal(backoffMs(0, { random: () => 1 }), 500);
  assert.equal(backoffMs(3, { random: () => 1 }), 4000);
  assert.equal(backoffMs(20, { random: () => 1, maxDelayMs: 30000 }), 30000);
  assert.equal(backoffMs(3, { random: () => 0 }), 0);
});

// ── signing ─────────────────────────────────────────────────────────────────────────────────────
test('signature is HMAC-SHA256 over the v2 canonical string and absent without a secret', () => {
  const body = '{"a":1}';
  const sig = signBody('s3cret', 1700000000, 'nonce-1', 'POST', '/api/attendance/ingest', body);
  // cross-language vector, pinned in DeviceIngestHardeningTests too
  assert.equal(sig, 'v2=19ddc14db30ec8263d6a0412e149558b8b196282deee0d5036a040c8fd4c0ebd');
  assert.deepEqual(signedHeaders(undefined, { path: '/x', body: '{}' }), {});
  const h = signedHeaders('s3cret', { path: '/api/attendance/ingest', body, nowMs: 1700000000_500, nonce: 'nonce-1' });
  assert.equal(h['X-Device-Timestamp'], '1700000000');
  assert.equal(h['X-Device-Nonce'], 'nonce-1');
  assert.equal(h['X-Device-Signature'], sig);
  assert.notEqual(signBody('s3cret', 1700000000, 'nonce-1', 'POST', '/api/attendance/heartbeat', body), sig, 'path is bound');
});

test('Retry-After is capped by maxDelayMs', async () => {
  let n = 0; const waits = [];
  const { s, url } = await server((_c, res) => (++n < 2 ? json(res, 429, {}, { 'retry-after': '86400' }) : json(res, 200, {})));
  await requestJson(url, { method: 'POST', body: '{}' }, { retries: 2, maxDelayMs: 30000, sleep: async (ms) => waits.push(ms) });
  assert.deepEqual(waits, [30000]);
  s.close();
});

// ── time zone ───────────────────────────────────────────────────────────────────────────────────
test('device wall clock is read in the device zone, not the host zone', () => {
  assert.equal(wallClockToUtcIso({ year: 2026, month: 7, day: 1, hour: 9, minute: 0 }, 'Asia/Riyadh'), '2026-07-01T06:00:00.000Z');
  assert.equal(wallClockToUtcIso({ year: 2026, month: 1, day: 1, hour: 9, minute: 0 }, 'America/New_York'), '2026-01-01T14:00:00.000Z');
  assert.equal(wallClockToUtcIso({ year: 2026, month: 7, day: 1, hour: 9, minute: 0 }, 'America/New_York'), '2026-07-01T13:00:00.000Z');
  // a Date whose HOST-local fields are 09:00 (whatever the host zone is)
  const d = new Date(2026, 6, 1, 9, 0, 0);
  assert.equal(hostLocalDateToUtcIso(d, 'Asia/Riyadh'), '2026-07-01T06:00:00.000Z');
});

test('parseDeviceTimestamp: explicit offset wins, bare time needs a zone', () => {
  assert.equal(parseDeviceTimestamp('2026-07-01T09:00:00+03:00', undefined), '2026-07-01T06:00:00.000Z');
  assert.equal(parseDeviceTimestamp('2026-07-01 09:00:00', 'Asia/Riyadh'), '2026-07-01T06:00:00.000Z');
  assert.equal(parseDeviceTimestamp('2026-07-01 09:00:00', undefined), null);
  assert.equal(parseDeviceTimestamp('garbage', 'Asia/Riyadh'), null);
});

// ── config ──────────────────────────────────────────────────────────────────────────────────────
test('config: https required, http only with the explicit flag', () => {
  const base = { devices: [{ name: 'a', connector: 'mock', deviceKey: 'k' }] };
  assert.match(validateConfig({ ...base, apiBaseUrl: 'http://x.example' }, {}).errors[0], /https/);
  const ok = validateConfig({ ...base, apiBaseUrl: 'http://localhost:5117', allowInsecureHttp: true }, {});
  assert.equal(ok.errors.length, 0);
  assert.ok(ok.warnings.some((w) => /unencrypted/.test(w)));
  assert.equal(validateConfig({ ...base, apiBaseUrl: 'https://api.example' }, {}).errors.length, 0);
});

test('config: zkteco needs an IANA timeZone; bad zone refused; chunk size capped at the server limit', () => {
  const r = validateConfig({ apiBaseUrl: 'https://a', chunkSize: 9999, devices: [{ name: 'z', connector: 'zkteco', deviceKey: 'k' }] }, {});
  assert.ok(r.errors.some((e) => /timeZone/.test(e)));
  assert.equal(r.cfg.chunkSize, 500);
  assert.ok(validateConfig({ apiBaseUrl: 'https://a', devices: [{ name: 'z', connector: 'mock', timeZone: 'Mars/Base' }] }, {}).errors.some((e) => /IANA/.test(e)));
});

test('config: device key and signing secret can come from the environment', () => {
  const r = validateConfig({ apiBaseUrl: 'https://a', devices: [{ name: 'Main Door', connector: 'mock' }] },
    { KYNEX_DEVICE_KEY_MAIN_DOOR: 'knx_env', KYNEX_DEVICE_SECRET_MAIN_DOOR: 'sec' });
  assert.equal(r.cfg.devices[0].deviceKey, 'knx_env');
  assert.equal(r.cfg.devices[0].signingSecret, 'sec');
});

test('config: warns when the key file is world/group readable', { skip: process.platform === 'win32' }, () => {
  const f = join(mkdtempSync(join(tmpdir(), 'kb-')), 'config.json');
  writeFileSync(f, '{}'); chmodSync(f, 0o644);
  assert.match(permissionWarnings(f)[0], /chmod 600/);
  chmodSync(f, 0o600);
  assert.deepEqual(permissionWarnings(f), []);
});

test('cursor file is written 0600 and legacy string cursors still read', { skip: process.platform === 'win32' }, () => {
  const f = join(mkdtempSync(join(tmpdir(), 'kb-')), 'c.json');
  saveCursors(f, { A: { lastPunchUtc: '2026-01-01T00:00:00.000Z' } });
  assert.equal((await_stat(f) & 0o077), 0);
  assert.equal(cursorOf(loadCursors(f), 'A'), '2026-01-01T00:00:00.000Z');
  assert.equal(cursorOf({ B: '2025-05-05T00:00:00.000Z' }, 'B'), '2025-05-05T00:00:00.000Z');
  assert.equal(cursorOf({}, 'C'), null);
});
import { statSync } from 'node:fs';
function await_stat(f) { return statSync(f).mode & 0o777; }

// ── poller ──────────────────────────────────────────────────────────────────────────────────────
test('a big backlog is sent in bounded chunks, cursor advances per chunk, heartbeat reports', async () => {
  const { s, url, calls } = await server((c, res) => (c.url.endsWith('/ingest')
    ? json(res, 200, ingestAck(c.body.punches.length)) : json(res, 200, {})));
  const cfg = cfgFor(url, { chunkSize: 3 });
  fakePunches = [1, 2, 3, 4, 5, 6, 7].map(punch);
  const cursors = {};
  const r = await pollDevice(cfg, cursors, dev(), noSleep, quiet);
  const ingest = calls.filter((c) => c.url.endsWith('/ingest'));
  assert.deepEqual(ingest.map((c) => c.body.punches.length), [3, 3, 1]);
  assert.equal(r.status, 'ok');
  assert.equal(cursorOf(cursors, 'D1'), punch(7).punchTimestampUtc);
  assert.ok(calls.some((c) => c.url.endsWith('/heartbeat') && c.body.bridgeVersion));
  assert.equal(ingest[0].headers['x-device-key'], 'knx_test');
  assert.ok(ingest[0].headers['x-bridge-version']);
  s.close();
});

test('resume: a failure mid-backlog keeps the cursor at the last good chunk, the next poll continues', async () => {
  let fail = true;
  const { s, url, calls } = await server((c, res) => {
    if (c.url.endsWith('/heartbeat')) return json(res, 200, {});
    if (fail && c.body.punches[0].punchTimestampUtc === punch(4).punchTimestampUtc) return json(res, 503, {});
    json(res, 200, ingestAck(c.body.punches.length));
  });
  const cfg = cfgFor(url, { chunkSize: 3, maxRetries: 1 });
  fakePunches = [1, 2, 3, 4, 5, 6].map(punch);
  const cursors = {};
  await assert.rejects(pollDevice(cfg, cursors, dev(), noSleep, quiet), /503/);
  assert.equal(cursorOf(cursors, 'D1'), punch(3).punchTimestampUtc, 'chunk 1 is committed, chunk 2 is not');
  assert.equal(cursorOf(loadCursors(cfg.cursorFile), 'D1'), punch(3).punchTimestampUtc, 'persisted');

  fail = false; calls.length = 0;
  await pollDevice(cfg, cursors, dev(), noSleep, quiet);
  const sent = calls.filter((c) => c.url.endsWith('/ingest')).flatMap((c) => c.body.punches.map((p) => p.punchTimestampUtc));
  assert.ok(sent.includes(punch(4).punchTimestampUtc) && sent.includes(punch(6).punchTimestampUtc));
  assert.equal(cursorOf(cursors, 'D1'), punch(6).punchTimestampUtc);
  s.close();
});

test('maxChunksPerPoll bounds one poll and reports the remaining backlog', async () => {
  const { s, url, calls } = await server((c, res) => json(res, 200, c.url.endsWith('/ingest') ? ingestAck(c.body.punches.length) : {}));
  const cfg = cfgFor(url, { chunkSize: 2, maxChunksPerPoll: 2 });
  fakePunches = [1, 2, 3, 4, 5, 6, 7].map(punch);
  const cursors = {};
  const r = await pollDevice(cfg, cursors, dev(), noSleep, quiet);
  assert.equal(r.status, 'partial');
  assert.equal(r.backlog, 3);
  assert.equal(calls.filter((c) => c.url.endsWith('/ingest')).length, 2);
  assert.equal(calls.find((c) => c.url.endsWith('/heartbeat')).body.backlog, 3);
  s.close();
});

test('a file connector backlog keeps its full progress until every deferred row is forwarded', async () => {
  const { s, url, calls } = await server((c, res) => json(res, 200, c.url.endsWith('/ingest') ? ingestAck(c.body.punches.length) : {}));
  const cfg = cfgFor(url, { chunkSize: 2, maxChunksPerPoll: 1, overlapSeconds: 300 });
  const hourly = (hour) => ({
    employeeCode: 'E1',
    punchTimestampUtc: new Date(Date.UTC(2026, 0, 1, hour)).toISOString(),
    punchDirection: 'In',
  });
  // The CSV connector deliberately ignores `since` and rereads an unarchived file until commit().
  fakePunches = [0, 1, 2, 3, 4, 5].map(hourly);
  const cursors = {};

  for (let poll = 0; poll < 3; poll++) await pollDevice(cfg, cursors, dev(), noSleep, quiet);

  const sent = calls
    .filter((c) => c.url.endsWith('/ingest'))
    .flatMap((c) => c.body.punches.map((p) => p.punchTimestampUtc));
  assert.deepEqual(sent, [0, 1, 2, 3, 4, 5].map((hour) => hourly(hour).punchTimestampUtc));
  assert.equal(cursorOf(cursors, 'D1'), hourly(5).punchTimestampUtc);
  s.close();
});

test('a poison punch is isolated by bisection, dead-lettered and skipped — the backlog is not wedged', async () => {
  const bad = punch(3).punchTimestampUtc;
  const { s, url } = await server((c, res) => {
    if (c.url.endsWith('/heartbeat')) return json(res, 200, {});
    if (c.body.punches.some((p) => p.punchTimestampUtc === bad)) return json(res, 400, { message: 'bad punch' });
    json(res, 200, ingestAck(c.body.punches.length));
  });
  const cfg = cfgFor(url, { chunkSize: 4 });
  fakePunches = [1, 2, 3, 4].map(punch);
  const cursors = {};
  await pollDevice(cfg, cursors, dev(), noSleep, quiet);
  assert.equal(cursorOf(cursors, 'D1'), punch(4).punchTimestampUtc);
  const dead = readFileSync(cfg.rejectedFile, 'utf8').trim().split('\n').map((l) => JSON.parse(l));
  assert.equal(dead.length, 1);
  assert.equal(dead[0].punch.punchTimestampUtc, bad);
  s.close();
});

test('a batch-level refusal stops the poll: nothing is bisected, skipped or dead-lettered, and the cursor stays put', async () => {
  const { s, url } = await server((c, res) => (c.url.endsWith('/heartbeat') ? json(res, 200, {}) : json(res, 400, { code: 'verification_label_reserved' })));
  const cfg = cfgFor(url, { chunkSize: 4 });
  fakePunches = [1, 2, 3, 4].map(punch);
  const cursors = {};
  await assert.rejects(pollDevice(cfg, cursors, dev(), noSleep, quiet), /refused the whole batch/);
  assert.equal(cursorOf(cursors, 'D1'), null);
  assert.equal(existsSync(cfg.rejectedFile), false);
  s.close();
});

for (const status of [404, 405]) {
  test(`HTTP ${status} is an endpoint failure, not a poison punch: no cursor or dead-letter progress`, async (t) => {
    const { s, url, calls } = await server((_c, res) => json(res, status, { message: 'wrong attendance route' }));
    t.after(() => { s.closeAllConnections(); s.close(); });
    const cfg = cfgFor(url, { chunkSize: 2, maxRetries: 0 });
    fakePunches = [1, 2].map(punch);
    const cursors = {};

    await assert.rejects(pollDevice(cfg, cursors, dev(), noSleep, quiet), new RegExp(String(status)));

    assert.equal(calls.length, 1, 'the whole batch is not bisected');
    assert.equal(cursorOf(cursors, 'D1'), null);
    assert.equal(existsSync(cfg.rejectedFile), false);
  });
}

test('malformed JSON in a successful ingest response does not advance the cursor', async (t) => {
  const { s, url } = await server((_c, res) => {
    res.writeHead(200, { 'content-type': 'text/html' });
    res.end('<html>maintenance</html>');
  });
  t.after(() => { s.closeAllConnections(); s.close(); });
  const cfg = cfgFor(url, { chunkSize: 2, maxRetries: 0 });
  fakePunches = [1, 2].map(punch);
  const cursors = {};

  await assert.rejects(pollDevice(cfg, cursors, dev(), noSleep, quiet), /valid JSON/);

  assert.equal(cursorOf(cursors, 'D1'), null);
  assert.equal(existsSync(cfg.rejectedFile), false);
});

test('an incomplete ingest acknowledgement does not advance the cursor', async (t) => {
  const { s, url } = await server((_c, res) => json(res, 200, {}));
  t.after(() => { s.closeAllConnections(); s.close(); });
  const cfg = cfgFor(url, { chunkSize: 2, maxRetries: 0 });
  fakePunches = [1, 2].map(punch);
  const cursors = {};

  await assert.rejects(pollDevice(cfg, cursors, dev(), noSleep, quiet), /ingest acknowledgement/);

  assert.equal(cursorOf(cursors, 'D1'), null);
  assert.equal(existsSync(cfg.rejectedFile), false);
});

for (const [name, acknowledgement] of [
  ['unaccounted punches', ingestAck(2, { accepted: 0 })],
  ['missing rejection detail', ingestAck(2, { accepted: 1, rejected: 1, rejections: [] })],
  ['duplicate rejection indexes', ingestAck(2, { accepted: 0, rejected: 2, rejections: [{ index: 0 }, { index: 0 }] })],
  ['out-of-range rejection index', ingestAck(2, { accepted: 1, rejected: 1, rejections: [{ index: 2 }] })],
  ['missing rejection reason', ingestAck(2, { accepted: 1, rejected: 1, rejections: [{ index: 1 }] })],
  ['blank rejection reason', ingestAck(2, { accepted: 1, rejected: 1, rejections: [{ index: 1, reason: '   ' }] })],
  ['processed count exceeds matched accepted punches', ingestAck(2, { unmatched: 1, processed: 2 })],
  ['invalid sync log id', ingestAck(2, { syncLogId: 'not-a-uuid' })],
]) {
  test(`a contradictory ingest acknowledgement (${name}) does not advance the cursor`, async (t) => {
    const { s, url } = await server((_c, res) => json(res, 200, acknowledgement));
    t.after(() => { s.closeAllConnections(); s.close(); });
    const cfg = cfgFor(url, { chunkSize: 2, maxRetries: 0 });
    fakePunches = [1, 2].map(punch);
    const cursors = {};

    await assert.rejects(pollDevice(cfg, cursors, dev(), noSleep, quiet), /ingest acknowledgement/);

    assert.equal(cursorOf(cursors, 'D1'), null);
    assert.equal(existsSync(cfg.rejectedFile), false);
  });
}

test('a run of single-punch refusals stops instead of draining the backlog', async () => {
  const { s, url } = await server((c, res) => (c.url.endsWith('/heartbeat') ? json(res, 200, {}) : json(res, 400, { message: 'nope' })));
  const cfg = cfgFor(url, { chunkSize: 8 });
  fakePunches = [1, 2, 3, 4, 5, 6, 7, 8].map(punch);
  await assert.rejects(pollDevice(cfg, {}, dev(), noSleep, quiet), /in a row/);
  s.close();
});

test('server per-row rejections are persisted to the dead-letter file (0600)', async () => {
  const { s, url } = await server((c, res) => (c.url.endsWith('/heartbeat') ? json(res, 200, {})
    : json(res, 200, ingestAck(2, { accepted: 1, rejected: 1, rejections: [{ index: 1, reason: 'timestamp_in_future' }] }))));
  const cfg = cfgFor(url, { chunkSize: 2 });
  fakePunches = [1, 2].map(punch);
  await pollDevice(cfg, {}, dev(), noSleep, quiet);
  const dead = readFileSync(cfg.rejectedFile, 'utf8').trim().split('\n').map((l) => JSON.parse(l));
  assert.equal(dead.length, 1);
  assert.equal(dead[0].error, 'timestamp_in_future');
  assert.equal(dead[0].punch.punchTimestampUtc, punch(2).punchTimestampUtc);
  assert.equal(statSync(cfg.rejectedFile).mode & 0o777, 0o600);
  s.close();
});

test('401 is not retried or bisected: it surfaces so the operator fixes the key', async () => {
  const { s, url, calls } = await server((_c, res) => json(res, 401, { message: 'Invalid or inactive device key.' }));
  const cfg = cfgFor(url);
  await assert.rejects(forwardChunk(cfg, dev(), [punch(1), punch(2)], noSleep, quiet), /401/);
  assert.equal(calls.length, 1);
  s.close();
});

test('already-sent punches inside the overlap window are not re-sent on the next poll', async () => {
  const { s, url, calls } = await server((c, res) => json(res, 200, c.url.endsWith('/ingest') ? ingestAck(c.body.punches.length) : {}));
  const cfg = cfgFor(url, { chunkSize: 10 });
  fakePunches = [1, 2, 3].map(punch);
  const cursors = {};
  await pollDevice(cfg, cursors, dev(), noSleep, quiet);
  calls.length = 0;
  fakePunches = [1, 2, 3, 4].map(punch);   // device still reports the old ones (overlap read) + one new
  await pollDevice(cfg, cursors, dev(), noSleep, quiet);
  const ingest = calls.filter((c) => c.url.endsWith('/ingest'));
  assert.equal(ingest.length, 1);
  assert.deepEqual(ingest[0].body.punches.map((p) => p.punchTimestampUtc), [punch(4).punchTimestampUtc]);
  s.close();
});

test('signing headers are sent when the device has a secret', async () => {
  const { s, url, calls } = await server((c, res) => json(res, 200, c.url.endsWith('/ingest') ? ingestAck(1) : {}));
  const cfg = cfgFor(url);
  fakePunches = [punch(1)];
  await pollDevice(cfg, {}, dev({ signingSecret: 'sec' }), noSleep, quiet);
  const c = calls.find((x) => x.url.endsWith('/ingest'));
  const ts = c.headers['x-device-timestamp'];
  assert.equal(c.headers['x-device-signature'], signBody('sec', Number(ts), c.headers['x-device-nonce'], 'POST', '/api/attendance/ingest', c.raw));
  s.close();
});

// ── zkteco connector: loud failure + device-zone mapping ────────────────────────────────────────
import * as zk from '../src/connectors/zkteco.js';

test('zkteco fails loudly without a timeZone, an ip, or the SDK', async () => {
  await assert.rejects(zk.pull({ name: 'z', zkteco: { ip: '1.2.3.4' } }), /timeZone/);
  await assert.rejects(zk.pull({ name: 'z', timeZone: 'Asia/Riyadh', zkteco: {} }), /zkteco\.ip/);
  await assert.rejects(zk.pull({ name: 'z', timeZone: 'Asia/Riyadh', zkteco: { ip: '1.2.3.4' } }, undefined,
    { loadSdk: async () => { throw new Error('not installed'); } }), /npm install node-zklib/);
});

test('zkteco maps device wall-clock to UTC via the device zone and filters by cursor', async () => {
  class FakeZK {
    async createSocket() {}
    async getAttendances() { return { data: [
      { deviceUserId: 7, recordTime: new Date(2026, 6, 1, 9, 0, 0) },
      { deviceUserId: 8, recordTime: new Date(2026, 6, 1, 17, 0, 0) } ] }; }
    async disconnect() {}
  }
  const punches = await zk.pull({ name: 'z', timeZone: 'Asia/Riyadh', zkteco: { ip: '1.2.3.4' } },
    '2026-07-01T07:00:00.000Z', { loadSdk: async () => ({ default: FakeZK }) });
  assert.deepEqual(punches.map((p) => [p.employeeCode, p.punchTimestampUtc]), [['8', '2026-07-01T14:00:00.000Z']]);
});

test('zkteco connection failure throws (so the cursor never advances and the heartbeat reports it)', async () => {
  class Down { async createSocket() { throw new Error('ETIMEDOUT'); } async disconnect() {} }
  await assert.rejects(zk.pull({ name: 'z', timeZone: 'Asia/Riyadh', zkteco: { ip: '1.2.3.4' } }, undefined,
    { loadSdk: async () => ({ default: Down }) }), /ETIMEDOUT/);
});
