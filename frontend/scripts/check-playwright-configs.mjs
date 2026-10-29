#!/usr/bin/env node
// Orphan guard: every playwright*.config.ts under frontend/ must be either
//   (a) referenced by name in a .github/workflows/*.yml file, or
//   (b) declared in e2e/config-manifest.json with a disposition and a real reason.
// And a manifest entry that points at a config that no longer exists, or that is ALSO referenced by a
// workflow (stale), or whose disposition says "nightly" but nightly.yml does not run it, fails too.
// Run: node scripts/check-playwright-configs.mjs   (from frontend/)
import { readFileSync, readdirSync, statSync, existsSync } from 'node:fs';
import { join, basename, resolve, relative } from 'node:path';

const frontend = resolve(new URL('..', import.meta.url).pathname);
const repo = resolve(frontend, '..');
const workflowsDir = join(repo, '.github', 'workflows');

function walk(dir, out = []) {
  for (const e of readdirSync(dir)) {
    if (e === 'node_modules' || e === '.next' || e === 'dist' || e.startsWith('.')) continue;
    const p = join(dir, e);
    if (statSync(p).isDirectory()) walk(p, out); else if (/^playwright.*\.config\.ts$/.test(e)) out.push(p);
  }
  return out;
}

const configs = walk(frontend).map((p) => basename(p)).sort();
const workflows = Object.fromEntries(
  readdirSync(workflowsDir).filter((f) => /\.ya?ml$/.test(f)).map((f) => [f, readFileSync(join(workflowsDir, f), 'utf8')]),
);
const manifest = JSON.parse(readFileSync(join(frontend, 'e2e', 'config-manifest.json'), 'utf8')).configs;
const DISPOSITIONS = new Set(['nightly', 'covered-by-chromium', 'manual-stack', 'manual']);

const errors = [];
const referencedBy = (name) => Object.entries(workflows).filter(([, t]) => t.includes(name)).map(([f]) => f);

for (const c of configs) {
  // The default config is picked up by a bare `npx playwright test` (browser-pilot, chrome lanes).
  if (c === 'playwright.config.ts') continue;
  const refs = referencedBy(c);
  const m = manifest[c];
  if (refs.length === 0 && !m) {
    errors.push(`ORPHAN: ${c} is run by no workflow and has no entry in e2e/config-manifest.json. Wire it into .github/workflows (nightly.yml for slow/stack suites), declare it in the manifest with a reason, or delete it.`);
  }
  if (m) {
    if (!DISPOSITIONS.has(m.disposition)) errors.push(`${c}: unknown disposition "${m.disposition}"`);
    if (!m.reason || m.reason.trim().length < 20) errors.push(`${c}: manifest entry needs a real reason (>= 20 chars)`);
    if (m.disposition === 'nightly' && !refs.includes('nightly.yml')) errors.push(`${c}: disposition "nightly" but .github/workflows/nightly.yml does not run it`);
    if (refs.length > 0 && m.disposition !== 'nightly') errors.push(`${c}: now referenced by ${refs.join(', ')}; remove its stale manifest entry`);
  }
}
for (const name of Object.keys(manifest)) {
  if (!configs.includes(name)) errors.push(`${name}: in manifest but the config file no longer exists; remove the entry`);
}

console.log(`playwright configs: ${configs.length}, in manifest: ${Object.keys(manifest).length}`);
if (errors.length) { console.error(errors.map((e) => `  - ${e}`).join('\n')); process.exit(1); }
console.log('ok: no orphan playwright configs');
