#!/usr/bin/env node
// ESLint with a ratchet. Runs eslint over the app, counts findings per (file, rule), and compares them with
// eslint-baseline.json: any count above the baseline (or a pair that is not in it) FAILS; lower counts pass
// and are reported so the baseline can be tightened with `npm run lint:repin`.
import { spawnSync } from 'node:child_process';
import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { resolve, relative } from 'node:path';

const root = resolve(new URL('..', import.meta.url).pathname);
const baselinePath = resolve(root, 'eslint-baseline.json');
const repin = process.argv.includes('--repin');

const r = spawnSync('npx', ['eslint', 'app', 'src', 'middleware.ts', 'instrumentation.ts', '--format', 'json', '--no-warn-ignored'], {
  cwd: root, encoding: 'utf8', maxBuffer: 256 * 1024 * 1024,
});
let results;
try { results = JSON.parse(r.stdout); } catch { console.error(r.stdout, r.stderr); process.exit(2); }

const counts = {};
let fatal = 0;
for (const f of results) {
  const rel = relative(root, f.filePath).replaceAll('\\', '/');
  for (const m of f.messages) {
    if (!m.ruleId) { fatal++; console.error(`${rel}:${m.line} ${m.message}`); continue; }
    (counts[rel] ??= {})[m.ruleId] = (counts[rel][m.ruleId] ?? 0) + 1;
  }
}
if (fatal) { console.error(`${fatal} parse/config error(s)`); process.exit(2); }

const total = Object.values(counts).flatMap((o) => Object.values(o)).reduce((a, b) => a + b, 0);
if (repin) {
  const sorted = Object.fromEntries(Object.keys(counts).sort().map((f) => [f, Object.fromEntries(Object.entries(counts[f]).sort())]));
  writeFileSync(baselinePath, JSON.stringify(sorted, null, 1) + '\n');
  console.log(`baseline written: ${total} findings in ${Object.keys(counts).length} files`);
  process.exit(0);
}
const baseline = existsSync(baselinePath) ? JSON.parse(readFileSync(baselinePath, 'utf8')) : {};
const grew = []; let shrunk = 0;
for (const [file, rules] of Object.entries(counts)) {
  for (const [rule, n] of Object.entries(rules)) {
    const b = baseline[file]?.[rule] ?? 0;
    if (n > b) grew.push(`${file}  ${rule}  ${b} -> ${n}`);
  }
}
for (const [file, rules] of Object.entries(baseline)) {
  for (const [rule, b] of Object.entries(rules)) shrunk += Math.max(0, b - (counts[file]?.[rule] ?? 0));
}
const baseTotal = Object.values(baseline).flatMap((o) => Object.values(o)).reduce((a, b) => a + b, 0);
console.log(`eslint: ${total} findings (baseline ${baseTotal}); ${shrunk} fixed since the baseline`);
if (grew.length) {
  console.error('NEW lint violations (fix them; do not raise the baseline):\n  ' + grew.join('\n  '));
  process.exit(1);
}
if (shrunk) console.log('Tip: run `npm run lint:repin` to lock in the improvement.');
