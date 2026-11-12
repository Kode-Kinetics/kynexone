import { expect, test } from '@playwright/test';
import { tenantYesterday } from '../src/lib/attendanceBulkEntry';

// "Process Records" used to default its range to TODAY, so pressing it before the shift marked everyone Absent for a
// day that had not happened. The default range now ends yesterday in the company's time zone.
test.describe('the default end of the processing range', () => {
  test('is yesterday in the company time zone, not in the browser zone', () => {
    // 04:37 UTC on 12 Nov is 07:37 in Riyadh: yesterday there is 11 Nov.
    expect(tenantYesterday('Asia/Riyadh', new Date('2026-11-12T04:37:00Z'))).toBe('2026-11-11');
  });

  test('uses the company zone near midnight: 22:30 UTC is already the next day in Riyadh', () => {
    expect(tenantYesterday('Asia/Riyadh', new Date('2026-11-11T22:30:00Z'))).toBe('2026-11-11');
    expect(tenantYesterday('UTC', new Date('2026-11-11T22:30:00Z'))).toBe('2026-11-10');
  });

  test('crosses a month and a year boundary', () => {
    expect(tenantYesterday('Asia/Riyadh', new Date('2026-12-01T08:00:00Z'))).toBe('2026-11-30');
    expect(tenantYesterday('Asia/Riyadh', new Date('2027-01-01T08:00:00Z'))).toBe('2026-12-31');
  });

  test('an unknown zone falls back to the UTC day', () => {
    expect(tenantYesterday('Not/AZone', new Date('2026-11-12T04:37:00Z'))).toBe('2026-11-11');
  });
});
