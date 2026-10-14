import { expect, test } from '@playwright/test';
import client from '../src/api/client';
import { holidayCalendarApi } from '../src/api/leave';

test('holiday_mutations_match_controller_routes_and_preserve_identity', async () => {
  const prior = client.defaults.adapter;
  const calls: { method?: string; url?: string }[] = [];
  Object.defineProperty(globalThis, 'localStorage', { configurable: true, value: { getItem: () => null } });
  client.defaults.adapter = async config => {
    calls.push({ method: config.method, url: config.url });
    return { data: {}, status: 200, statusText: 'OK', headers: {}, config };
  };
  try {
    await holidayCalendarApi.updateHoliday('existing-holiday', { nameEn: 'National day', date: '2026-09-23' });
    await holidayCalendarApi.deleteHoliday('existing-holiday');
    expect(calls).toEqual([
      { method: 'put', url: '/api/leave/holidays/holidays/existing-holiday' },
      { method: 'delete', url: '/api/leave/holidays/holidays/existing-holiday' },
    ]);
  } finally { client.defaults.adapter = prior; delete (globalThis as { localStorage?: unknown }).localStorage; }
});
