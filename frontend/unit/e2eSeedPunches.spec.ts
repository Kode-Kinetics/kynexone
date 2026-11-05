import { expect, test } from '@playwright/test';
import { assertImportClean, todayPunchTimes } from '../e2e/bootstrap/seedPunches';

const MIN = 60_000;
const at = (iso: string) => new Date(iso);

test('late in the day nothing is clamped: the full 05:00Z-14:00Z day', () => {
  expect(todayPunchTimes(at('2026-06-16T20:00:00Z'))).toEqual({ inUtc: '2026-06-16T05:00:00Z', outUtc: '2026-06-16T14:00:00Z' });
});

test('mid-day the Out that has not happened yet is pulled back to now minus 10 minutes (the In stays)', () => {
  expect(todayPunchTimes(at('2026-06-16T09:00:00Z'))).toEqual({ inUtc: '2026-06-16T05:00:00Z', outUtc: '2026-06-16T08:50:00Z' });
});

test('early in the day both punches move back, keeping In before Out so the day is still not a missing punch', () => {
  expect(todayPunchTimes(at('2026-06-16T03:00:00Z'))).toEqual({ inUtc: '2026-06-16T02:40:00Z', outUtc: '2026-06-16T02:50:00Z' });
});

test('no punch is ever later than now minus 10 minutes, whatever the hour', () => {
  for (let h = 0; h < 24; h++) for (const m of [0, 15, 45]) {
    const now = new Date(Date.UTC(2026, 5, 16, h, m, 0));
    const t = todayPunchTimes(now);
    if (!t) continue;
    expect(Date.parse(t.outUtc)).toBeLessThanOrEqual(now.getTime() - 10 * MIN);
    expect(Date.parse(t.inUtc)).toBeLessThan(Date.parse(t.outUtc));
    expect(t.inUtc.slice(0, 10)).toBe(now.toISOString().slice(0, 10));
  }
});

test('in the first minutes of the UTC day there is no past instant on today to punch at: seed nothing', () => {
  expect(todayPunchTimes(at('2026-06-16T00:10:00Z'))).toBeNull();
  expect(todayPunchTimes(at('2026-06-16T00:25:00Z'))).not.toBeNull();
});

test('a clean import response passes', () => {
  expect(() => assertImportClean({ totalRows: 4, failedRows: 0, importedRows: 4, errors: [] }, 'import x')).not.toThrow();
});

test('a response with failed rows throws, naming the first errors and the count', () => {
  const body = { totalRows: 9, failedRows: 5, errors: [
    { rowNumber: 2, message: 'Time is in the future' }, { rowNumber: 3, message: 'Unknown employee' },
    { rowNumber: 4, message: 'c' }, { rowNumber: 5, message: 'd' }, { rowNumber: 6, message: 'e' },
  ] };
  expect(() => assertImportClean(body, 'import x')).toThrow(/import x[\s\S]*5 of 9 row\(s\) failed/);
  expect(() => assertImportClean(body, 'import x')).toThrow(/row 2: Time is in the future/);
  expect(() => assertImportClean(body, 'import x')).toThrow(/row 3: Unknown employee/);
  expect(() => assertImportClean(body, 'import x')).not.toThrow(/row 5:/);
});

test('a response that does not report failedRows at all is not treated as clean', () => {
  expect(() => assertImportClean({ totalRows: 4 }, 'import x')).toThrow(/failedRows/);
  expect(() => assertImportClean(null, 'import x')).toThrow(/failedRows/);
});
