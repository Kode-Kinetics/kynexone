import { expect, test } from '@playwright/test';
import type { CompanyProfile } from '../src/api/setupAssistant';
import {
  canReviewSavedPreview,
  readSetupDraft,
  saveSetupDraft,
  setupCountryCode,
  setupDraftStorageKey,
  setupProfileFingerprint,
  type SavedSetupPreview,
} from '../src/lib/setupDraftLifecycle';

function memoryStorage() {
  const values = new Map<string, string>();
  return {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value); },
  };
}

const profile: CompanyProfile = {
  countryCode: 'SA',
  currencyCode: 'SAR',
  industry: 'Services',
  companySize: '51-200',
  strictEntityScope: true,
  requireCostCenterForPayroll: true,
  requireGradeForApprovalPolicy: true,
  sections: {
    org: true,
    leave: true,
    shifts: true,
    payroll: true,
    entity: true,
    governance: true,
    leavePolicies: true,
    holidays: true,
    attendance: true,
    localization: true,
    benefits: true,
  },
  configuration: { attendanceMethods: ['Manual'], overtimeModes: ['PaidOvertime'] },
};

test('browser draft retains authored values and is scoped by tenant user and company', () => {
  const storage = memoryStorage();
  const key = setupDraftStorageKey('tenant-1', 'user-1', 'company-1');

  saveSetupDraft(storage, key, profile, 2);

  expect(readSetupDraft(storage, key)).toMatchObject({ step: 2, profile });
  expect(readSetupDraft(storage, setupDraftStorageKey('tenant-1', 'user-1', 'company-2'))).toBeNull();
});

test('saved preview is reusable only for the same inputs and company', () => {
  const preview = {
    companyId: 'company-1',
    profileFingerprint: setupProfileFingerprint(profile),
  } as SavedSetupPreview;

  expect(canReviewSavedPreview(preview, profile, 'company-1')).toBe(true);
  expect(canReviewSavedPreview(preview, { ...profile, industry: 'Changed' }, 'company-1')).toBe(false);
  expect(canReviewSavedPreview(preview, profile, 'company-2')).toBe(false);
});

test('malformed drafts are ignored and storage failures are reported to the caller', () => {
  const storage = memoryStorage();
  storage.setItem('draft', '{');
  expect(readSetupDraft(storage, 'draft')).toBeNull();
  expect(() => saveSetupDraft({ setItem: () => { throw new Error('quota'); } }, 'draft', profile, 0)).toThrow('quota');
});

test('company country codes normalize without inventing unsupported mappings', () => {
  expect(setupCountryCode(' SAU ')).toBe('SA');
  expect(setupCountryCode('ARE')).toBe('AE');
  expect(setupCountryCode('XYZ')).toBe('XYZ');
});
