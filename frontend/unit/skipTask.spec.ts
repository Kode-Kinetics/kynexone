import { expect, test } from '@playwright/test';
import { LOCALE_DICTS } from '../src/i18n/translations';
import { ONBOARDING_PROBLEM_CODES, onboardingProblemKey, skipReasonOk } from '../src/lib/onboardingProblem';

test('a task that blocks activation needs a reason of five characters; others need none', () => {
  expect(skipReasonOk(true, '')).toBe(false);
  expect(skipReasonOk(true, '  abcd ')).toBe(false);
  expect(skipReasonOk(true, '  abcde ')).toBe(true);
  expect(skipReasonOk(false, '')).toBe(true);
});

test('skip_reason_required is a known problem code with English and Arabic sentences', () => {
  expect(ONBOARDING_PROBLEM_CODES).toContain('skip_reason_required');
  const err = { response: { data: { code: 'skip_reason_required' } } };
  expect(onboardingProblemKey(err)).toBe('onboarding.error.skip_reason_required');
  for (const lang of ['en', 'ar'] as const) expect(LOCALE_DICTS[lang]['onboarding.error.skip_reason_required'], lang).toMatch(/\S{3}/);
  expect(LOCALE_DICTS.en['onboarding.error.skip_reason_required']).not.toBe('onboarding.error.skip_reason_required');
});

test('every skip string has Arabic', () => {
  for (const k of ['Skip', 'Skip this task?', 'Skip {task}', 'Reason (required, at least 5 characters)', 'Reason (optional)', 'Skip task', 'Skipping…', 'Task skipped.'])
    expect(LOCALE_DICTS.ar[k], k).toBeTruthy();
});
