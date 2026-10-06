import { expect, test } from '@playwright/test';
import { createFormatter } from '../src/lib/format';

/**
 * A calendar date ("2026-10-05": an expiry, a pay date) must read as that day for every viewer.
 * It used to be parsed as the VIEWER's local midnight and then shown in the TENANT's zone, so a
 * viewer east of the tenant (Karachi, Tokyo, looking at a Riyadh tenant) saw "4 Oct".
 *
 * Each case switches the process zone (Node re-reads process.env.TZ on assignment) and puts it
 * back afterwards. `npm run test:unit:tz` also runs the whole unit suite under Asia/Karachi.
 */

const ZONES = ['Asia/Karachi', 'Asia/Tokyo', 'America/Los_Angeles', 'Asia/Riyadh'];
const TENANT_ZONES = ['Asia/Riyadh', 'America/New_York', 'Pacific/Kiritimati', '', undefined];

test.describe('date-only values in any viewer zone', () => {
  let saved: string | undefined;
  test.beforeEach(() => { saved = process.env.TZ; });
  test.afterEach(() => {
    if (saved === undefined) delete process.env.TZ; else process.env.TZ = saved;
  });

  for (const viewer of ZONES) {
    test(`viewer in ${viewer}`, () => {
      process.env.TZ = viewer;
      // Guard: the switch really happened, or this test would pass vacuously.
      expect(Intl.DateTimeFormat().resolvedOptions().timeZone).toBe(viewer);
      for (const tenant of TENANT_ZONES) {
        const label = `tenant zone ${JSON.stringify(tenant)}`;
        const f = createFormatter({ locale: 'en', timeZone: tenant, dateFormat: 'YYYY-MM-DD' });
        expect(f.date('2026-10-05', 'medium'), label).toBe('5 Oct 2026');
        expect(f.date('2026-10-05', 'dayMonth'), label).toBe('5 Oct');
        expect(f.date('2026-10-05', 'short'), label).toBe('2026-10-05');
        expect(f.date('2026-01-01', 'long'), label).toBe('1 January 2026');
        expect(f.dateTime('2026-10-05'), label).toBe('5 Oct 2026');
        const ar = createFormatter({ locale: 'ar', timeZone: tenant });
        expect(ar.date('2026-10-05', 'long'), label).toContain('5 أكتوبر 2026');
      }
      // A real instant still follows the tenant's zone: 22:30 UTC is the next day in Riyadh.
      const riyadh = createFormatter({ locale: 'en', timeZone: 'Asia/Riyadh' });
      expect(riyadh.date('2026-10-04T22:30:00Z', 'medium')).toBe('5 Oct 2026');
      expect(riyadh.time('2026-10-04T22:30:00Z')).toBe('01:30');
    });
  }
});
