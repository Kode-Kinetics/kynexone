import { expect, test } from '@playwright/test';
import { resolveComplianceRoute } from '../src/lib/complianceRoutes';
import { parsePeopleDirectoryState } from '../src/lib/peopleRoutes';

const state = (route: string) => parsePeopleDirectoryState(new URL(route, 'https://x.invalid').searchParams);

test('the three server filters become directory filters the People list parses', () => {
  expect(state(resolveComplianceRoute('/people?filter=qiwa_blocked'))).toEqual({ readiness: 'Blocked' });
  expect(state(resolveComplianceRoute('/people?filter=missing_iban'))).toEqual({ readiness: 'NotReady' });
  expect(state(resolveComplianceRoute('/people?filter=missing_gosi_ref'))).toEqual({ readiness: 'NotReady' });
});

test('the ignored key never reaches the People list', () => {
  for (const f of ['qiwa_blocked', 'missing_iban', 'missing_gosi_ref', 'something_new']) {
    expect(resolveComplianceRoute(`/people?filter=${f}`)).not.toContain('filter=');
  }
});

test('an unknown filter lands on the plain list, other keys survive, other pages are untouched', () => {
  expect(resolveComplianceRoute('/people?filter=something_new')).toBe('/people');
  expect(resolveComplianceRoute('/people?filter=missing_iban&search=ali')).toBe('/people?search=ali&readiness=NotReady');
  expect(resolveComplianceRoute('/people?readiness=Blocked')).toBe('/people?readiness=Blocked');
  expect(resolveComplianceRoute('/saudi-compliance?tab=configure&section=gosi')).toBe('/saudi-compliance?tab=configure&section=gosi');
  expect(resolveComplianceRoute('/payroll')).toBe('/payroll');
});
