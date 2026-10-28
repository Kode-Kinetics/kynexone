#!/usr/bin/env node
/**
 * Design-token ratchet. Counts off-system styling in className strings and fails if any
 * count goes UP versus scripts/design-token-baseline.json. Counts may only go down.
 *
 *   node scripts/design-token-ratchet.mjs            check (exit 1 on regression)
 *   node scripts/design-token-ratchet.mjs --update   lower the baseline (refuses to raise it)
 *   node scripts/design-token-ratchet.mjs --print    print current counts
 */
import { readFileSync, readdirSync, statSync, writeFileSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '..');
const baselinePath = join(here, 'design-token-baseline.json');

export const METRICS = {
  // Arbitrary pixel font sizes: a hidden second type scale. Use the named sizes instead.
  arbitraryFontSize: /\btext-\[\d+(?:\.\d+)?px\]/g,
  // Anything under 11px is below the type-scale floor.
  fontSizeBelow11px: /\btext-\[(?:[0-9]|10)(?:\.\d+)?px\]/g,
  // Raw hex inside an arbitrary Tailwind value instead of a semantic token.
  rawHexInClassName: /-\[[^\]\s]*#[0-9a-fA-F]{3,8}[^\]\s]*\]/g,
  // gray-* is the wrong neutral (the product neutral is slate / the semantic roles).
  grayPalette: /-gray-\d{2,3}\b/g,
  // Radii outside 4 / 8 / 12 / 16 / full.
  radiusOutsideScale: /\brounded(?:-[tbselr]{1,2})?-(?:sm|md|3xl|\[[^\]]+\])/g,
  shadow2xl: /\bshadow-2xl\b/g,
  // Coloured / tinted shadows (shadow-sapphire/30, shadow-blue-500/20 ...).
  tintedShadow: /\bshadow-(?:sapphire|blue|indigo|violet|purple|emerald|rose|red|amber|cyan|midnight)(?:-\d{2,3})?\/\d+/g,
  // Weights beyond regular / medium / semibold.
  fontWeightSprawl: /\bfont-(?:thin|extralight|light|bold|extrabold|black)\b/g,
};

function* walk(dir) {
  for (const name of readdirSync(dir)) {
    if (name === 'node_modules' || name.startsWith('.')) continue;
    const p = join(dir, name);
    const st = statSync(p);
    if (st.isDirectory()) yield* walk(p);
    else if (/\.(tsx|css)$/.test(name)) yield p;
  }
}

export function count() {
  const totals = Object.fromEntries(Object.keys(METRICS).map(k => [k, 0]));
  for (const base of ['src', 'app']) {
    const dir = join(root, base);
    if (!existsSync(dir)) continue;
    for (const file of walk(dir)) {
      // tokens.css is where raw values belong; index.css carries @apply component classes.
      if (file.endsWith('.css') && !file.endsWith('index.css')) continue;
      const text = readFileSync(file, 'utf8');
      for (const [key, re] of Object.entries(METRICS)) totals[key] += (text.match(re) ?? []).length;
    }
  }
  return totals;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const now = count();
  const mode = process.argv[2];
  if (mode === '--print') {
    console.log(JSON.stringify(now, null, 2));
    process.exit(0);
  }
  let baseline = null;
  try { baseline = JSON.parse(readFileSync(baselinePath, 'utf8')); } catch (e) { if (e.code !== 'ENOENT') throw e; }
  if (mode === '--update') {
    if (baseline) {
      const raised = Object.keys(now).filter(k => k in baseline && now[k] > baseline[k]);
      if (raised.length) {
        console.error(`Refusing to raise the baseline: ${raised.map(k => `${k} ${baseline[k]} -> ${now[k]}`).join(', ')}`);
        process.exit(1);
      }
    }
    writeFileSync(baselinePath, JSON.stringify(now, null, 2) + '\n');
    console.log('Baseline written:', now);
    process.exit(0);
  }
  if (!baseline) { console.error('No baseline; run with --update'); process.exit(1); }
  const bad = Object.keys(baseline).filter(k => now[k] > baseline[k]);
  const lower = Object.keys(baseline).filter(k => now[k] < baseline[k]);
  if (bad.length) {
    console.error('Design-token ratchet FAILED (counts may only go down):');
    for (const k of bad) console.error(`  ${k}: ${baseline[k]} -> ${now[k]}`);
    process.exit(1);
  }
  console.log('Design-token ratchet ok.', lower.length ? `Lower than baseline (run --update): ${lower.map(k => `${k} ${baseline[k]}->${now[k]}`).join(', ')}` : '');
}
