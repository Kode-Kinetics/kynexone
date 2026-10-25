import { expect, test } from '@playwright/test';
import { readFileSync } from 'node:fs';
import * as lifecycle from '../src/lib/setupDraftLifecycle';
import type { CompanyProfile } from '../src/api/setupAssistant';

/**
 * The "Policy starter" setup studio is gone (#268). Its preview/apply client, saved-draft storage and the two
 * profile fields nothing read must not linger; what the policy-intake screens still use must keep working.
 */
const api = readFileSync('src/api/setupAssistant.ts', 'utf8');

test('the_studio_preview_and_apply_client_is_gone_but_the_policy_calls_remain', () => {
  expect(api).not.toMatch(/\/api\/setup-assistant\/(preview|apply)/);
  expect(api).not.toMatch(/SetupPreviewResult|normalizeDraft/);
  for (const call of ['prepareOnboarding', 'extractPolicy', 'savePolicyReview', 'policyReviewHistory']) expect(api).toContain(`${call}:`);
});

test('the_company_profile_no_longer_carries_the_unread_approval_model_or_strict_entity_scope', () => {
  const profile = api.slice(api.indexOf('export interface CompanyProfile'), api.indexOf('export interface DraftDepartment'));
  expect(profile).not.toMatch(/approvalModel|strictEntityScope/);
});

test('only_the_helpers_the_policy_screens_use_are_left_in_setupDraftLifecycle', () => {
  expect(Object.keys(lifecycle).sort()).toEqual(['matchesPolicyExtractionInputs', 'setupCountryCode', 'setupProfileFingerprint']);
});

test('a_checkpoint_saved_with_the_retired_profile_fields_still_matches_the_same_inputs', () => {
  const profile = { countryCode: 'SAU', industry: '', companySize: '', currencyCode: 'SAR', sections: { org: true } } as unknown as CompanyProfile;
  const legacy = JSON.stringify({ profile: { ...profile, countryCode: 'SA', approvalModel: 'HRFinal', strictEntityScope: true }, value: {} });
  expect(lifecycle.matchesPolicyExtractionInputs(legacy, profile)).toBe(true);
});

test('the_draft_attendance_policy_no_longer_carries_the_five_settings_nothing_reads', () => {
  const partial = readFileSync('src/lib/partialPolicy.ts', 'utf8');
  const draft = api.slice(api.indexOf('export interface DraftAttendancePolicy'), api.indexOf('export interface DraftOvertimeMultiplier'));
  for (const field of ['lateThresholdMinutes', 'absentThresholdMinutes', 'requiresOvertimeApproval', 'allowAbsenceToLeaveConversion']) {
    expect(draft).not.toContain(field);
    expect(partial).not.toContain(field);
  }
  // roundingRule stays on the overtime policy, where it is live.
  expect(draft).not.toContain('roundingRule');
  expect(api.slice(api.indexOf('export interface DraftOvertimePolicy'))).toContain('roundingRule');
});
