#!/usr/bin/env node
import { spawnSync } from 'node:child_process';
import { resolve } from 'node:path';
import { loadConfig, loadCursors } from './config.js';
import { pollOnce } from './poller.js';

const cfg = loadConfig();

// Node's built-in fetch trusts extra CAs only through NODE_EXTRA_CA_CERTS, which is read once at process start.
// With a caFile configured, run the bridge again as a child process that has it set.
if (cfg.caFile && process.env.NODE_EXTRA_CA_CERTS !== resolve(cfg.caFile)) {
  const child = spawnSync(process.execPath, process.argv.slice(1), {
    stdio: 'inherit',
    env: { ...process.env, NODE_EXTRA_CA_CERTS: resolve(cfg.caFile) },
  });
  process.exit(child.status ?? 1);
}
const cursors = loadCursors(cfg.cursorFile);
const once = process.argv.includes('--once');

console.log(`KynexBridge starting · API ${cfg.apiBaseUrl} · ${cfg.devices.length} device(s) · interval ${cfg.pollIntervalSeconds}s${once ? ' · single run' : ''}`);

async function loop() {
  await pollOnce(cfg, cursors);
  if (once) { process.exit(0); }
}

await loop();
if (!once) {
  setInterval(loop, cfg.pollIntervalSeconds * 1000);
  process.on('SIGINT', () => { console.log('\nKynexBridge stopped.'); process.exit(0); });
}
