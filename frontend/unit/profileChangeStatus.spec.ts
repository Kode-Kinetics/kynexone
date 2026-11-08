import { test, expect } from '@playwright/test';
import { legacyFieldLabelKey, profileChangeStatus } from '../src/lib/profileChangeStatus';

// G-003: the older mobile request has a status the employee can read and field names HR can read.
test('every status the server can hold has a label and a tone', () => {
  expect(profileChangeStatus('PendingHR')).toEqual({ label: 'Waiting for HR', tone: 'amber' });
  expect(profileChangeStatus('Approved')).toEqual({ label: 'Approved and applied', tone: 'emerald' });
  expect(profileChangeStatus('Rejected')).toEqual({ label: 'Sent back by HR', tone: 'rose' });
  expect(profileChangeStatus('AwaitingFinance')).toEqual({ label: 'Waiting for Finance', tone: 'amber' });
  expect(profileChangeStatus('Expired')).toEqual({ label: 'Expired', tone: 'slate' });
});

test('an unknown status is shown as it is, never hidden', () => {
  expect(profileChangeStatus('Something')).toEqual({ label: 'Something', tone: 'slate' });
});

test('legacy fields map to dictionary keys', () => {
  expect(legacyFieldLabelKey('phone')).toBe('ess.profileChange.field.phone');
  expect(legacyFieldLabelKey('emergencyContactName')).toBe('ess.profileChange.field.emergencyContactName');
});
