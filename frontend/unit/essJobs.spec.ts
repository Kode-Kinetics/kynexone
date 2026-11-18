import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { test, expect } from '@playwright/test';
import { LOCALE_DICTS } from '../src/i18n/translations';
import {
  ESS_JOBS_PAGE_SIZE, ESS_JOBS_PATH, jobsLoadProblem, jobsParams, lastPage, pageRange, readJobsPage, splitLines,
} from '../src/lib/essJobs';
import { activeEssSection, essNavItems, essPages } from '../src/routes/essSections';

const root = join(__dirname, '..');
const read = (rel: string) => readFileSync(join(root, rel), 'utf8');

test.describe('requirements and responsibilities are newline-separated lists', () => {
  test('splits on LF and CRLF, trims, and drops blank lines', () => {
    expect(splitLines('Degree in HR\r\n  3 years experience  \n\n\nArabic and English')).toEqual(['Degree in HR', '3 years experience', 'Arabic and English']);
  });
  test('removes a typed bullet marker so the list does not show two bullets', () => {
    expect(splitLines('- First\n• Second\n* Third\n–Fourth')).toEqual(['First', 'Second', 'Third', 'Fourth']);
  });
  test('keeps a leading number or a hyphenated word', () => {
    expect(splitLines('5 years in payroll\nCo-ordinate the team')).toEqual(['5 years in payroll', 'Co-ordinate the team']);
  });
  test('null, undefined and blank text are an empty list', () => {
    expect(splitLines(null)).toEqual([]);
    expect(splitLines(undefined)).toEqual([]);
    expect(splitLines('  \n \r\n')).toEqual([]);
  });
});

test.describe('paging params', () => {
  test('always carry an explicit page and pageSize; q only when there is something to search', () => {
    expect(jobsParams({ q: '', page: 1 })).toEqual({ page: 1, pageSize: ESS_JOBS_PAGE_SIZE });
    expect(jobsParams({ q: '   ', page: 2 })).toEqual({ page: 2, pageSize: ESS_JOBS_PAGE_SIZE });
    expect(jobsParams({ q: '  analyst ', page: 3 })).toEqual({ q: 'analyst', page: 3, pageSize: ESS_JOBS_PAGE_SIZE });
  });
  test('a page below 1 or a fraction is clamped to a real page', () => {
    expect(jobsParams({ page: 0 }).page).toBe(1);
    expect(jobsParams({ page: -4 }).page).toBe(1);
    expect(jobsParams({ page: 2.9 }).page).toBe(2);
  });
  test('the page size never exceeds the API maximum', () => {
    expect(ESS_JOBS_PAGE_SIZE).toBeLessThanOrEqual(100);
  });
});

test.describe('the range on screen comes from the server total and the rows that arrived', () => {
  test('first, middle and last page', () => {
    expect(pageRange(1, 25, 60, 25)).toEqual({ from: 1, to: 25, pages: 3 });
    expect(pageRange(2, 25, 60, 25)).toEqual({ from: 26, to: 50, pages: 3 });
    expect(pageRange(3, 25, 60, 10)).toEqual({ from: 51, to: 60, pages: 3 });
  });
  test('an empty list has no range and one page', () => {
    expect(pageRange(1, 25, 0, 0)).toEqual({ from: 0, to: 0, pages: 1 });
  });
  test('the range ends where the rows end, never at the page size', () => {
    expect(pageRange(1, 25, 7, 7)).toEqual({ from: 1, to: 7, pages: 1 });
  });
  test('lastPage is at least 1 and rounds up', () => {
    expect(lastPage(0, 25)).toBe(1);
    expect(lastPage(25, 25)).toBe(1);
    expect(lastPage(26, 25)).toBe(2);
  });
});

test.describe('the reply is checked, not trusted', () => {
  const job = { id: 'j1', jobCode: 'JOB-1', title: 'Analyst', departmentName: 'Finance', designationTitle: 'Analyst', employmentType: 'Full-Time', location: 'Riyadh', description: 'd', requirements: 'a\nb', responsibilities: 'c', publishedAtUtc: '2026-11-01T08:00:00Z' };
  test('a well-formed page passes through with its total', () => {
    const page = readJobsPage({ items: [job], total: 40, page: 2, pageSize: 25 });
    expect(page.total).toBe(40);
    expect(page.items).toHaveLength(1);
  });
  test('a reply with no items array, or no numeric total, is a failed load, not an empty list', () => {
    for (const bad of [null, '<html>502</html>', [], { total: 3 }, { items: [] }, { items: [], total: '3' }, { items: [], total: -1 }]) {
      expect(() => readJobsPage(bad), JSON.stringify(bad)).toThrow(/unexpected reply/);
    }
  });
  test('missing text fields become empty strings so the screen never prints "undefined"', () => {
    const page = readJobsPage({ items: [{ id: 'j2', title: 'Clerk' }], total: 1 });
    expect(page.items[0]).toMatchObject({ id: 'j2', title: 'Clerk', location: '', departmentName: '', requirements: '', publishedAtUtc: null });
  });
});

test.describe('what a failed load means', () => {
  const http = (status: number, data?: unknown) => ({ isAxiosError: true, response: { status, data } });
  test('404 is an unlinked login, in the viewer\'s language when the server gave both', () => {
    const err = http(404, { message: 'Not linked.', messageAr: 'غير مرتبط.' });
    expect(jobsLoadProblem(err, 'en')).toEqual({ kind: 'unlinked', message: 'Not linked.' });
    expect(jobsLoadProblem(err, 'ar')).toEqual({ kind: 'unlinked', message: 'غير مرتبط.' });
  });
  test('404 without a message is still unlinked', () => {
    expect(jobsLoadProblem(http(404), 'en')).toEqual({ kind: 'unlinked', message: null });
  });
  test('403 is forbidden; anything else is a failed load that can be retried', () => {
    expect(jobsLoadProblem(http(403, { message: 'no' }), 'en').kind).toBe('forbidden');
    expect(jobsLoadProblem(http(500), 'en').kind).toBe('failed');
    expect(jobsLoadProblem(new Error('x'), 'en').kind).toBe('failed');
  });
});

test.describe('registration', () => {
  test('the page is a Self-Service page gated on ess.read, reached from the workspace and not the sidebar', () => {
    expect(essPages.map((p) => p.path)).toContain(ESS_JOBS_PATH);
    expect(activeEssSection(ESS_JOBS_PATH)?.id).toBe('overview');
    expect(essNavItems.find((i) => i.path === ESS_JOBS_PATH)?.requiredPermissions).toEqual(['ess.read']);
    expect(read('app/(dashboard)/ess/jobs/page.tsx')).toMatch(/<PermissionGate permissions=\{\['ess\.read'\]\}>/);
    expect(read('src/routes/navigation.ts')).not.toContain(ESS_JOBS_PATH);
  });
  test('the screen is read-only: no apply control, no write call', () => {
    const view = read('src/views/MyOpenPositionsPage.tsx');
    expect(view).not.toMatch(/\bApply\b|\bapply\b|\bsubmit\b/i);
    expect(read('src/api/essJobs.ts')).not.toMatch(/client\.(post|put|patch|delete)/);
  });
  test('every string on it is translated, with the placeholders kept', () => {
    const view = read('src/views/MyOpenPositionsPage.tsx');
    const keys = [...view.matchAll(/\bt\(\s*'((?:[^'\\]|\\.)*)'/g)].map((m) => m[1].replace(/\\'/g, "'"));
    expect(keys.length).toBeGreaterThan(15);
    for (const key of keys) {
      expect(LOCALE_DICTS.en[key], `en: ${key}`).toBeTruthy();
      expect(LOCALE_DICTS.ar[key], `ar: ${key}`).toMatch(/[؀-ۿ]/);
      const holes = (s: string) => [...s.matchAll(/\{(\w+)/g)].map((m) => m[1]).sort().join(',');
      expect(holes(LOCALE_DICTS.ar[key]), key).toBe(holes(LOCALE_DICTS.en[key]));
    }
  });
});
