'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import type { EmployeeDetail } from '../../api/employees';
import {
  employeeFieldCatalogApi,
  editFieldsForCountry,
  LOCAL_FIELD_CATALOG,
  normalizeCountryCode,
  resolveFieldCatalog,
  SALARY_BREAKDOWN_EDIT_FIELDS,
  type EmployeeEditField,
  type ResolvedFieldCatalog,
} from '../../api/employeeFieldCatalog';
import { employeesApi } from '../../api/employees';
import { useNavigationBlocker } from '../../hooks/useNavigationBlocker';
import { workEmailLocalProblem, workEmailProblemKey } from '../../lib/employeeAccess';
import {
  buildEmployeeEditChanges,
  buildEmployeeEditSnapshot,
  buildSalaryBreakdownChange,
  type EmployeeEditDraft,
} from '../../lib/employeeEditDraft';

export const EMPLOYEE_EDIT_STEP_LABELS = ['Profile', 'Employment', 'Payroll', 'Salary', 'Identity', 'Review'] as const;

const MASKED_KEYS = new Set([
  'salary', 'bankName', 'bankIban', 'wpsBankDetails', 'passportNumber', 'iqamaNumber',
  'medicalInformation', 'disciplinaryRecords', 'terminationReason', 'passportIssueDate',
  'passportExpiryDate', 'visaIssueDate', 'visaExpiryDate', 'residencyIssueDate',
  'workPermitIssueDate', 'visaNumber', 'visaFileNumber', 'muqeemNumber', 'gosiReference',
  'gosiFirstRegisteredOn', 'gosiCohort', 'qiwaContractNumber', 'emiratesId', 'laborCardNumber',
  'qid', 'workPermitNumber', 'civilId', 'residencyNumber', 'idNumber',
  'iqamaExpiryDate', 'emiratesIdExpiryDate', 'qidExpiryDate', 'civilIdExpiryDate',
  'accountNumber', 'bankRoutingCode', 'socialInsuranceReference', 'molId', 'salaryCurrency',
  'payrollGroup', 'salaryStructureReference', 'paymentMethod',
]);
const EXTENDED_PAYROLL_KEYS = new Set(['molId', 'salaryCurrency', 'payrollGroup', 'salaryStructureReference', 'paymentMethod']);
const OPTIONAL_EXPIRY_KEYS = new Set(['iqamaExpiryDate', 'emiratesIdExpiryDate', 'qidExpiryDate', 'civilIdExpiryDate']);

export function employeeEditStep(field: EmployeeEditField): number {
  if (field.key === 'salary' || field.key.startsWith('salaryBreakdown.')) return 3;
  if (field.key === 'workEmail' || field.section === 'Employment') return 1;
  if (field.section === 'Payroll & Banking') return 2;
  if (field.section === 'Compliance Documents') return 4;
  return 0;
}

function fieldsForEmployee(employee: EmployeeDetail, catalog: ResolvedFieldCatalog, canSeeSensitive: boolean) {
  const country = normalizeCountryCode(employee.countryCode);
  const base = editFieldsForCountry(catalog, country);
  const withSalary = Object.prototype.hasOwnProperty.call(employee, 'salaryBreakdown')
    && (employee.salaryBreakdown != null || Number(employee.salary ?? 0) <= 0)
    ? [...base.filter(field => field.key !== 'salary'), ...SALARY_BREAKDOWN_EDIT_FIELDS]
    : base;
  return withSalary.filter(field => canSeeSensitive || (!field.sensitive && !MASKED_KEYS.has(field.key) && !field.key.startsWith('salaryBreakdown.')));
}

function fieldIsAvailable(employee: EmployeeDetail, field: EmployeeEditField) {
  if (OPTIONAL_EXPIRY_KEYS.has(field.key) && (employee as unknown as Record<string, unknown>)[field.key] === undefined) return false;
  if (EXTENDED_PAYROLL_KEYS.has(field.key) && !Object.prototype.hasOwnProperty.call(employee, 'salaryBreakdown')) return false;
  return true;
}

function errorMessage(error: unknown) {
  const data = (error as { response?: { data?: { message?: string; errors?: Record<string, string[]> } } })?.response?.data;
  if (data?.message) return data.message;
  if (data?.errors) return Object.entries(data.errors).map(([field, messages]) => `${field}: ${messages.join(' ')}`).join(' · ');
  return 'Could not save the changes. Please review the values and try again.';
}

export function useEmployeeEditDraft({
  employee,
  canWrite,
  canSeeSensitive,
  initialStep,
  onSaved,
}: {
  employee: EmployeeDetail;
  canWrite: boolean;
  canSeeSensitive: boolean;
  initialStep: number;
  onSaved(status: number): Promise<void> | void;
}) {
  const [catalog, setCatalog] = useState<ResolvedFieldCatalog>(LOCAL_FIELD_CATALOG);
  const fields = useMemo(() => fieldsForEmployee(employee, catalog, canSeeSensitive), [employee, catalog, canSeeSensitive]);
  const availableFields = useMemo(() => fields.filter(field => fieldIsAvailable(employee, field)), [employee, fields]);
  const [original, setOriginal] = useState<EmployeeEditDraft>(() => buildEmployeeEditSnapshot(employee, availableFields, catalog, employee.countryCode));
  const [form, setForm] = useState<EmployeeEditDraft>(() => ({ ...original }));
  const [step, setStepState] = useState(initialStep);
  const [saving, setSaving] = useState(false);
  const [notice, setNotice] = useState('');

  useEffect(() => {
    let cancelled = false;
    employeeFieldCatalogApi.get({ companyId: employee.companyId, countryCode: employee.countryCode, nationality: employee.nationality })
      .then(remote => { if (!cancelled) setCatalog(resolveFieldCatalog(remote)); });
    return () => { cancelled = true; };
  }, [employee.companyId, employee.countryCode, employee.nationality]);

  useEffect(() => {
    const additions = buildEmployeeEditSnapshot(employee, availableFields, catalog, employee.countryCode);
    setOriginal(current => ({ ...additions, ...current }));
    setForm(current => ({ ...additions, ...current }));
  }, [employee, availableFields, catalog]);

  const availableKeys = useMemo(() => new Set(availableFields.map(field => field.key)), [availableFields]);
  const dirtyKeys = useMemo(() => Object.keys(form).filter(key => form[key] !== original[key]), [form, original]);
  const changedKeys = useMemo(() => dirtyKeys.filter(key => availableKeys.has(key)), [availableKeys, dirtyKeys]);
  const unavailableKeys = useMemo(() => dirtyKeys.filter(key => !availableKeys.has(key)), [availableKeys, dirtyKeys]);
  const dirty = dirtyKeys.length > 0;

  const discard = useCallback(() => {
    setForm({ ...original });
    setNotice('');
  }, [original]);
  useNavigationBlocker({ id: `employee-edit-${employee.id}`, when: dirty && !saving, message: 'Discard unsaved employee changes?', onDiscard: discard });

  const setField = useCallback((key: string, value: string) => {
    setForm(current => ({ ...current, [key]: value }));
    setNotice('');
  }, []);

  const validateStep = useCallback((targetStep: number) => {
    if (unavailableKeys.length) {
      setNotice('Some edited fields are no longer available. Revert the changes or reopen the employee record.');
      return false;
    }
    for (const field of availableFields.filter(item => employeeEditStep(item) === targetStep && changedKeys.includes(item.key))) {
      const value = form[field.key]?.trim() ?? '';
      if (field.key === 'englishName' && !value) { setNotice('Enter the employee’s English full name to continue.'); return false; }
      if (field.key === 'salary' && !value) { setNotice('Salary cannot be empty. Enter an amount before continuing.'); return false; }
      if (field.key === 'joiningDate' && !value) { setNotice('Joining date cannot be empty. Choose a date before continuing.'); return false; }
      // WorkEmailField already shows this exact reason as its own alert; repeating it here announced it twice.
      if (field.key === 'workEmail' && workEmailProblemKey(workEmailLocalProblem(value))) { setNotice(''); return false; }
      if (field.type === 'number' && value && !Number.isFinite(Number(value))) { setNotice(`${field.label} must be a valid number.`); return false; }
    }
    if (targetStep === 3 && changedKeys.some(key => key.startsWith('salaryBreakdown.'))) {
      try {
        buildSalaryBreakdownChange(form);
        const effectiveDate = form['salaryBreakdown.effectiveDate'] ?? '';
        if (effectiveDate > new Date().toISOString().slice(0, 10)
          && availableFields.some(field => field.sensitive && !field.key.startsWith('salaryBreakdown.') && changedKeys.includes(field.key))) {
          setNotice('Save future salary changes separately from other approval-required details.');
          return false;
        }
      } catch (error) {
        setNotice(error instanceof Error ? error.message : 'Review the salary values and try again.');
        return false;
      }
    }
    return true;
  }, [availableFields, changedKeys, form, unavailableKeys.length]);

  const setStep = useCallback((next: number) => {
    if (saving) return;
    if (next > step && !validateStep(step)) return;
    setNotice('');
    setStepState(Math.max(0, Math.min(EMPLOYEE_EDIT_STEP_LABELS.length - 1, next)));
  }, [saving, step, validateStep]);

  const save = useCallback(async () => {
    if (!canWrite || saving || step !== 5 || changedKeys.length === 0) return;
    for (let index = 0; index < 5; index++) {
      if (!validateStep(index)) { setStepState(index); return; }
    }
    let changes: ReturnType<typeof buildEmployeeEditChanges>;
    try {
      changes = buildEmployeeEditChanges(form, availableFields, changedKeys);
    } catch (error) {
      setNotice(error instanceof Error ? error.message : 'Review the changed values and try again.');
      return;
    }
    const salaryChanged = changedKeys.some(key => key.startsWith('salaryBreakdown.'));
    const effectiveDate = salaryChanged ? form['salaryBreakdown.effectiveDate'] : new Date().toISOString().slice(0, 10);
    setSaving(true);
    setNotice('');
    try {
      const response = await employeesApi.update(employee.id, effectiveDate, changes);
      setOriginal({ ...form });
      await onSaved(response.status);
    } catch (error) {
      setNotice(errorMessage(error));
    } finally {
      setSaving(false);
    }
  }, [availableFields, canWrite, changedKeys, employee.id, form, onSaved, saving, step, validateStep]);

  const displayValue = useCallback((field: EmployeeEditField, before = false) => {
    const value = (before ? original : form)[field.key];
    return value || 'Not provided';
  }, [form, original]);

  const reviewSections = EMPLOYEE_EDIT_STEP_LABELS.slice(0, 5).map((title, index) => ({
    title,
    rows: availableFields.filter(field => !field.hidden && employeeEditStep(field) === index).map(field => [
      field.sensitive && changedKeys.includes(field.key) ? `${field.label} · Approval` : field.label,
      changedKeys.includes(field.key) ? `From ${displayValue(field, true)} to ${displayValue(field)}` : displayValue(field),
    ] as [string, string]),
  }));

  return {
    fields: availableFields,
    form,
    original,
    step,
    saving,
    notice,
    dirtyKeys,
    changedKeys,
    unavailableKeys,
    reviewSections,
    salaryPackageChanged: changedKeys.some(key => key.startsWith('salaryBreakdown.')),
    salaryEffectiveDate: form['salaryBreakdown.effectiveDate'] ?? '',
    setField,
    setStep,
    save,
    discard,
    displayValue,
  };
}
