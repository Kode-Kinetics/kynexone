import { expect, test } from '@playwright/test';
import { lastMeasuredLabel } from '../src/lib/glLeaveProvision';

test('a rehire not yet measured in the new period says so instead of showing the old period', () => {
  const t = (key: string) => (key === 'Not yet measured' ? 'لم يُقَس بعد' : key);
  expect(lastMeasuredLabel({ lastMeasuredPeriod: '2026-07', notYetMeasured: true }, t)).toBe('لم يُقَس بعد');
  expect(lastMeasuredLabel({ lastMeasuredPeriod: '2026-07', notYetMeasured: false }, t)).toBe('2026-07');
  expect(lastMeasuredLabel({ lastMeasuredPeriod: '2026-07' }, t)).toBe('2026-07');
});
