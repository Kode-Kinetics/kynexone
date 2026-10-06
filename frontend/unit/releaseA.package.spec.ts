import { readFileSync } from 'node:fs';
import { test, expect } from '@playwright/test';
import { translate } from '../src/i18n/translations';
import { packageStrings } from '../src/i18n/releaseA/package';
import { coverageText, essWhy, hrWhy, reasonText, valueText, type FormatContext, type ValueLine } from '../src/components/entitlements/packageFormat';

// Release A slice R2: the package in words, as Mohammed (G3, plan §5) sees it in English and Arabic.

const ctx = (locale: 'en' | 'ar'): FormatContext => ({ t: (k) => translate(locale, k), locale, currency: 'SAR' });
const line = (over: Partial<ValueLine>): ValueLine => ({
  componentCode: 'X', valueType: 'Amount', amount: null, rate: null, monthlyCash: null, coverageTier: null, quantity: null,
  dependantScope: 'None', maxDependants: null, dependantsCovered: 0, limitPeriod: null, ...over,
});

const housing = line({ componentCode: 'HOUSING', valueType: 'PercentOfBasic', rate: 0.25, monthlyCash: 2000, limitPeriod: 'Monthly' });
const medical = line({ componentCode: 'MEDICAL', valueType: 'CoverageTier', coverageTier: 'B', dependantScope: 'Family', dependantsCovered: 3 });
const ticket = line({ componentCode: 'AIR_TICKET', valueType: 'Quantity', quantity: 1, coverageTier: 'Economy', limitPeriod: 'Annual' });
const advance = line({ componentCode: 'LOAN_HOUSING_ADVANCE', valueType: 'MultipleOfHousing', rate: 3, amount: 6000 });

test('Mohammed’s package reads as the storyline says, in English', () => {
  const en = ctx('en');
  expect(valueText(housing, en)).toBe('25% of basic = SAR 2,000 a month');
  expect(valueText(medical, en)).toBe('Class B');
  expect(coverageText(medical, en.t)).toBe('Employee + 3 dependants');
  expect(valueText(ticket, en)).toBe('1 × Economy class ticket a year');
  expect(valueText(advance, en)).toBe('Up to SAR 6,000 (3 × housing allowance)');
  expect(valueText(line({ componentCode: 'TRANSPORT', monthlyCash: 800, amount: 800, limitPeriod: 'Monthly' }), en)).toBe('SAR 800 a month');
  expect(valueText(line({ componentCode: 'PER_DIEM', amount: 250, limitPeriod: 'PerDay' }), en)).toBe('SAR 250 a day');
  expect(valueText(line({ componentCode: 'HOUSING', valueType: 'InKind' }), en)).toBe('Provided in kind');
  expect(valueText(line({ componentCode: 'EDUCATION', amount: 10000, dependantScope: 'Children', maxDependants: 2, limitPeriod: 'Annual' }), en))
    .toBe('SAR 10,000 a child a year · up to 2 children');
});

test('and in Arabic (باقتي), with Western digits and the riyal sign after the amount', () => {
  const ar = ctx('ar');
  expect(valueText(housing, ar)).toBe('25% من الراتب الأساسي = 2,000 ر.س شهرياً');
  expect(valueText(medical, ar)).toBe('الفئة B');
  expect(coverageText(medical, ar.t)).toBe('الموظف + 3 من المعالين');
  expect(valueText(advance, ar)).toBe('حتى 6,000 ر.س (3 × بدل السكن)');
  expect(translate('ar', 'Why this value?')).toBe('لماذا هذه القيمة؟');
});

test('"Why this value?" names the grade cell for HR and the contract year for the employee', () => {
  const en = ctx('en');
  const why = hrWhy({
    source: 'Facility', gradeStandardDiffers: false, isCompanyOverride: false, gradeName: 'G3 Supervisor', companyName: 'Masar Facility Services',
    cell: { effectiveFrom: '2026-11-01', minServiceMonths: null, afterProbation: false, nationalityScope: 'Any', nationalityBasis: null, note: null, valueText: '3 × housing allowance' },
  }, en);
  expect(why[0]).toBe('Current policy: the grade cell for G3 Supervisor, in force since 1 Nov 2026.');
  expect(why).toContain('The same for every company in the group.');

  const frozen = hrWhy({
    source: 'ContractFrozen', gradeStandardDiffers: true, isCompanyOverride: true, gradeName: 'G3 Supervisor', companyName: 'Masar Logistics',
    cell: { effectiveFrom: '2026-01-01', minServiceMonths: null, afterProbation: false, nationalityScope: 'NonSaudi', nationalityBasis: 'Home-leave ticket per contract', note: null, valueText: '' },
    frozen: { effectiveFrom: '2026-02-01', effectiveTo: '2027-01-31', verificationState: 'Verified', contractNumber: 'CON-1' },
  }, en);
  expect(frozen).toEqual([
    'Fixed for contract CON-1 from 1 Feb 2026 to 31 Jan 2027.',
    'Copied from the grade cell for G3 Supervisor, in force since 1 Jan 2026.',
    'Masar Logistics sets its own value for this grade.',
    'Limited to non-Saudi employees. Legal basis: Home-leave ticket per contract',
    'The grade standard is different now. This is reviewed at renewal; nothing changes mid-year.',
  ]);

  const mine = essWhy({ basis: 'contract', gradeName: 'Supervisor', gradeNameAr: 'مشرف', companyRule: false, since: '2026-02-01', until: '2027-01-31' }, ctx('ar'));
  expect(mine[0]).toContain('مثبتة في عقدك');
  expect(mine[0]).not.toMatch(/[{}]/);
});

test('a reason is always a sentence, never a raw code', () => {
  for (const code of ['ENTITLEMENT_NOT_OFFERED_BY_COMPANY', 'ENTITLEMENT_NOT_IN_GRADE', 'ENTITLEMENT_HOUSING_IN_KIND', 'ENTITLEMENT_SALARY_MISSING',
    'ENTITLEMENT_NATIONALITY_UNCONFIRMED', 'ENTITLEMENT_LOAN_POLICY_BLOCKS', 'ENTITLEMENT_TERM_OVERLAP', 'ENTITLEMENT_CELL_MISSING', 'SOMETHING_NEW']) {
    for (const locale of ['en', 'ar'] as const) {
      const text = reasonText(code, ctx(locale).t);
      expect(text.length, code).toBeGreaterThan(10);
      expect(text, code).not.toContain('_');
    }
  }
});

test('every string the package screens translate has an Arabic entry', () => {
  const files = ['src/components/entitlements/packageFormat.ts', 'src/components/entitlements/EmployeePackagePanel.tsx',
    'src/components/entitlements/DependantsPanel.tsx', 'src/components/entitlements/ProposalCard.tsx',
    'src/components/entitlements/WhyPopover.tsx', 'src/views/MyPackagePage.tsx'];
  for (const file of files) {
    const source = readFileSync(file, 'utf8');
    for (const [, key] of source.matchAll(/\bt\('((?:[^'\\]|\\.)*)'\)/g)) {
      expect(translate('ar', key), `${file}: ${key}`).not.toBe(key);
    }
  }
  expect(Object.keys(packageStrings.ar)).toEqual(Object.keys(packageStrings.en));
});

test('the loan form explains the housing advance as a multiple of the housing allowance, and the grid offers it only there', async () => {
  const { breakdownExplanation, fillTemplate, allowsHousingMultiple, gradeReasonKeyFor } = await import('../src/lib/gradeLoanLimits');
  const sentence = breakdownExplanation({ limit: 'GradePerLoan', basis: 'MultipleOfHousing', multiple: 3, salaryBasisAmount: 2000, cap: 6000,
    outstandingNow: null, available: 6000, unit: 'Principal' }, (n) => `SAR ${n.toLocaleString('en-US')}`)!;
  expect(fillTemplate(translate('en', sentence.key), sentence.values)).toBe('Eligible up to SAR 6,000 = 3 × housing allowance SAR 2,000');
  expect(translate('ar', sentence.key)).toContain('بدل السكن');
  // Keyed on the entitlement component code, as the server is: the display code alone never decides.
  expect([
    allowsHousingMultiple({ code: 'HOUSING_ADVANCE' }),
    allowsHousingMultiple({ code: 'Housing', entitlementComponentCode: 'LOAN_HOUSING_ADVANCE' }),
    allowsHousingMultiple({ code: 'HOUSING_ADVANCE', entitlementComponentCode: 'LOAN_HOUSING' }),
    allowsHousingMultiple({ code: 'PERSONAL' }),
  ]).toEqual([true, true, false, false]);
  for (const self of [true, false]) {
    const key = gradeReasonKeyFor('GradeHousingInKind', self);
    expect(key).toContain('in kind');
    expect(translate('ar', key)).toContain('عيناً');
  }
});

test('"not eligible yet" says which criterion and from when; no dependants on file is said, not counted as 0', () => {
  const en = ctx('en');
  expect(reasonText('ENTITLEMENT_NOT_ELIGIBLE_CRITERIA', en.t, 'ServiceMonths', '2027-02-01', 'en'))
    .toBe('Applies from 1 Feb 2027, once the required months of service are completed.');
  expect(reasonText('ENTITLEMENT_NOT_ELIGIBLE_CRITERIA', ctx('ar').t, 'AfterProbation', '2026-05-02', 'ar')).toContain('فترة التجربة');
  expect(coverageText(medical, en.t, 0)).toBe('Employee — no dependants on file');
  expect(valueText({ ...advance, amount: null, resolvedAmount: 6000 }, en)).toBe('Up to SAR 6,000 (3 × housing allowance)');
});
