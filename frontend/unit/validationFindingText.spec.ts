import { expect, test } from '@playwright/test';
import { findingText } from '../src/lib/validationFindingText';
import { payItemLabel } from '../src/lib/payItemLabel';
import { translate } from '../src/i18n/translations';

const en = (key: string, params?: Record<string, string | number | null | undefined>) => translate('en', key, params);
const ar = (key: string, params?: Record<string, string | number | null | undefined>) => translate('ar', key, params);
const latin = (s: string) => s.replace(/[⁦-⁩]/g, '').match(/[A-Za-z]{4,}/g) ?? [];

const ARREARS = { code: 'WARN_ARREARS_LOOKBACK_TRUNCATED', message: 'Maha Almutairi (EVT2-0008) has a salary change effective 2022-01-07, which is older than the 24-month arrears lookback. These finalised period(s) were NOT settled by this run: 2021-04, 2021-05, 2021-06. Raise a supplementary run, or widen payparameter.arrears_max_lookback_months.' };
const BANK = { code: 'IMPORTED_BANK_DETAILS_UNVERIFIED', message: "Employee EVT2-0008's bank details came from an employee import and have not been confirmed by a second person. Verify them with the employee, then confirm them on the employee's record (People → the employee → Confirm bank details) before this run is locked." };
const COHORT = { code: 'GOSI_COHORT_NOT_RECORDED', message: 'Employee EVT2-0001 (Fahad Alharbi): GOSI cohort not recorded, contribution basis unverified. There is no GOSI first-registration date on the employee record.' };
const PERMIT = { code: 'READINESS_PAY_BLOCKED', message: 'Yousef Alshammari (EVT2-0005): required before payroll — Work permit number. Until it is recorded, exporting the WPS / bank file needs an explicit acknowledgement to include this employee.' };

test.describe('validation finding text', () => {
  test('never shows a raw configuration key, a month list or "before this run is locked"', () => {
    for (const locale of [en, ar]) {
      for (const f of [ARREARS, BANK, COHORT, PERMIT]) {
        const text = findingText(locale, f);
        expect(text, f.code).not.toMatch(/payparameter|arrears_max|before this run is locked/);
        expect(text, f.code).not.toMatch(/2021-0\d/);
      }
    }
  });

  test('known findings read in Arabic with no English left over', () => {
    for (const f of [ARREARS, BANK, COHORT, PERMIT]) {
      const text = findingText(ar, f);
      expect(latin(text), `${f.code}: ${text}`).toEqual([]);
    }
  });

  test('an unknown finding keeps its text but loses a raw key', () => {
    const text = findingText(en, { code: 'SOMETHING_NEW', message: 'Widen payparameter.some_new_setting to continue.' });
    expect(text).toBe('Widen payroll setting to continue.');
  });
});

test.describe('benefit findings keep their details in both languages', () => {
  const HELD = { code: 'CASH_ALLOWANCE_CHANGE_WAITING_FOR_FINANCE',
    message: 'بدل سيارة (ALW_CAR): a change is waiting for Finance since 2026-11-16; this run paid the last confirmed terms. Ask another Finance approver to confirm the change in Benefits by grade.' };
  const LEGACY = { code: 'LEGACY_BENEFIT_ENROLMENT_SKIPPED',
    message: "علي حسن (C001): the previous benefits system's enrolment 3f2b1c9e-0000-4000-8000-000000000001 in سيارة (SalaryAllowance) was not paid. Benefits are now set in Benefits by grade. Review it in the legacy enrolment reconcile report, and end it or move it to the employee's grade package." };
  test('the held-back allowance finding names the allowance and the date, in English and Arabic', () => {
    expect(findingText(en, HELD)).toBe('بدل سيارة (ALW_CAR): a change is waiting for Finance since 2026-11-16, so this run paid the last confirmed terms. Ask another Finance approver to confirm the change in Benefits by grade.');
    const text = findingText(ar, HELD);
    expect(text).toContain('ALW_CAR');
    expect(text).toContain('2026-11-16');
    expect(latin(text), text).toEqual([]);
  });
  test('the skipped legacy enrolment finding names the employee, the plan and the reference, in Arabic', () => {
    const text = findingText(ar, LEGACY);
    expect(text).toContain('علي حسن');
    expect(text).toContain('3f2b1c9e-0000-4000-8000-000000000001');
    expect(latin(text), text).toEqual([]);
  });
});

test.describe('late overtime no run pays', () => {
  const LATE = { code: 'OVERTIME_PRIOR_PERIOD_UNPAID', message: 'Employee EVT2-0008 has 2.00 h of approved overtime (150.00 owed) from an earlier pay period (2026-05-28) that no payroll run has paid.' };
  test('names the amount owed, in English and in Arabic', () => {
    expect(findingText(en, LATE)).toContain('150.00');
    const arabic = findingText(ar, LATE);
    expect(arabic).toContain('150.00');
    expect(latin(arabic)).toEqual([]);
  });
});

test.describe('pay item labels', () => {
  const labels = ['Overtime (5.00 h × (168.75 + 125.00 × 0.90)/h)', 'Late/early attendance deduction', 'Loss of Pay (1.00 d × 133.33/d)', 'Basic Salary', 'Housing Allowance'];

  test('English is unchanged', () => {
    for (const l of labels) expect(payItemLabel(en, l)).toBe(l);
  });

  test('Arabic names the item and isolates the working so its numbers do not reorder', () => {
    const overtime = payItemLabel(ar, labels[0]);
    expect(overtime.startsWith('العمل الإضافي')).toBe(true);
    expect(overtime).toContain('⁦(5.00 h × (168.75 + 125.00 × 0.90)/h)⁩');
    for (const l of labels) expect(latin(payItemLabel(ar, l)), l).toEqual([]);
  });
});
