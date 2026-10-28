import { appendFileSync, chmodSync } from 'node:fs';
import { getConnector } from './connectors/index.js';
import { saveCursors, cursorOf, BRIDGE_VERSION } from './config.js';
import { requestJson, HttpError } from './http.js';
import { signedHeaders } from './signing.js';

const ts = (iso) => Date.parse(iso);
const keyOf = (p) => `${p.employeeCode}|${p.punchTimestampUtc}|${p.punchDirection || ''}`;

function headersFor(device, path, body) {
  return {
    'Content-Type': 'application/json',
    'X-Device-Key': device.deviceKey,
    'X-Bridge-Version': BRIDGE_VERSION,
    ...signedHeaders(device.signingSecret, { method: 'POST', path, body }),
  };
}

function post(cfg, device, path, payload, http) {
  const body = JSON.stringify(payload);
  const base = cfg.apiBaseUrl.replace(/\/$/, '');
  const urlPath = new URL(`${base}${path}`).pathname;     // the path the server sees, and so the path that is signed
  return requestJson(`${base}${path}`,
    { method: 'POST', headers: () => headersFor(device, urlPath, body), body },
    { timeoutMs: cfg.requestTimeoutMs, retries: cfg.maxRetries, ...http });
}

// The dead-letter file holds employee codes, timestamps and server error bodies: owner-only (0600), like the cursor.
export function appendRejected(file, entry) {
  appendFileSync(file, JSON.stringify({ at: new Date().toISOString(), ...entry }) + '\n', { mode: 0o600 });
  try { chmodSync(file, 0o600); } catch { /* best effort on filesystems without modes */ }
}

// Server answers that refuse the WHOLE batch for a reason that is not a bad punch. Bisecting these would drain the entire
// backlog into the dead-letter file while the cursor advances: silent attendance loss. They stop the poll and alert instead.
const BATCH_LEVEL_CODES = new Set(['batch_too_large', 'verification_label_reserved']);
export class BatchRefusedError extends Error {
  constructor(message) { super(message); this.name = 'BatchRefusedError'; }
}
function isBatchLevelRefusal(e) {
  if (e.status === 413) return true;
  try { return BATCH_LEVEL_CODES.has(JSON.parse(e.body ?? '{}').code); } catch { return false; }
}
const MAX_CONSECUTIVE_SINGLE_REFUSALS = 5;

// Forwards one chunk. A chunk the server refuses for a reason that points at a PUNCH (HTTP 400/422 without a batch-level code) is
// bisected until the offending punch is isolated, written to the rejected file and skipped: one poison punch must never wedge a
// backlog. A batch-level refusal (413, batch_too_large, verification_label_reserved) or a run of consecutive single-punch
// refusals stops the poll without advancing the cursor, and surfaces through the heartbeat error.
export async function forwardChunk(cfg, device, punches, http = {}, log = console, guard = { singles: 0 }) {
  try {
    const result = await post(cfg, device, '/api/attendance/ingest', { punches, autoProcess: true }, http);
    guard.singles = 0;
    // Server per-row rejections carry the punch itself so they can be persisted (indexes are relative to this request).
    const rejections = (result.rejections ?? []).map((r) => ({ ...r, punch: punches[r.index] }));
    return { accepted: result.accepted ?? 0, duplicates: result.duplicates ?? 0, unmatched: result.unmatched ?? 0,
      processed: result.processed ?? 0, rejected: result.rejected ?? 0, rejections, skipped: 0 };
  } catch (e) {
    const permanent = e instanceof HttpError && !e.retryable && e.status !== 401 && e.status !== 403;
    if (!permanent) throw e;
    if (isBatchLevelRefusal(e))
      throw new BatchRefusedError(`server refused the whole batch (HTTP ${e.status}): ${String(e.body).slice(0, 200)}. Nothing was skipped; the cursor did not move.`);
    if (punches.length === 1) {
      appendRejected(cfg.rejectedFile, { device: device.name, status: e.status, error: e.body, punch: punches[0] });
      log.error(`[${device.name}] server refused one punch (HTTP ${e.status}); written to ${cfg.rejectedFile} and skipped.`);
      if (++guard.singles >= MAX_CONSECUTIVE_SINGLE_REFUSALS)
        throw new BatchRefusedError(`server refused ${guard.singles} punches in a row (HTTP ${e.status}); stopping instead of skipping the backlog.`);
      return { accepted: 0, duplicates: 0, unmatched: 0, processed: 0, rejected: 0, rejections: [], skipped: 1 };
    }
    const mid = Math.ceil(punches.length / 2);
    const a = await forwardChunk(cfg, device, punches.slice(0, mid), http, log, guard);
    const b = await forwardChunk(cfg, device, punches.slice(mid), http, log, guard);
    return Object.fromEntries(Object.keys(a).map((k) => [k, Array.isArray(a[k]) ? [...a[k], ...b[k]] : a[k] + b[k]]));
  }
}

export async function sendHeartbeat(cfg, device, info, http = {}) {
  try {
    await post(cfg, device, '/api/attendance/heartbeat', {
      bridgeVersion: BRIDGE_VERSION, deviceTimeZone: device.timeZone ?? null, hostTimeUtc: new Date().toISOString(), ...info,
    }, { ...http, retries: 1 });
  } catch (e) {
    console.warn(`[${device.name}] heartbeat failed: ${e.message}`);
  }
}

export async function pollDevice(cfg, cursors, device, http = {}, log = console) {
  const tag = `[${device.name}]`;
  if (!device.deviceKey || device.deviceKey.includes('REPLACE')) {
    log.warn(`${tag} skipped — deviceKey not set.`);
    return { status: 'skipped' };
  }
  const connector = getConnector(device.connector);
  const cursor = cursorOf(cursors, device.name);
  // Re-read a small overlap before the cursor: a punch that reaches the device late with an earlier timestamp is
  // not skipped forever, and the server de-duplicates what we send twice.
  const since = cursor ? new Date(ts(cursor) - cfg.overlapSeconds * 1000).toISOString() : undefined;
  // Punches inside the overlap window that were already forwarded are dropped here (the cursor file remembers them),
  // so an idle device does not re-send five minutes of history on every poll.
  const recent = new Set(cursors[device.name]?.recent ?? []);
  const all = (await connector.pull(device, since, { log }))
    .filter((p) => p && p.punchTimestampUtc && !Number.isNaN(ts(p.punchTimestampUtc)))
    .filter((p) => !(cursor && ts(p.punchTimestampUtc) <= ts(cursor) && recent.has(keyOf(p))))
    .sort((a, b) => ts(a.punchTimestampUtc) - ts(b.punchTimestampUtc));

  if (!all.length) {
    log.log(`${tag} no new punches.`);
    await sendHeartbeat(cfg, device, { backlog: 0, lastPunchUtc: cursor }, http);
    return { status: 'idle', sent: 0 };
  }

  let sent = 0, last = cursor;
  const totals = { accepted: 0, duplicates: 0, unmatched: 0, processed: 0, rejected: 0, skipped: 0 };
  const budget = cfg.maxChunksPerPoll * cfg.chunkSize;
  const guard = { singles: 0 };
  for (let i = 0; i < all.length && i < budget; i += cfg.chunkSize) {
    const chunk = all.slice(i, i + cfg.chunkSize);
    const r = await forwardChunk(cfg, device, chunk, http, log, guard);     // throws on a transient failure: cursor stays put
    for (const k of Object.keys(totals)) totals[k] += r[k] ?? 0;
    for (const rej of r.rejections ?? []) {
      log.warn(`${tag} server rejected punch: ${rej.reason}`);
      appendRejected(cfg.rejectedFile, { device: device.name, status: 200, error: rej.reason, punch: rej.punch });   // durable: a wrong device clock must not lose punches silently
    }
    sent += chunk.length;
    last = chunk[chunk.length - 1].punchTimestampUtc;
    // The cursor moves per chunk, so an interrupted run resumes where it stopped instead of re-sending everything.
    for (const p of chunk) recent.add(keyOf(p));
    // File connectors reread an unarchived file and may ignore `since`. Keep every forwarded key while a bounded
    // backlog is still in flight; pruning to the overlap window here would make old rows reappear ahead of the
    // unforwarded tail on the next poll and the cursor would oscillate forever.
    cursors[device.name] = { lastPunchUtc: last, recent: [...recent], updatedAtUtc: new Date().toISOString() };
    saveCursors(cfg.cursorFile, cursors);
  }
  const backlog = all.length - sent;
  // Files a connector deferred archiving for are archived only now that every chunk was forwarded (L4).
  if (!backlog) {
    await connector.commit?.(device);
    const floor = ts(last) - cfg.overlapSeconds * 1000;
    const keep = [...recent].filter((k) => ts(k.split('|')[1]) >= floor);
    cursors[device.name] = { lastPunchUtc: last, recent: keep, updatedAtUtc: new Date().toISOString() };
    saveCursors(cfg.cursorFile, cursors);
  }
  log.log(`${tag} forwarded ${sent}/${all.length} → accepted=${totals.accepted} dup=${totals.duplicates} unmatched=${totals.unmatched} rejected=${totals.rejected} skipped=${totals.skipped} processed=${totals.processed}${backlog ? ` · ${backlog} left for the next poll` : ''}`);
  await sendHeartbeat(cfg, device, { backlog, lastPunchUtc: last }, http);
  return { status: backlog ? 'partial' : 'ok', sent, backlog, totals };
}

export async function pollOnce(cfg, cursors, http = {}, log = console) {
  for (const device of cfg.devices) {
    try { await pollDevice(cfg, cursors, device, http, log); }
    catch (e) {
      log.error(`[${device.name}] ERROR: ${e.message}`);
      await sendHeartbeat(cfg, device, { error: String(e.message).slice(0, 300) }, { ...http });
    }
  }
}
