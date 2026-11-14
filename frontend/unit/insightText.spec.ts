import { expect, test } from '@playwright/test';
import { insightDetail, insightTitle } from '../src/lib/insightText';
import { translate } from '../src/i18n/translations';

type P = Record<string, string | number | null | undefined>;
const en = (key: string, params?: P) => translate('en', key, params);
const ar = (key: string, params?: P) => translate('ar', key, params);
const latin = (s: string) => s.match(/[A-Za-z]{4,}/g) ?? [];
const period = (m: string) => m;

// The rules engine writes its findings in English with the numbers baked in; the home chips showed them as sent.
const CASES = [
  { insightType: 'PayrollVariance', title: 'Net payroll increased by 40.6%', summary: 'Net payroll for November 2026 is increased by 40.6% (133,589) vs. 3-month average (95,000). Review for new hires, terminations, or one-time adjustments.' },
  { insightType: 'PayrollVariance', title: 'Net payroll decreased by 12.0%', summary: 'Net payroll for October 2026 is decreased by 12.0% (83,600) vs. 3-month average (95,000). Review for new hires, terminations, or one-time adjustments.' },
  { insightType: 'MissingSalarySetup', title: '5 employee(s) without salary assignment', summary: '5 of 9 active employees (56%) have no active salary structure assigned. These employees will be excluded from payroll processing until salary is configured.' },
  { insightType: 'OvertimeAnomaly', title: '2 employee(s) with overtime exceeding 30% of average basic', summary: '2 employee(s) received overtime pay exceeding 30% of the company average basic salary in October 2026. Review for unauthorized overtime, shift planning issues, or data entry errors.' },
  { insightType: 'LeaveAccumulationRisk', title: '3 employee(s) with 60+ days leave balance', summary: '3 employee(s) have accumulated more than 60 days of leave balance. Large balances represent a financial liability and may indicate low team morale or vacation planning issues. Consider enforcing a leave utilization policy.' },
  { insightType: 'HeadcountTurnover', title: '2 termination(s) in the last 30 days (7.4%)', summary: '2 employees left in the last 30 days, representing 7.4% monthly turnover. This is above normal thresholds. Review exit interviews and retention strategies. New hires: 1.' },
  { insightType: 'VisaExpiryRisk', title: '1 employee(s) with visa expiring within 60 days', summary: '1 active employee(s) have visas expiring within the next 60 days. Expired visas can result in regulatory fines and operational disruption. Initiate renewal processes immediately.' },
  { insightType: 'InactiveSalaryStructure', title: '4 employee(s) assigned to inactive salary structures', summary: '4 employee(s) are assigned to salary structures that have been deactivated. These employees will fail payroll validation. Reassign them to active salary structures.' },
];

test.describe('rules-engine finding text', () => {
  for (const c of CASES) {
    test(`${c.insightType}: ${c.title}`, () => {
      const title = insightTitle(en, c);
      expect(title).toMatch(/\d/);
      expect(title).not.toMatch(/\(s\)/);
      const arTitle = insightTitle(ar, c);
      expect(arTitle).not.toBe(title);
      expect(latin(arTitle), arTitle).toEqual([]);
      const detail = insightDetail(ar, c, period);
      expect(detail, c.insightType).not.toBe(c.summary);
      expect(latin(detail.replace(/\d{4}-\d{2}/g, '')), detail).toEqual([]);
      // The numbers in the sentence survive.
      for (const n of c.title.match(/\d+(\.\d+)?/g) ?? []) expect(arTitle + detail, `${n} in ${c.insightType}`).toContain(n);
    });
  }

  test('English keeps the finding as the engine wrote it, with employee(s) spelled out', () => {
    expect(insightTitle(en, CASES[0])).toBe('Net payroll increased by 40.6%');
    expect(insightTitle(en, CASES[2])).toBe('5 employees without salary assignment');
  });

  test('an unknown finding type keeps the engine text', () => {
    const unknown = { insightType: 'SomethingNew', title: '3 employee(s) did a thing', summary: 'Details.' };
    expect(insightTitle(ar, unknown)).toBe('3 employees did a thing');
    expect(insightDetail(ar, unknown, period)).toBe('Details.');
  });
});
