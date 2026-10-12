import type { EmployeeDetail, EmployeeSalaryBreakdownRequest } from '../api/employees';
import type { EmployeeEditField, ResolvedFieldCatalog } from '../api/employeeFieldCatalog';

export type EmployeeEditDraft = Record<string, string>;
export type EmployeeEditChanges = Record<string, string | number | null | EmployeeSalaryBreakdownRequest>;

/** Only these flat update keys are stored exclusively on the payroll profile. */
const PAYROLL_PROFILE_KEYS = new Set(['socialInsuranceReference', 'bankRoutingCode', 'accountNumber', 'molId', 'salaryCurrency', 'payrollGroup', 'salaryStructureReference', 'paymentMethod']);

/**
 * Build the draft from the record returned by GET. A scalar, including an explicit
 * null/empty value, is authoritative. Compliance records can retain old mirrored
 * expiry values after a scalar edit, so they must never fill missing columns.
 * On an older API the caller keeps unprojected expiry fields unavailable.
 * The caller owns permission masking and must not offer protected fields for edit.
 */
export function buildEmployeeEditSnapshot(
  employee: EmployeeDetail,
  fields: readonly EmployeeEditField[],
  _catalog: ResolvedFieldCatalog,
  _countryCode?: string,
): EmployeeEditDraft {
  const source = employee as unknown as Record<string, unknown>;
  const snapshot: EmployeeEditDraft = {};

  for (const field of fields) {
    let raw = source[field.key];
    if (field.key.startsWith('salaryBreakdown.')) {
      const key = field.key.slice('salaryBreakdown.'.length) as keyof EmployeeSalaryBreakdownRequest;
      raw = employee.salaryBreakdown?.[key];
      if (raw == null) {
        if (field.type === 'number' && key !== 'basicSalary') raw = 0;
        if (key === 'currency') raw = employee.payrollProfile?.salaryCurrency;
        if (key === 'effectiveDate') raw = employee.joiningDate;
      }
    }
    if (raw === undefined && PAYROLL_PROFILE_KEYS.has(field.key)) {
      raw = (employee.payrollProfile as Record<string, unknown> | undefined)?.[field.key];
    }
    snapshot[field.key] = raw === null || raw === undefined
      ? ''
      : field.type === 'date' ? String(raw).slice(0, 10) : String(raw);
  }
  return snapshot;
}

/**
 * Preserve the flat, changed-fields-only PUT contract. The server retains text
 * on null, so a deliberate text clear must be ''. Nullable dates/manager use
 * null. Salary and the nonnullable joining date cannot be cleared by this API.
 */
export function buildEmployeeEditChanges(
  form: EmployeeEditDraft,
  fields: readonly EmployeeEditField[],
  changedKeys: readonly string[],
): EmployeeEditChanges {
  const changes: EmployeeEditChanges = {};
  const byKey = new Map(fields.map((field) => [field.key, field]));

  for (const key of changedKeys) {
    const field = byKey.get(key);
    if (!field || !Object.prototype.hasOwnProperty.call(form, key)) {
      throw new Error(`The employee field "${key}" is no longer available. Reopen the employee and try again.`);
    }
    if (key.startsWith('salaryBreakdown.')) continue;
    const value = form[key].trim();
    if (field.type === 'number') {
      if (!value) {
        if (key === 'managerEmployeeId') changes[key] = null;
        else throw new Error(`${field.label} cannot be cleared.`);
      } else {
        const number = Number(value);
        if (!Number.isFinite(number)) throw new Error(`${field.label} must be a valid number.`);
        if (key === 'managerEmployeeId' && (!Number.isInteger(number) || number <= 0)) {
          throw new Error(`${field.label} must be a positive whole number.`);
        }
        changes[key] = number;
      }
    } else if (field.type === 'date') {
      if (!value && key === 'joiningDate') throw new Error(`${field.label} cannot be cleared.`);
      changes[key] = value || null;
    } else {
      changes[key] = value;
    }
  }
  if (changedKeys.some(key => key.startsWith('salaryBreakdown.'))) {
    if (changedKeys.includes('salary')) throw new Error('Save the salary package without a separate salary total.');
    changes.salaryBreakdown = buildSalaryBreakdownChange(form);
  }
  return changes;
}

export function buildSalaryBreakdownChange(form: EmployeeEditDraft): EmployeeSalaryBreakdownRequest {
  const value = (key: string) => (form[`salaryBreakdown.${key}`] ?? '').trim();
  const amounts = ['basicSalary', 'housingAllowance', 'transportAllowance', 'foodAllowance', 'mobileAllowance', 'otherAllowance', 'fixedDeduction'] as const;
  const salary: EmployeeSalaryBreakdownRequest = {};
  for (const key of amounts) {
    const raw = value(key);
    const number = Number(raw);
    if (!raw || !Number.isFinite(number) || number < 0) throw new Error('Enter a non-negative amount for every salary component. Use zero when it does not apply.');
    salary[key] = number;
  }
  if (!salary.basicSalary || salary.basicSalary <= 0) throw new Error('Basic salary must be greater than zero.');
  const gross = amounts.filter(key => key !== 'fixedDeduction').reduce((total, key) => total + (salary[key] ?? 0), 0);
  if ((salary.fixedDeduction ?? 0) > gross) throw new Error('Fixed deduction cannot exceed the gross salary.');
  const date = value('effectiveDate');
  if (!/^\d{4}-\d{2}-\d{2}$/.test(date) || !Number.isFinite(Date.parse(date)) || new Date(date).toISOString().slice(0, 10) !== date) throw new Error('Choose a valid salary effective date.');
  if (!/^[A-Z]{3}$/.test(value('currency'))) throw new Error('Choose a salary currency.');
  if (value('salaryStructureCode').length > 80) throw new Error('Salary structure code must be 80 characters or fewer.');
  return { ...salary, currency: value('currency'), effectiveDate: date, salaryStructureCode: value('salaryStructureCode') };
}
