import { expect, test } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';

/**
 * Terminology ratchet for the cost-accounting release (Arabic review, binding glossary). It fails on a banned term in the
 * Arabic strings and the components that carry Arabic literals. Comment lines are ignored.
 */
const root = join(__dirname, '..', 'src');
const files = [
  'i18n/costCentres.ts',
  'i18n/payrollCostCentres.ts',
  'lib/payrollCostLock.ts',
  'components/setup/CostCentreMasterPanel.tsx',
  'components/employees/EmployeeCostCentreField.tsx',
  'components/payroll/UncostedApprovalAck.tsx',
  'components/payroll/CostCentreLockPreview.tsx',
  'components/payroll/CostCentreJournalReport.tsx',
  'components/gl/CostAccountingPanel.tsx',
].filter((f) => { try { readFileSync(join(root, f)); return true; } catch { return false; } });

const AR = '\\u0621-\\u064A';
const banned: { name: string; re: RegExp }[] = [
  { name: 'مسيرة (use مسير الرواتب)', re: /مسيرة/ },
  { name: 'الأب for a cost-centre parent (use المركز الأعلى)', re: /(^|[^ء-ي])[وبلف]?الأب(?![ء-ي])/ },
  { name: 'تخصيص (use مركز تكلفة استثنائي للموظف)', re: /تخصيص/ },
  { name: 'slash plural (use ICU branches or a list)', re: new RegExp(`[${AR}]+/[${AR}]+`) },
  { name: 'bare المالية (use الإدارة المالية)', re: /(?<!الإدارة )(?<!المراجعة )المالية/ },
  { name: 'خانات (use منازل عشرية)', re: /خانات|خانتين/ },
  { name: 'رابط/روابط/يمسح (use ارتباط / يزيل)', re: /روابط|رابط|يمسح|سيمسح/ },
  { name: 'بند (use سطر)', re: new RegExp(`(^|[^${AR}])[وبلكف]?(ال)?بنو?د`) },
  { name: 'حساب أرباح وخسائر (use حسابات قائمة الدخل)', re: /حساب أرباح وخسائر/ },
  { name: 'bare القفل التالي / أعد القفل (use قفل مسير الرواتب)', re: /القفل التالي|أعد القفل|سيتوقف القفل/ },
  { name: 'دفتر اليومية (use قيد اليومية)', re: /دفتر اليومية/ },
];

const stripComments = (src: string) => src.split('\n').filter((l) => !/^\s*(\/\/|\*|\/\*)/.test(l)).join('\n');

test.describe('cost accounting Arabic terminology', () => {
  for (const f of files) {
    test(`${f} has no banned term`, () => {
      const text = stripComments(readFileSync(join(root, f), 'utf8'));
      const hits = banned.filter((b) => b.re.test(text)).map((b) => b.name);
      expect(hits).toEqual([]);
    });
  }

  test('the scan covers the two i18n files at least', () => {
    expect(files).toContain('i18n/costCentres.ts');
    expect(files).toContain('i18n/payrollCostCentres.ts');
  });
});
