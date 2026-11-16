import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { test, expect } from '@playwright/test';
import { LOCALE_DICTS, translate } from '../src/i18n/translations';
import { navigationGroups, navigationHints } from '../src/routes/navigation';

// Launch wave L4.3: an export or a report is named for what it is. Nothing the product produces for GOSI is a filing,
// and the Saudi bank file is a payment instruction in a bank's format, not a Mudad/WPS submission.

const read = (relative: string) => readFileSync(join(__dirname, '..', relative), 'utf8');
const ar = (key: string) => translate('ar', key);
const hasArabic = (key: string) => Boolean(LOCALE_DICTS.ar[key]) && LOCALE_DICTS.ar[key] !== key;

/** Every t('…') literal in a source file (single-quoted, with \' escapes undone). */
function literals(source: string): string[] {
  return [...source.matchAll(/\bt\('((?:[^'\\]|\\.)*)'/g)].map(m => m[1].replace(/\\'/g, "'"));
}

test('the GOSI screen is a reconciliation report in the menu, the heading and Setup', () => {
  const nav = navigationGroups.flatMap(g => g.items).find(i => i.path === '/gosi-filing');
  expect(nav?.label).toBe('GOSI Reconciliation');
  expect(hasArabic('GOSI Reconciliation')).toBe(true);
  expect(ar('GOSI Reconciliation')).not.toContain('ملفات');
  expect(navigationHints['/gosi-filing']).toMatch(/reconcil/i);
  expect(navigationHints['/gosi-filing']).toMatch(/not a filing/i);
  expect(hasArabic(navigationHints['/gosi-filing'])).toBe(true);

  const page = read('src/views/GosiFilingPage.tsx');
  expect(page).toContain("t('GOSI reconciliation report')");
  expect(hasArabic('GOSI reconciliation report')).toBe(true);
  expect(read('src/lib/setupAreas.ts')).toContain("'GOSI reconciliation'");
  expect(hasArabic('GOSI reconciliation')).toBe(true);
});

test('no sentence on the GOSI screen says anything was filed, or tells the user to file from it', () => {
  const claims = literals(read('src/views/GosiFilingPage.tsx')).filter(s => /\bfil(e|ed|ing)\b/i.test(s) && !/not (a|submitted|filed)|nothing is (submitted|filed)/i.test(s));
  expect(claims).toEqual([]);
  // The one place that says what the screen is not.
  const notice = 'This is a reconciliation report. Nothing is submitted or filed with GOSI from here; submit and pay through the GOSI portal.';
  expect(read('src/views/GosiFilingPage.tsx')).toContain(notice);
  expect(hasArabic(notice)).toBe(true);
});

test('every changed GOSI sentence has Arabic, and the Arabic does not call it a filing', () => {
  for (const key of literals(read('src/views/GosiFilingPage.tsx'))) {
    if (!/reconcil|GOSI|period|run/i.test(key)) continue;
    expect(hasArabic(key), `Arabic for "${key}"`).toBe(true);
  }
  for (const key of ['GOSI reconciliation report', 'GOSI Reconciliation', 'GOSI reconciliation']) {
    expect(ar(key)).not.toMatch(/تقديم|إقرار/);
  }
});

test('the GOSI CSV export is named for reconciliation, never as a GOSI upload', () => {
  const key = 'Export CSV (for reconciliation, not a GOSI upload)';
  expect(read('src/views/GosiFilingPage.tsx')).toContain(key);
  expect(hasArabic(key)).toBe(true);
});

test('the Saudi bank file is a payroll payment instruction file in a bank format', () => {
  const panel = read('src/components/payroll/SaudiBankExportPanel.tsx');
  expect(panel).toContain("title: 'Payroll payment instruction file (bank format) — ANB Connect CSV'");
  expect(panel).not.toContain("title: 'Bank instruction file");
  expect(panel).toContain("title: 'ملف تعليمات دفع الرواتب (بصيغة البنك) — ANB Connect CSV'");

  const payroll = read('src/views/PayrollPage.tsx');
  const note = literals(payroll).find(s => s.startsWith('Saudi bank payroll file:'));
  expect(note).toBeTruthy();
  expect(note).toContain('Payroll payment instruction file (bank format)');
  expect(note).not.toMatch(/switched on for this company/);
  expect(hasArabic(note!)).toBe(true);
});
