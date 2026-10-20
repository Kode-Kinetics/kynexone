import { test, expect } from '@playwright/test';
import { retainedEmailRequest, offerWorkspacePath, canOfferAction, communicationLabel, draftInput } from '../src/api/offerLifecycle';
import client from '../src/api/client';
import { applicationsApi, offersApi } from '../src/api/recruitment';
import { offerTemplatesApi } from '../src/api/offerTemplates';
import { employeeDraftsApi } from '../src/api/employeeDrafts';
test.beforeEach(() => { Object.defineProperty(globalThis, 'localStorage', { configurable: true, value: { getItem: () => null } }); });

test('both entry paths resolve the same revision and actions require server permission', () => {
  expect(offerWorkspacePath('revision-1')).toBe('/recruitment/offers/revision-1');
  expect(canOfferAction({ issue: { allowed: false, reason: 'Approval required' } }, 'issue')).toBe(false);
  expect(canOfferAction({}, 'issue')).toBe(false);
  expect(canOfferAction({ issue: { allowed: true } }, 'issue')).toBe(true);
});
test('network replay retains exact reviewed payload and key while changed content gets a new request', () => {
  const fields = { expectedVersion: 2, subject: 'Offer', message: 'Please review', reviewedSenderFingerprint: 'sender-1' };
  const first = retainedEmailRequest(null, fields, () => 'key-1');
  expect(retainedEmailRequest(first, fields, () => 'key-2')).toBe(first);
  const changed = retainedEmailRequest(first, { ...fields, message: 'Updated' }, () => 'key-2');
  expect(changed.idempotencyKey).toBe('key-2');
  expect(first.message).toBe('Please review');
});
test('communication labels do not claim delivery or invite retry for uncertain outcomes', () => {
  expect(communicationLabel('Captured')).toContain('testing');
  expect(communicationLabel('Unknown')).toContain('reconciliation');
  expect(communicationLabel('LegacyUnverified')).toContain('unverified');
  expect(communicationLabel('AcceptedByRelay')).not.toMatch(/delivered/i);
});
test('draft save strips server-owned snapshot fields and totals', () => {
  const result = draftInput({ expectedVersion: 4, reviewedPolicyVersion: 2, startDate: '2027-01-01', responseDeadline: null, probationMonths: 3, recipientEmail: 'candidate@example.test', offeredJobTitle: 'Analyst', offeredDepartment: 'Finance', components: [{ code: 'BASIC', labelEn: 'Basic', labelAr: null, kind: 'Cash', amount: 9000, description: null, frequency: 'Monthly', included: true, exclusionReason: null, payComponentId: null, order: 0, required: true, structureField: 'BasicSalary' }], monthlyCash: 999999 } as never);
  expect(result).not.toHaveProperty('monthlyCash');
  expect(result.components?.[0]).not.toHaveProperty('required');
  expect(result.components?.[0]).not.toHaveProperty('structureField');
  expect(result.expectedVersion).toBe(4);
});

test('retained application and approval adapters send explicit concurrency versions', async () => {
  const captured: { url?: string; body: Record<string, unknown> }[] = [];
  const original = client.defaults.adapter;
  client.defaults.adapter = async config => { captured.push({ url: config.url, body: JSON.parse(config.data) }); return { data: {}, status: 200, statusText: 'OK', headers: {}, config }; };
  try {
    await applicationsApi.advance('app', 7);
    await applicationsApi.reject('app', 8, 'Reviewed reason');
    await offersApi.requestApproval('offer', { expectedVersion: 3, approverUserId: 'checker', approverName: 'Checker' });
    await offersApi.decideApproval('offer', 'approval', { expectedVersion: 4, decision: 'Approved' });
    expect(captured.map(r => r.body.expectedVersion)).toEqual([7, 8, 3, 4]);
    expect(captured.map(r => r.url)).toEqual(['/api/recruitment/applications/app/advance', '/api/recruitment/applications/app/reject', '/api/recruitment/offers/offer/approvals', '/api/recruitment/offers/offer/approvals/approval/decide']);
  } finally { client.defaults.adapter = original; }
});

test('candidate preview sends parent identity and version and handoff never sends money or placement', async () => {
  const captured: { url?: string; body: Record<string, unknown> }[] = [];
  const original = client.defaults.adapter;
  client.defaults.adapter = async config => { captured.push({ url: config.url, body: JSON.parse(config.data) }); return { data: {}, status: 200, statusText: 'OK', headers: {}, config }; };
  try {
    await offerTemplatesApi.candidatePreview('actual-offer', 12, { schemaVersion: 2 } as never);
    await employeeDraftsApi.saveOfferCompensation('hire', 6, [{ code: 'BASIC', action: 'Payroll', payrollField: 'BasicSalary', amount: 999, placementCompanyId: 'guessed' } as never], true);
    expect(captured[0].body).toEqual({ offerId: 'actual-offer', expectedVersion: 12, design: { schemaVersion: 2 } });
    expect(captured[1].body).toEqual({ expectedVersion: 6, dispositions: [{ code: 'BASIC', action: 'Payroll', payrollField: 'BasicSalary' }], confirm: true });
  } finally { client.defaults.adapter = original; }
});
