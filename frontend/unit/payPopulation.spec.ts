import { expect, test } from '@playwright/test';
import { translate, type MessageParams } from '../src/i18n/translations';
import { leftOutGroups, payPopulationReasonLabel } from '../src/lib/payPopulation';

const en = (k: string, p?: MessageParams) => translate('en', k, p);
const ar = (k: string, p?: MessageParams) => translate('ar', k, p);
const arabic = /[؀-ۿ]/;

const codes = [
  'NOT_YET_EMPLOYED', 'LEFT_BEFORE_PERIOD', 'FINAL_WAGE_ALREADY_PAID', 'SUSPENDED_PAY_HOLD', 'INACTIVE_OR_ARCHIVED',
  'EXIT_STATUS_NO_DATE', 'DELETED_NO_WAGES_OWED', 'UNKNOWN_STATUS', 'EXCLUDED_FROM_RUN', 'SELECTION_NOT_ELIGIBLE',
  'WARN_NO_LAST_WORKING_DAY', 'WARN_STATUS_ON_LEAVE_NO_LEAVE_REQUEST', 'DELETED_BUT_WAGES_OWED',
  'WARN_EMPLOYED_STATUS_NOT_PAID',
];

test.describe('pay population reasons', () => {
  test('every server reason code reads as words, in English and Arabic', () => {
    for (const code of codes) {
      expect(payPopulationReasonLabel(en, code)).not.toBe(code);
      expect(payPopulationReasonLabel(ar, code)).toMatch(arabic);
    }
    expect(payPopulationReasonLabel(en, 'SUSPENDED_PAY_HOLD')).toBe('Suspended — not paid (pay hold)');
  });

  test('an unknown code is shown as is, never dropped', () => {
    expect(payPopulationReasonLabel(en, 'SOMETHING_NEW')).toBe('SOMETHING_NEW');
  });

  test('the left-out list groups by reason, with every name kept and the largest group first', () => {
    const groups = leftOutGroups([
      { employeeId: 1, code: 'E1', name: 'A', status: 'Suspended', reasonCode: 'SUSPENDED_PAY_HOLD', message: '' },
      { employeeId: 2, code: 'E2', name: 'B', status: 'Invited', reasonCode: 'NOT_YET_EMPLOYED', message: '' },
      { employeeId: 3, code: 'E3', name: 'C', status: 'Suspended', reasonCode: 'SUSPENDED_PAY_HOLD', message: '' },
    ]);
    expect(groups.map(g => [g.reasonCode, g.entries.length])).toEqual([['SUSPENDED_PAY_HOLD', 2], ['NOT_YET_EMPLOYED', 1]]);
  });
});
