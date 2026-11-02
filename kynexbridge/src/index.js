#!/usr/bin/env node
import { loadConfig, loadCursors } from './config.js';
import { pollOnce } from './poller.js';

const cfg = loadConfig();
const cursors = loadCursors(cfg.cursorFile);
const once = process.argv.includes('--once');

console.log(`KynexBridge starting · API ${cfg.apiBaseUrl} · ${cfg.devices.length} device(s) · interval ${cfg.pollIntervalSeconds}s${once ? ' · single run' : ''}`);

// Never overlap two polls: a slow run (retries, big backlog) must not be doubled up by the next timer tick.
let running = false;
async function loop() {
  if (running) { console.warn('previous poll still running — skipping this tick.'); return; }
  running = true;
  try { await pollOnce(cfg, cursors); } finally { running = false; }
  if (once) process.exit(0);
}

await loop();
if (!once) {
  setInterval(loop, cfg.pollIntervalSeconds * 1000);
  process.on('SIGINT', () => { console.log('\nKynexBridge stopped.'); process.exit(0); });
}
