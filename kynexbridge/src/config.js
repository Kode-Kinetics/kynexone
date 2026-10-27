import { readFileSync, existsSync, writeFileSync, statSync, chmodSync, renameSync } from 'node:fs';
import { assertTimeZone } from './tz.js';

export const BRIDGE_VERSION = '0.2.0';
export const MAX_CHUNK = 500;            // the server rejects a batch above this
export const DEFAULT_CHUNK = 300;

// Pure validation so it can be tested without exiting the process. Returns { cfg, errors, warnings }.
export function validateConfig(raw, env = process.env) {
  const errors = [];
  const warnings = [];
  const cfg = { ...raw };
  cfg.apiBaseUrl = env.KYNEX_API_URL || cfg.apiBaseUrl;
  cfg.pollIntervalSeconds = Number(env.KYNEX_POLL_SECONDS || cfg.pollIntervalSeconds || 60);
  cfg.cursorFile = cfg.cursorFile || './.cursor.json';
  cfg.allowInsecureHttp = cfg.allowInsecureHttp === true || env.KYNEX_ALLOW_INSECURE_HTTP === '1';
  cfg.chunkSize = Math.min(MAX_CHUNK, Math.max(1, Number(cfg.chunkSize || DEFAULT_CHUNK)));
  cfg.maxChunksPerPoll = Math.max(1, Number(cfg.maxChunksPerPoll || 40));
  cfg.overlapSeconds = Math.max(0, Number(cfg.overlapSeconds ?? 300));
  cfg.requestTimeoutMs = Number(cfg.requestTimeoutMs || 20_000);
  cfg.maxRetries = Number(cfg.maxRetries ?? 4);
  cfg.rejectedFile = cfg.rejectedFile || './rejected.jsonl';

  if (!cfg.apiBaseUrl) errors.push('apiBaseUrl is required.');
  else {
    let url;
    try { url = new URL(cfg.apiBaseUrl); } catch { errors.push(`apiBaseUrl "${cfg.apiBaseUrl}" is not a valid URL.`); }
    if (url && url.protocol !== 'https:') {
      if (url.protocol === 'http:' && cfg.allowInsecureHttp)
        warnings.push('apiBaseUrl is plain HTTP and allowInsecureHttp is set: device keys and punches cross the network unencrypted. Use only on a trusted test network.');
      else errors.push(`apiBaseUrl must be https:// (got ${url.protocol}//). Device keys travel in a header; plain HTTP exposes them. For a local test server set "allowInsecureHttp": true (or KYNEX_ALLOW_INSECURE_HTTP=1).`);
    }
  }
  if (!Array.isArray(cfg.devices) || cfg.devices.length === 0) errors.push('No devices configured.');
  else {
    const names = new Set();
    cfg.devices = cfg.devices.map((d) => {
      const dev = { ...d };
      if (!dev.name) errors.push('Every device needs a "name".');
      else if (names.has(dev.name)) errors.push(`Duplicate device name "${dev.name}" (the cursor is keyed by name).`);
      else names.add(dev.name);
      const envName = `KYNEX_DEVICE_KEY_${String(dev.name || '').toUpperCase().replace(/[^A-Z0-9]+/g, '_')}`;
      dev.deviceKey = env[envName] || dev.deviceKey;
      dev.signingSecret = env[`${envName.replace('KEY', 'SECRET')}`] || dev.signingSecret;
      if (dev.timeZone) { try { assertTimeZone(dev.timeZone); } catch (e) { errors.push(`[${dev.name}] ${e.message}`); } }
      if (dev.connector === 'zkteco' && !dev.timeZone)
        errors.push(`[${dev.name}] "timeZone" (IANA, e.g. "Asia/Riyadh") is required for the zkteco connector: the device reports local wall-clock time, and the bridge host's zone is not the device's.`);
      return dev;
    });
  }
  return { cfg, errors, warnings };
}

// A config file that holds device keys must not be readable by other local users.
export function permissionWarnings(path, platform = process.platform) {
  if (platform === 'win32') return [];
  try {
    const mode = statSync(path).mode & 0o777;
    if (mode & 0o077)
      return [`${path} is readable by other users (mode ${mode.toString(8)}) and contains device keys. Run: chmod 600 ${path}  (or supply keys via KYNEX_DEVICE_KEY_<DEVICE_NAME> environment variables).`];
  } catch { /* unreadable: loadConfig reports it */ }
  return [];
}

export function loadConfig() {
  const path = process.env.KYNEXBRIDGE_CONFIG || './config.json';
  if (!existsSync(path)) {
    console.error(`[config] No config found at ${path}. Copy config.example.json → config.json.`);
    process.exit(1);
  }
  const { cfg, errors, warnings } = validateConfig(JSON.parse(readFileSync(path, 'utf8')));
  for (const w of [...permissionWarnings(path), ...warnings]) console.warn(`[config] WARNING: ${w}`);
  if (errors.length) {
    for (const e of errors) console.error(`[config] ${e}`);
    process.exit(1);
  }
  return cfg;
}

// Per-device cursor. Legacy files stored a bare ISO string per device; both shapes are read.
export function loadCursors(file) {
  try { return existsSync(file) ? JSON.parse(readFileSync(file, 'utf8')) : {}; }
  catch { return {}; }
}

export function cursorOf(cursors, name) {
  const c = cursors[name];
  if (!c) return null;
  return typeof c === 'string' ? c : c.lastPunchUtc || null;
}

// Atomic (write temp + rename) and 0600: a crash mid-write must not truncate the cursor (that would re-send the
// device's whole history), and the file names devices.
export function saveCursors(file, cursors) {
  try {
    const tmp = `${file}.tmp`;
    writeFileSync(tmp, JSON.stringify(cursors, null, 2), { mode: 0o600 });
    try { chmodSync(tmp, 0o600); } catch { /* best effort on non-POSIX */ }
    renameSync(tmp, file);
  } catch (e) { console.error(`[cursor] failed to persist: ${e.message}`); }
}
