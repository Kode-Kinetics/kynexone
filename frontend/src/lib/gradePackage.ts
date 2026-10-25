import type { GradePackageComponentOption, GradePayScaleComponentRequest } from '../api/organization';

/**
 * Grade package totals — the mirror of backend GradePackageMath (Infrastructure/Organization/GradePackageMath.cs).
 * Gross monthly cash = the monthly Earning lines; a % line is a share of the basic (the Fixed monthly Earning lines whose
 * code names BASIC — the match the employee salary writer uses). Benefit and Annual lines are not monthly cash.
 */
export interface PackageLineLike {
  componentCode: string | null | undefined;
  componentType: string;
  calculationType: string;
  amount: number;
  percentage: number;
  frequency?: string;
}

export const isPercent = (calculationType: string) => calculationType === 'PercentOfBasic' || calculationType === 'Percentage';
const isMonthly = (l: PackageLineLike) => (l.frequency ?? 'Monthly') !== 'Annual';
const num = (n: unknown) => (typeof n === 'number' && Number.isFinite(n) ? n : 0);

export function packageBasic(lines: PackageLineLike[]): number {
  return lines
    .filter(l => isMonthly(l) && !isPercent(l.calculationType) && l.componentType === 'Earning' && (l.componentCode ?? '').toUpperCase().includes('BASIC'))
    .reduce((sum, l) => sum + num(l.amount), 0);
}

export function lineAmount(line: PackageLineLike, basic: number): number {
  return isPercent(line.calculationType) ? Math.round(basic * num(line.percentage)) / 100 : num(line.amount);
}

export function packageTotals(lines: PackageLineLike[]): { basic: number; gross: number; deductions: number } {
  const priced = lines.filter(l => (l.componentCode ?? '').trim());
  const basic = packageBasic(priced);
  const sum = (type: string) => priced.filter(l => isMonthly(l) && l.componentType === type).reduce((s, l) => s + lineAmount(l, basic), 0);
  return { basic, gross: sum('Earning'), deductions: sum('Deduction') };
}

/** 'below' / 'above' when a priced package falls outside the grade's Min–Max (an unset bound is open). */
export function rangeStatus(gross: number, min: number | undefined, max: number | undefined): 'below' | 'above' | null {
  if (gross <= 0) return null;
  if ((min ?? 0) > 0 && gross < (min ?? 0)) return 'below';
  if ((max ?? 0) > 0 && gross > (max ?? 0)) return 'above';
  return null;
}

/**
 * The lines the editor saves: blank rows stripped, and each line's name, type and taxable flag taken from the catalogue
 * (the server does the same; this keeps the totals honest before saving).
 */
export function linesToSave(lines: GradePayScaleComponentRequest[], catalogue: GradePackageComponentOption[]): GradePayScaleComponentRequest[] {
  const byCode = new Map(catalogue.map(c => [c.code.toUpperCase(), c]));
  return lines
    .filter(l => (l.componentCode ?? '').trim())
    .map((l, i) => {
      const option = byCode.get(l.componentCode.trim().toUpperCase());
      return {
        ...l,
        componentCode: l.componentCode.trim().toUpperCase(),
        componentName: option?.nameEn ?? l.componentName,
        componentType: option?.componentType ?? l.componentType,
        isTaxable: option?.isTaxable ?? l.isTaxable,
        amount: isPercent(l.calculationType) ? 0 : num(l.amount),
        percentage: isPercent(l.calculationType) ? num(l.percentage) : 0,
        sortOrder: i + 1,
      };
    });
}

/** Codes on the grade that are not in the company's catalogue (legacy free-text lines). */
export function unknownCodes(lines: GradePayScaleComponentRequest[], catalogue: GradePackageComponentOption[]): string[] {
  const known = new Set(catalogue.map(c => c.code.toUpperCase()));
  return lines.map(l => (l.componentCode ?? '').trim().toUpperCase()).filter(code => code && !known.has(code));
}
