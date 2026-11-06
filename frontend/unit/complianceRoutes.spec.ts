import { expect, test } from '@playwright/test';
import { COMPLIANCE_PEOPLE_LABEL, complianceActionLabel, resolveComplianceRoute } from '../src/lib/complianceRoutes';

/**
 * The Saudi Compliance action centre receives `/people?filter=qiwa_blocked|missing_iban|missing_gosi_ref` from the server.
 * The People list has no such filter, and no existing directory filter selects the same people the card counts
 * (`readiness=Blocked` is the activation gate, `NotReady` is a superset), so the honest frontend behaviour is the unfiltered
 * list with a label that does not promise a filter. A real filter needs the backend (see the report: `?gap=...`).
 */
test('the three server filters open the plain People list: no filter that selects a different population', () => {
  for (const f of ['qiwa_blocked', 'missing_iban', 'missing_gosi_ref']) {
    expect(resolveComplianceRoute(`/people?filter=${f}`)).toBe('/people');
  }
});

test('an unknown filter and other keys: the dead key is dropped, real keys survive, other pages are untouched', () => {
  expect(resolveComplianceRoute('/people?filter=something_new')).toBe('/people');
  expect(resolveComplianceRoute('/people?filter=missing_iban&search=ali')).toBe('/people?search=ali');
  expect(resolveComplianceRoute('/people?readiness=Blocked')).toBe('/people?readiness=Blocked');
  expect(resolveComplianceRoute('/saudi-compliance?tab=configure&section=gosi')).toBe('/saudi-compliance?tab=configure&section=gosi');
  expect(resolveComplianceRoute('/payroll')).toBe('/payroll');
});

test('the link says what it opens: the People list, not a filtered worklist', () => {
  expect(complianceActionLabel('/people?filter=qiwa_blocked')).toBe(COMPLIANCE_PEOPLE_LABEL);
  expect(COMPLIANCE_PEOPLE_LABEL).toBe('Open People: incomplete records are marked');
  expect(complianceActionLabel('/payroll')).toBe('Fix');
});
