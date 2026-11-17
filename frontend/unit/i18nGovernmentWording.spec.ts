import { expect, test } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { LOCALE_DICTS } from '../src/i18n/translations';

/**
 * CTO-GATE C10/C15 for the government and bank wording: a key that names Mudad, ANB, WPS or SIF is defined once, or with the SAME Arabic
 * everywhere it appears (keys that differ only by an escaped or typographic dash count as the same key); Mudad is always مدد
 * (no diacritic) and ANB is always البنك العربي الوطني, never البنك العربي alone.
 */
const dir = path.resolve(__dirname, '../src/i18n');
const isDict = (v: unknown): v is Record<string, string> =>
  !!v && typeof v === 'object' && Object.keys(v).length > 0 && Object.values(v as object).every(x => typeof x === 'string');
const normKey = (k: string) => k.replace(/[‐-―−]/g, '-').replace(/\s+/g, ' ').trim();
const GOV = /mudad|\bANB\b|\bWPS\b|\bSIF\b|Arab National/i;

function arabicEntries(): { source: string; key: string; value: string }[] {
  const out: { source: string; key: string; value: string }[] = [];
  for (const file of fs.readdirSync(dir).filter(f => f.endsWith('.ts') && !['translations.ts', 'message.ts'].includes(f))) {
    let mod: Record<string, unknown>;
    try { mod = require(path.join(dir, file)); } catch { continue; }
    for (const [name, value] of Object.entries(mod)) {
      const ar = (value as { ar?: unknown } | null)?.ar;
      if (isDict(ar)) for (const [key, v] of Object.entries(ar)) out.push({ source: `${file}:${name}`, key, value: v });
    }
  }
  for (const [key, v] of Object.entries(LOCALE_DICTS.ar ?? {})) if (typeof v === 'string') out.push({ source: 'LOCALE_DICTS.ar', key, value: v });
  return out;
}

test('a government or bank key never has two different Arabic wordings across catalogs', () => {
  const byKey = new Map<string, { source: string; value: string }[]>();
  const seen = new Set<string>();
  for (const e of arabicEntries()) {
    if (!GOV.test(e.key)) continue;
    const id = `${e.source}|${e.key}`;
    if (seen.has(id)) continue;
    seen.add(id);
    const k = normKey(e.key);
    byKey.set(k, [...(byKey.get(k) ?? []), { source: e.source, value: e.value }]);
  }
  const conflicts = [...byKey].filter(([, v]) => new Set(v.map(x => x.value)).size > 1)
    .map(([k, v]) => `"${k}": ${v.map(x => `${x.source} = «${x.value}»`).join(' vs ')}`);
  expect(byKey.size).toBeGreaterThan(5);
  expect(conflicts, conflicts.join('\n')).toEqual([]);
});

test('the key normaliser treats an escaped em dash and a literal one as one key', () => {
  expect(normKey('Accepted — evidence')).toBe(normKey('Accepted — evidence'));
  expect(normKey('Accepted – evidence')).toBe(normKey('Accepted — evidence'));
});

test('Arabic spells Mudad as مدد and the Arab National Bank in full', () => {
  const bad: string[] = [];
  for (const e of arabicEntries()) {
    if (/مُدد|مدّد/.test(e.value)) bad.push(`${e.source} "${e.key}": Mudad must be مدد`);
    if (/البنك العربي(?! الوطني)/.test(e.value)) bad.push(`${e.source} "${e.key}": ANB must be البنك العربي الوطني`);
  }
  expect(bad, bad.join('\n')).toEqual([]);
});
