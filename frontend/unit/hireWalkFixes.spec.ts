import { test, expect } from '@playwright/test';
import { draftApprovalNotice } from '../src/lib/newHireReview';
import { OVERVIEW_OPENING_STATUSES, mergeOverviewOpenings } from '../src/lib/recruitmentOverview';
import { LOCALE_DICTS } from '../src/i18n/translations';
import { hireWalkFixes } from '../src/i18n/hireWalkFixes';

const arabic = (k: string, p?: Record<string, string | number>) => (LOCALE_DICTS.ar[k] ?? k).replace(/\{(\w+)\}/g, (_, n) => String(p?.[n] ?? ''));

test.describe('New hires notice after Approve says the true state', () => {
  test('an active employee reads as before', () => {
    expect(draftApprovalNotice({ name: 'Layla', employeeCode: 'EMP-7', status: 'Active' })).toBe('Layla is now employee EMP-7.');
  });
  test('a record that is not active yet is never called "now employee" and never told to activate in the same breath', () => {
    const text = draftApprovalNotice({ name: 'Layla', employeeCode: 'EMP-7', status: 'Draft' });
    expect(text).not.toContain('is now employee');
    expect(text).toContain('EMP-7');
    expect(text).toContain('not active yet');
    expect(text).toContain('waiting for a second approver');
    expect(text).toContain('activate them once it is approved');
  });
  test('an active employee whose pay package still waits says both facts as facts, with no instruction to activate', () => {
    const text = draftApprovalNotice({ name: 'Layla', employeeCode: 'EMP-7', status: 'Active', salaryApprovalState: 'Pending' });
    expect(text).toContain('is now employee EMP-7');
    expect(text).toContain('waiting for a second approver');
    expect(text).not.toContain('activate them');
  });
  test('the Arabic wording exists for every state and keeps the code', () => {
    for (const status of ['Active', 'Draft']) {
      const text = draftApprovalNotice({ name: 'ليلى', employeeCode: 'EMP-7', status }, arabic);
      expect(text).toContain('EMP-7');
      expect(text).toContain('ليلى');
    }
    expect(draftApprovalNotice({ name: 'ليلى', employeeCode: 'EMP-7', status: 'Draft' }, arabic)).toContain('معتمِد ثانٍ');
    expect(draftApprovalNotice({ name: 'ليلى', employeeCode: 'EMP-7', status: 'Draft' }, arabic)).not.toContain('أصبح');
  });
});

test.describe('Recruitment overview lists the same openings its Open Positions tile counts', () => {
  const o = (id: string, status: string, createdAtUtc: string) => ({ id, status, createdAtUtc, title: id }) as never;
  test('the definition is Open and In Progress, like the API stats', () => {
    expect([...OVERVIEW_OPENING_STATUSES]).toEqual(['Open', 'InProgress']);
  });
  test('merges the lists, drops other statuses, newest first, and keeps five', () => {
    const merged = mergeOverviewOpenings([
      [o('a', 'Open', '2026-10-01T00:00:00Z'), o('b', 'Open', '2026-10-03T00:00:00Z')],
      [o('c', 'InProgress', '2026-10-02T00:00:00Z'), o('x', 'Closed', '2026-10-09T00:00:00Z')],
    ], 5) as unknown as { id: string }[];
    expect(merged.map(r => r.id)).toEqual(['b', 'c', 'a']);
    const many = mergeOverviewOpenings([[1, 2, 3, 4].map(n => o(`o${n}`, 'Open', `2026-10-0${n}T00:00:00Z`)), [5, 6].map(n => o(`o${n}`, 'InProgress', `2026-10-0${n}T00:00:00Z`))], 5);
    expect(many).toHaveLength(5);
  });
  test('an opening listed twice appears once', () => {
    expect(mergeOverviewOpenings([[o('a', 'Open', '2026-10-01T00:00:00Z')], [o('a', 'Open', '2026-10-01T00:00:00Z')]], 5)).toHaveLength(1);
  });
});

test.describe('hireWalkFixes translations', () => {
  const placeholders = (s: string) => [...s.matchAll(/\{(\w+)[,}]/g)].map(m => m[1]).sort();
  test('every key has an Arabic text with the same placeholders, registered in both dictionaries', () => {
    const keys = Object.keys(hireWalkFixes.en);
    expect(keys.length).toBeGreaterThan(0);
    for (const key of keys) {
      expect(hireWalkFixes.ar[key], `Arabic for ${key}`).toBeTruthy();
      expect(placeholders(hireWalkFixes.ar[key])).toEqual(placeholders(hireWalkFixes.en[key]));
      expect(LOCALE_DICTS.en[key]).toBe(hireWalkFixes.en[key]);
      expect(LOCALE_DICTS.ar[key]).toBe(hireWalkFixes.ar[key]);
    }
    expect(Object.keys(hireWalkFixes.ar).sort()).toEqual([...keys].sort());
  });
  test('no wording promises an email where the server may print', () => {
    for (const key of Object.keys(hireWalkFixes.en)) {
      if (/sign-in code/i.test(key)) expect(key).not.toMatch(/^Email sign-in code/);
    }
  });
});
