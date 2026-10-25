import { readFileSync, existsSync, writeFileSync } from 'node:fs';

export function loadConfig() {
  const path = process.env.KYNEXBRIDGE_CONFIG || './config.json';
  if (!existsSync(path)) {
    console.error(`[config] No config found at ${path}. Copy config.example.json → config.json.`);
    process.exit(1);
  }
  const cfg = JSON.parse(readFileSync(path, 'utf8'));
  cfg.apiBaseUrl = process.env.KYNEX_API_URL || cfg.apiBaseUrl;
  cfg.pollIntervalSeconds = Number(process.env.KYNEX_POLL_SECONDS || cfg.pollIntervalSeconds || 60);
  cfg.cursorFile = cfg.cursorFile || './.cursor.json';
  if (!cfg.apiBaseUrl) { console.error('[config] apiBaseUrl is required.'); process.exit(1); }
  // Optional: the organisation's internal CA (PEM), for a KynexOne server whose certificate it issued.
  cfg.caFile = process.env.KYNEX_CA_FILE || cfg.caFile || '';
  if (cfg.caFile && !existsSync(cfg.caFile)) { console.error(`[config] caFile not found: ${cfg.caFile}`); process.exit(1); }
  if (!Array.isArray(cfg.devices) || cfg.devices.length === 0) { console.error('[config] No devices configured.'); process.exit(1); }
  return cfg;
}

// Per-device cursor: the latest punch timestamp already forwarded, so we never re-send.
export function loadCursors(file) {
  try { return existsSync(file) ? JSON.parse(readFileSync(file, 'utf8')) : {}; }
  catch { return {}; }
}

export function saveCursors(file, cursors) {
  try { writeFileSync(file, JSON.stringify(cursors, null, 2)); }
  catch (e) { console.error(`[cursor] failed to persist: ${e.message}`); }
}
