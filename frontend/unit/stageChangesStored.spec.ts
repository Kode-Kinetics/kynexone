import { test, expect } from '@playwright/test';
import { applicationStageChanges, approverOptionLabel } from '../src/lib/recruitmentJourney';

test.describe('stage moves use the stored from/to when the API sends them', () => {
  test('a stored fromStage/toStage wins over the inferred one', () => {
    const changes = applicationStageChanges([
      { id: '1', eventType: 'ApplicationCreated', stage: 'Applied' },
      // An inferred reading would say Applied -> Offer; the stored values say what really happened.
      { id: '2', eventType: 'StageAdvanced', stage: 'Offer', fromStage: 'Interview', toStage: 'Offer' },
    ]);
    expect(changes.get('2')).toEqual({ from: 'Interview', to: 'Offer' });
  });

  test('events without stored stages still infer them', () => {
    const changes = applicationStageChanges([
      { id: '1', eventType: 'ApplicationCreated', stage: 'Applied' },
      { id: '2', eventType: 'StageAdvanced', stage: 'Screening' },
    ]);
    expect(changes.get('2')).toEqual({ from: 'Applied', to: 'Screening' });
  });
});

test.describe('approver dropdown label', () => {
  test('shows the name and the role', () => {
    expect(approverOptionLabel({ name: 'Rana Saleh', email: 'r@x.sa', role: 'HR Manager' })).toBe('Rana Saleh · HR Manager');
  });
  test('falls back to the email when there is no name, and omits an absent role', () => {
    expect(approverOptionLabel({ name: '', email: 'r@x.sa', role: '' })).toBe('r@x.sa');
    expect(approverOptionLabel({ name: 'Rana Saleh', email: 'r@x.sa' })).toBe('Rana Saleh');
  });
});

import { advanceNeedsOverrideReason, OFFER_STAGE_NEEDS_EVALUATION } from '../src/lib/recruitmentJourney';
import { LOCALE_DICTS } from '../src/i18n/translations';

test.describe('moving to Offer without an interview or assessment', () => {
  test('the API refusal is recognised so the screen can ask for a reason', () => {
    const err = { response: { status: 422, data: { error: OFFER_STAGE_NEEDS_EVALUATION, message: 'x' } } };
    expect(advanceNeedsOverrideReason(err)).toBe(true);
    expect(advanceNeedsOverrideReason({ response: { status: 422, data: { error: 'other' } } })).toBe(false);
    expect(advanceNeedsOverrideReason(null)).toBe(false);
  });
  test('the override wording has Arabic', () => {
    for (const k of ['Reason for moving to Offer without an interview or assessment', 'Move to Offer with this reason'])
      expect(LOCALE_DICTS.ar[k], k).toBeTruthy();
  });
});
