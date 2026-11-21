import { expect, test } from '@playwright/test';
import { tenantLocalInstantIso } from '../src/lib/tenantInstant';

// The overtime form sends the moment a person typed as a clock time on a work date. The server reads that moment in
// the company's time zone, so the clock time must be read in the company's zone, not the zone of the browser.

test('a clock time is read in the company zone, whatever zone the browser is in', () => {
  expect(tenantLocalInstantIso('2026-12-02', '18:00', 'Asia/Riyadh')).toBe('2026-12-02T15:00:00.000Z');
  expect(tenantLocalInstantIso('2026-12-02', '00:30', 'Asia/Riyadh')).toBe('2026-12-01T21:30:00.000Z');
  expect(tenantLocalInstantIso('2026-07-01', '09:00', 'America/New_York')).toBe('2026-07-01T13:00:00.000Z');
});

test('a zone with a daylight change is read at the offset in force that day', () => {
  expect(tenantLocalInstantIso('2026-01-15', '09:00', 'America/New_York')).toBe('2026-01-15T14:00:00.000Z');
  expect(tenantLocalInstantIso('2026-03-08', '12:00', 'America/New_York')).toBe('2026-03-08T16:00:00.000Z');
});

test('without a known company zone the clock time falls back to the browser zone', () => {
  const local = new Date('2026-12-02T18:00:00').toISOString();
  expect(tenantLocalInstantIso('2026-12-02', '18:00', '')).toBe(local);
  expect(tenantLocalInstantIso('2026-12-02', '18:00', 'Not/AZone')).toBe(local);
});
