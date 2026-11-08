#!/usr/bin/env node
/**
 * `i` coverage ratchet (Screen Standard section 8d). Every <Field> is one of three things:
 *   withInfo     has `info` (and usually `infoKey`): the field explains itself with the `i`
 *   noInfo       has `noInfo="reason"`: exempt, and the reason is in the code, so the omission is a decision
 *   withoutInfo  has neither: an undecided field
 * `withoutInfo` may only go DOWN per file, and a file that is not in the baseline starts at 0: a new screen decides
 * every field. The baseline is scripts/info-coverage-baseline.json (today's counts).
 *
 *   node scripts/info-coverage-ratchet.mjs            check (exit 1 on regression)
 *   node scripts/info-coverage-ratchet.mjs --update   lower the baseline (refuses to raise a count)
 *   node scripts/info-coverage-ratchet.mjs --print    print current counts
 *
 * Only elements named `Field` count (the shared ui/Field and the local helpers it replaces). Other field wrappers
 * (FormField and similar) are migrated to Field first; they are listed by the migration notes, not measured here.
 */
import { readFileSync, readdirSync, statSync, writeFileSync, existsSync } from 'node:fs';
import { join, dirname, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '..');
const baselinePath = join(here, 'info-coverage-baseline.json');

function* walk(dir) {
  for (const name of readdirSync(dir)) {
    if (name === 'node_modules' || name.startsWith('.')) continue;
    const p = join(dir, name);
    const st = statSync(p);
    if (st.isDirectory()) yield* walk(p);
    else if (/\.tsx$/.test(name) && !/\.(spec|test)\.tsx$/.test(name)) yield p;
  }
}

export function countFile(text, fileName = 'x.tsx') {
  const sf = ts.createSourceFile(fileName, text, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
  const counts = { withInfo: 0, noInfo: 0, withoutInfo: 0 };
  const visit = (node) => {
    if ((ts.isJsxOpeningElement(node) || ts.isJsxSelfClosingElement(node)) && node.tagName.getText(sf) === 'Field') {
      const names = new Set(node.attributes.properties.flatMap(a => (ts.isJsxAttribute(a) ? [a.name.getText(sf)] : [])));
      const spread = node.attributes.properties.some(a => ts.isJsxSpreadAttribute(a));
      if (names.has('info') || names.has('infoKey') || spread) counts.withInfo++; // a spread can carry info: not counted against the file
      else if (names.has('noInfo')) counts.noInfo++;
      else counts.withoutInfo++;
    }
    ts.forEachChild(node, visit);
  };
  visit(sf);
  return counts;
}

export function count() {
  const files = {};
  for (const base of ['src', 'app']) {
    const dir = join(root, base);
    if (!existsSync(dir)) continue;
    for (const file of walk(dir)) {
      const c = countFile(readFileSync(file, 'utf8'), file);
      if (c.withInfo + c.noInfo + c.withoutInfo > 0) files[relative(root, file).replaceAll('\\', '/')] = c;
    }
  }
  return Object.fromEntries(Object.entries(files).sort(([a], [b]) => a.localeCompare(b)));
}

export function totals(files) {
  return Object.values(files).reduce((t, c) => ({ withInfo: t.withInfo + c.withInfo, noInfo: t.noInfo + c.noInfo, withoutInfo: t.withoutInfo + c.withoutInfo }), { withInfo: 0, noInfo: 0, withoutInfo: 0 });
}

/** Files whose `withoutInfo` rose (or that are new with undecided fields). */
export function regressions(now, baseline) {
  return Object.entries(now)
    .filter(([file, c]) => c.withoutInfo > (baseline[file]?.withoutInfo ?? 0))
    .map(([file, c]) => `${file}: withoutInfo ${baseline[file]?.withoutInfo ?? 0} -> ${c.withoutInfo}`);
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const now = count();
  const mode = process.argv[2];
  if (mode === '--print') {
    console.log(JSON.stringify({ totals: totals(now), files: now }, null, 2));
    process.exit(0);
  }
  let baseline = null;
  try { baseline = JSON.parse(readFileSync(baselinePath, 'utf8')).files; } catch (e) { if (e.code !== 'ENOENT') throw e; }
  if (mode === '--update') {
    if (baseline) {
      const raised = regressions(now, baseline);
      if (raised.length) { console.error(`Refusing to raise the baseline:\n  ${raised.join('\n  ')}`); process.exit(1); }
    }
    writeFileSync(baselinePath, JSON.stringify({ totals: totals(now), files: now }, null, 1) + '\n');
    console.log('Baseline written:', totals(now));
    process.exit(0);
  }
  if (!baseline) { console.error('No baseline; run with --update'); process.exit(1); }
  const bad = regressions(now, baseline);
  if (bad.length) {
    console.error('Info-coverage ratchet FAILED (a Field without `info` or `noInfo="reason"`; decide it):');
    for (const line of bad) console.error(`  ${line}`);
    process.exit(1);
  }
  const t = totals(now);
  const lower = Object.entries(baseline).filter(([file, c]) => (now[file]?.withoutInfo ?? 0) < c.withoutInfo).length;
  console.log(`Info-coverage ratchet ok. Fields: ${t.withInfo} with info, ${t.noInfo} exempt, ${t.withoutInfo} undecided.${lower ? ` Lower than baseline in ${lower} file(s) (run --update).` : ''}`);
}
