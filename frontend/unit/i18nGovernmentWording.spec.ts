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

/**
 * Wave-1 officer-facing wording (package F5). The login page must not say a certification is in progress, must not call the Saudi
 * bank file a wage-protection file, and the batch status "Reconciled" must not share its Arabic with "Settled by bank" (تمت التسوية).
 */
test('the login page claims no certification in progress and calls the Saudi file a bank payroll file', () => {
  const source = fs.readFileSync(path.resolve(__dirname, '../src/components/LoginMarketing.tsx'), 'utf8').replace(/\s+/g, ' ');
  expect(source).not.toContain('pending ANB certification');
  expect(source).not.toContain('ملفات حماية الأجور');
  expect(source).not.toContain("name: 'WPS files'");
  expect(source).toContain("name: 'Wage files'");
  expect(source).toContain("nameAr: 'ملفات الأجور'");
  expect(source).toContain('a Saudi bank payroll file, bank acceptance not verified');
  expect(source).toContain('ملف رواتب للبنك في السعودية لم يُتحقق من قبول البنك له');
});

test('the Arabic batch status Reconciled reads تمت المطابقة and never collides with Settled by bank', () => {
  const reconciled = arabicEntries().filter(e => e.key === 'enum.Status.Reconciled');
  expect(reconciled.length).toBeGreaterThan(0);
  expect(reconciled.map(e => e.value)).toEqual(reconciled.map(() => 'تمت المطابقة'));
  // "Settled by bank" is the bank's settlement of an instruction (تمت التسوية لدى البنك), a different fact from a reconciled batch.
  expect(reconciled.some(e => e.value.startsWith('تمت التسوية'))).toBe(false);
});
