import { expect, test } from '@playwright/test';
import { canReviewSavedPreview, readSetupDraft, saveSetupDraft, setupCountryCode, setupDraftStorageKey, setupProfileFingerprint, type SavedSetupPreview } from '../src/lib/setupDraftLifecycle';
import { clearSessionKeepingLocale } from '../src/api/clearSession';
import type { CompanyProfile } from '../src/api/setupAssistant';
import * as lifecycle from '../src/lib/setupDraftLifecycle';

// Resuming a serialized draft must not invalidate it merely because object keys were reordered.
test('extraction checkpoint accepts equivalent restored inputs but rejects real changes', () => {
  expect(lifecycle).toHaveProperty('matchesPolicyExtractionInputs');
  const matches = lifecycle.matchesPolicyExtractionInputs;
  const saved = JSON.stringify({ value: { attendanceMethods: ['Manual'], policySources: [{ name: 'Policy 3', documentId: 'doc', contentSha256: 'hash' }] }, profile: { companyId: 'company', countryCode: 'SA', sections: { org: true, entity: true } } });
  const current = { companyId: 'company', sections: { entity: true, org: true }, countryCode: 'SA', configuration: { policySources: [{ contentSha256: 'hash', documentId: 'doc', name: 'Policy 3' }], attendanceMethods: ['Manual'] } } as CompanyProfile;
  expect(matches(saved, current)).toBe(true);
  expect(matches(saved.replace('"countryCode":"SA"', '"countryCode":"SAU"'), current)).toBe(true);
  expect(matches(saved, { ...current, companyId: 'another' })).toBe(false);
  expect(matches(saved, { ...current, countryCode: 'AE' })).toBe(false);
  expect(matches(saved, { ...current, configuration: { ...current.configuration, attendanceMethods: ['WebCheckIn'] } })).toBe(false);
  expect(matches(saved, { ...current, configuration: { ...current.configuration, policySources: [{ name: 'Policy 3', documentId: 'doc', contentSha256: 'changed' }] } })).toBe(false);
  expect(matches(saved, { ...current, configuration: { ...current.configuration, policyIntakeText: 'unsent draft' } })).toBe(true);
  for (const malformed of ['', 'null', '{}', '{']) expect(matches(malformed, current)).toBe(false);
});

test('single-company policy review identity requires one active exact legal-name match', () => {
  expect(lifecycle).toHaveProperty('singleCompanyPolicyId');
  const resolve = lifecycle.singleCompanyPolicyId;
  const active = { id: 'evostel', legalNameEn: 'Evostel LLC', isActive: true, approvalStatus: 'Active' };
  expect(resolve('SingleCompany', ' evostel llc ', [active])).toBe('evostel');
  expect(resolve('Group', 'Evostel LLC', [active])).toBeUndefined();
  expect(resolve('SingleCompany', '', [active])).toBeUndefined();
  expect(resolve('SingleCompany', 'Other Company', [active])).toBeUndefined();
  expect(resolve('SingleCompany', 'Evostel', [active])).toBeUndefined();
  expect(resolve('SingleCompany', 'Evostel LLC', [active, { ...active, id: 'duplicate' }])).toBeUndefined();
  expect(resolve('SingleCompany', 'Evostel LLC', [{ ...active, isActive: false }])).toBeUndefined();
  expect(resolve('SingleCompany', 'Evostel LLC', [{ ...active, approvalStatus: 'Pending' }])).toBeUndefined();
});

test('explicit accessible company selection scopes extraction despite an edited draft name', () => {
  const companies = [{ id: 'shadow', legalNameEn: 'Evostel Shadow', isActive: true, approvalStatus: 'Active' }];
  expect(lifecycle.singleCompanyPolicyId('SingleCompany', 'Evostel LLC', companies, 'shadow')).toBe('shadow');
  expect(lifecycle.singleCompanyPolicyId('SingleCompany', 'Evostel LLC', companies, 'other')).toBeUndefined();
  expect(lifecycle.singleCompanyPolicyId('Group', 'Evostel Shadow', companies, 'shadow')).toBeUndefined();
  expect(lifecycle.singleCompanyPolicyId('SingleCompany', 'Evostel LLC', [{ ...companies[0], isActive: false }], 'shadow')).toBeUndefined();
});

function memoryStorage() {
  const values = new Map<string, string>();
  return { getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => { values.set(key, value); }, clear: () => values.clear() };
}
const profile: CompanyProfile = {
  countryCode: 'SA', currencyCode: 'SAR', industry: 'Services', companySize: '500+',
  strictEntityScope: true,
  sections: { entity: true, org: true, leave: true, shifts: true, payroll: true, governance: true, leavePolicies: true, holidays: true, attendance: true, localization: true },
  configuration: { policySources: [{ documentId: 'doc-1', contentSha256: 'hash-1', name: 'Policy 4' }], attendanceMethods: ['BiometricDevice'] },
};

test('browser draft preserves policy provenance and step, scoped by tenant, user and company', () => {
  const storage = memoryStorage();
  const key = setupDraftStorageKey('tenant1', 'user1', 'company1');
  saveSetupDraft(storage, key, profile, 3);
  expect(readSetupDraft(storage, key)?.profile).toEqual(profile);
  expect(readSetupDraft(storage, key)?.step).toBe(3);
  for (const other of [setupDraftStorageKey('tenant2', 'user1', 'company1'), setupDraftStorageKey('tenant1', 'user2', 'company1'), setupDraftStorageKey('tenant1', 'user1', 'company2')]) expect(readSetupDraft(storage, other)).toBeNull();
  expect(JSON.parse(storage.getItem(key)!)).not.toHaveProperty('draft');
});

test('sign out clears policy draft including source metadata', () => {
  const storage = memoryStorage();
  const key = setupDraftStorageKey('tenant1', 'user1', 'company1');
  saveSetupDraft(storage, key, profile, 1);
  clearSessionKeepingLocale(storage);
  expect(readSetupDraft(storage, key)).toBeNull();
});

test('malformed, incompatible or out-of-range drafts are ignored', () => {
  const storage = memoryStorage();
  for (const raw of ['{', 'null', JSON.stringify({ version: 2, profile, step: 1 }), JSON.stringify({ version: 1, savedAt: '', profile, step: 8 }), JSON.stringify({ version: 1, savedAt: '', profile: { ...profile, configuration: { grades: {} } }, step: 1 })]) {
    storage.setItem('draft', raw);
    expect(readSetupDraft(storage, 'draft')).toBeNull();
  }
});

test('normalizes supported ISO3 company codes without inventing unknown countries', () => {
  expect(setupCountryCode(' SAU ')).toBe('SA');
  expect(setupCountryCode('ARE')).toBe('AE');
  expect(setupCountryCode('GBR')).toBe('GB');
  expect(setupCountryCode('IN')).toBe('IN');
  expect(setupCountryCode('XYZ')).toBe('XYZ');
});

test('storage quota errors are exposed to the caller rather than falsely reporting saved', () => {
  expect(() => saveSetupDraft({ setItem: () => { throw new Error('quota'); } }, 'draft', profile, 0)).toThrow('quota');
});

test('saved preview requires matching original inputs and company; intake-only edits do not invalidate', () => {
  const preview = { companyId: 'company1', profileFingerprint: setupProfileFingerprint(profile) } as SavedSetupPreview;
  expect(canReviewSavedPreview(preview, profile, 'company1')).toBe(true);
  expect(canReviewSavedPreview(preview, { ...profile, industry: 'Changed' }, 'company1')).toBe(false);
  expect(canReviewSavedPreview(preview, profile, 'company2')).toBe(false);
  expect(canReviewSavedPreview(preview, { ...profile, configuration: { ...profile.configuration, policyIntakeText: 'Not extracted yet' } }, 'company1')).toBe(true);
  expect(setupProfileFingerprint({ ...profile, currencyCode: 'AED' })).not.toBe(preview.profileFingerprint);
});

test('saving a policy review comment preserves the business preview while changed form settings still invalidate it', () => {
  const preview = { companyId: 'company1', profileFingerprint: setupProfileFingerprint(profile) } as SavedSetupPreview;
  const reviewed = { ...profile, configuration: { ...profile.configuration, policyReview: { drafts: { question: { disposition: 'NeedsClarification' as const, comment: 'Await HR approval.', owner: 'HR Director' } }, history: [] } } };
  expect(canReviewSavedPreview(preview, reviewed, 'company1')).toBe(true);
  expect(canReviewSavedPreview(preview, { ...reviewed, configuration: { ...reviewed.configuration, attendanceMethods: ['WebCheckIn'] } }, 'company1')).toBe(false);
});
