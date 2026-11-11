import { totalMonthly, type AllowanceDraftLine } from './salaryAllowances';

/** A grade's salary range, as Setup → Grades holds it. 0 means "not set" on that side. */
export interface GradeRange { code: string; minSalary: number; maxSalary: number; companyId?: string | null }

export type GradePayVerdict = 'noRange' | 'inside' | 'below' | 'above';

/** The same test the server applies (EmployeeSalaryBreakdownChanges): monthly pay against the grade's min and max; a side set to 0 is not checked. */
export function gradePayVerdict(range: Pick<GradeRange, 'minSalary' | 'maxSalary'>, monthlyPay: number): GradePayVerdict {
  const min = range.minSalary > 0 ? range.minSalary : 0;
  const max = range.maxSalary > 0 ? range.maxSalary : 0;
  if (!min && !max) return 'noRange';
  if (min && monthlyPay < min) return 'below';
  if (max && monthlyPay > max) return 'above';
  return 'inside';
}

const SALARY_PARTS = ['basicSalary', 'housingAllowance', 'transportAllowance', 'foodAllowance', 'mobileAllowance', 'otherAllowance'] as const;

/**
 * The monthly pay the server judges against the range: basic plus the fixed allowances plus the other monthly allowance lines.
 * The fixed deduction is not part of it. `form` is the edit draft (salaryBreakdown.* keys).
 */
export function monthlyPayFromDraft(form: Readonly<Record<string, string | undefined>>, lines: readonly AllowanceDraftLine[]): number {
  const amount = (key: string) => {
    const n = Number((form[`salaryBreakdown.${key}`] ?? '').trim());
    return Number.isFinite(n) && n > 0 ? n : 0;
  };
  const basic = amount('basicSalary');
  return SALARY_PARTS.reduce((total, key) => total + amount(key), 0) + totalMonthly(lines, basic > 0 ? basic : undefined);
}

/**
 * The grade a code names for this employee, as the server resolves it: the employee's company's own grade first, then a
 * group-wide one; code compared without regard to case. Undefined when nothing matches.
 */
export function gradeForCode<T extends GradeRange>(grades: readonly T[], code: string | undefined, companyId: string | null | undefined): T | undefined {
  const wanted = code?.trim().toUpperCase();
  if (!wanted) return undefined;
  const matching = grades.filter((g) => g.code.trim().toUpperCase() === wanted);
  return matching.find((g) => !!companyId && g.companyId === companyId) ?? matching.find((g) => !g.companyId);
}
