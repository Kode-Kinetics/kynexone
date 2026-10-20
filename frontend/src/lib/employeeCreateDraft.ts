export const EMPLOYEE_CREATE_RESUME_KEY = 'kynexone.employee-create.resume.v1';

export type EmployeeCreateResume = {
  employeeId: number;
  section: 'personal' | 'employment' | 'pay-benefits' | 'compliance-documents';
};

export function readEmployeeCreateResume(storage: Pick<Storage, 'getItem'>): EmployeeCreateResume | null {
  try {
    const value = JSON.parse(storage.getItem(EMPLOYEE_CREATE_RESUME_KEY) ?? 'null') as Partial<EmployeeCreateResume> | null;
    if (!value || !Number.isInteger(value.employeeId) || Number(value.employeeId) <= 0) return null;
    if (!['personal', 'employment', 'pay-benefits', 'compliance-documents'].includes(value.section ?? '')) return null;
    return value as EmployeeCreateResume;
  } catch {
    return null;
  }
}

export function writeEmployeeCreateResume(storage: Pick<Storage, 'setItem'>, value: EmployeeCreateResume) {
  storage.setItem(EMPLOYEE_CREATE_RESUME_KEY, JSON.stringify(value));
}

export function clearEmployeeCreateResume(storage: Pick<Storage, 'removeItem'>) {
  storage.removeItem(EMPLOYEE_CREATE_RESUME_KEY);
}
