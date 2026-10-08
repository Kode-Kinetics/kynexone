import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { test, expect } from '@playwright/test';
import { LOCALE_DICTS, translate } from '../src/i18n/translations';
import { ESS_SECTIONS, activeEssPage, activeEssSection, essPageAllowed, essPages, visibleEssPages } from '../src/routes/essSections';

const ARABIC = /[؀-ۿ]/;
const root = join(__dirname, '..');

// Self-Service is one sidebar entry; its pages are sections and tabs inside the workspace. These pin
// the rules the workspace navigation (EssWorkspaceNav) depends on.

test('every page belongs to exactly one section and has its own route under /ess', () => {
  const paths = essPages.map((p) => p.path);
  expect(new Set(paths).size).toBe(paths.length);
  for (const path of paths) {
    expect(path === '/ess' || path.startsWith('/ess/'), path).toBe(true);
    expect(() => readFileSync(join(root, 'app/(dashboard)', path.slice(1), 'page.tsx'))).not.toThrow();
  }
});

test('the active section and tab follow the URL, including deeper paths', () => {
  expect(activeEssSection('/ess')?.id).toBe('overview');
  expect(activeEssSection('/ess/payslips')?.id).toBe('pay');
  expect(activeEssSection('/ess/deductions')?.id).toBe('pay');
  expect(activeEssSection('/ess/overtime')?.id).toBe('time');
  expect(activeEssSection('/ess/documents')?.id).toBe('requests');
  expect(activeEssSection('/ess/jawazat')?.id).toBe('requests');
  expect(activeEssPage('/ess/requests/123')?.path).toBe('/ess/requests');
  // A deeper path never lights up Overview just because it starts with /ess.
  expect(activeEssPage('/ess/unknown')).toBeUndefined();
});

test('a feature-flagged tab shows only while its flag is on, and a section never goes empty', () => {
  const pay = ESS_SECTIONS.find((s) => s.id === 'pay')!;
  const time = ESS_SECTIONS.find((s) => s.id === 'time')!;
  expect(visibleEssPages(pay, () => false).map((p) => p.path)).toEqual(['/ess/payslips']);
  expect(visibleEssPages(pay, (k) => k === 'release_a').map((p) => p.path)).toEqual(['/ess/payslips', '/ess/package', '/ess/deductions']);
  expect(visibleEssPages(time, () => false).map((p) => p.path)).toEqual(['/ess/leave']);
  for (const s of ESS_SECTIONS) expect(visibleEssPages(s, () => false).length, s.id).toBeGreaterThan(0);
});

test('every section, tab, page name and hint is translated into Arabic', () => {
  for (const s of ESS_SECTIONS) {
    expect(ARABIC.test(translate('ar', s.label)), s.label).toBe(true);
    for (const p of s.pages) {
      for (const key of [p.label, p.tab, p.hint]) {
        expect(LOCALE_DICTS.en[key], key).toBeTruthy();
        expect(ARABIC.test(translate('ar', key)), key).toBe(true);
      }
    }
  }
});

test('Exit and re-entry (a Saudi Jawazat visa) is offered only to users with a company in Saudi Arabia', () => {
  const requests = ESS_SECTIONS.find((s) => s.id === 'requests')!;
  const on = () => true;
  const allowed = () => ({ allowed: true });
  expect(visibleEssPages(requests, on, ['SA']).map((p) => p.path)).toContain('/ess/jawazat');
  expect(visibleEssPages(requests, on, ['KW', 'BH']).map((p) => p.path)).not.toContain('/ess/jawazat');
  expect(visibleEssPages(requests, on, []).map((p) => p.path)).not.toContain('/ess/jawazat');
  expect(essPageAllowed('/ess/jawazat', on, ['OM', 'SA'], allowed)).toBe(true);
  expect(essPageAllowed('/ess/jawazat', on, ['OM'], allowed)).toBe(false);
  // A module switched off hides the page whatever the country.
  expect(essPageAllowed('/ess/benefits', on, ['SA'], () => ({ allowed: false }))).toBe(false);
});
