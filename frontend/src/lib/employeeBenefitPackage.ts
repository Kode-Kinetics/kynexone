import type { EmployeeBenefitPackage } from '../api/benefits';

/** The employee benefits reply with both lists always present, so a partial or empty reply shows "nothing yet" instead of crashing the record. */
export function normaliseEmployeePackage(raw: unknown, employeeId: number): EmployeeBenefitPackage {
  const r = (raw && typeof raw === 'object' && !Array.isArray(raw) ? raw : {}) as Partial<EmployeeBenefitPackage>;
  return {
    employeeId: r.employeeId ?? employeeId,
    employeeName: r.employeeName ?? '',
    gradeId: r.gradeId ?? null,
    companyId: r.companyId ?? null,
    asOf: r.asOf ?? '',
    enrollments: Array.isArray(r.enrollments) ? r.enrollments : [],
    additionalRequests: Array.isArray(r.additionalRequests) ? r.additionalRequests : [],
  };
}
