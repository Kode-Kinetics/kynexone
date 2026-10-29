import type { IdType } from './nationality';

/** Everything the wizard collects. Strings throughout so a half-typed value is never coerced; payload.ts converts. */
export interface HireForm {
  englishName: string;
  companyId: string;
  startDate: string;
  designationId: string;
  gradeId: string;
  branchId: string;
  departmentId: string;
  managerId: string;
  employeeCode: string;
  manualCode: boolean;
  workEmail: string;
  arabicName: string;
  preferredName: string;
  gender: string;
  maritalStatus: string;
  nationalityCode: string;
  idType: IdType | '';
  idNumber: string;
  idExpiry: string;
  idChecksumOverrideReason: string;
  /** CD27: an expat's passport, as a second identity row beside the Iqama / Border Number. */
  passportNumber: string;
  passportExpiry: string;
  dob: string;
  mobileCountry: string;
  mobileNational: string;
  personalEmail: string;
  employmentType: string;
  contractType: string;
  contractEndDate: string;
  probationEndDate: string;
  workLocation: string;
  iban: string;
  molId: string;
  basic: string;
  housing: string;
  transport: string;
  other: string;
}

export function emptyHireForm(): HireForm {
  return {
    englishName: '', companyId: '', startDate: '', designationId: '', gradeId: '', branchId: '', departmentId: '', managerId: '',
    employeeCode: '', manualCode: false, workEmail: '', arabicName: '', preferredName: '', gender: '', maritalStatus: '',
    nationalityCode: '', idType: '', idNumber: '', idExpiry: '', idChecksumOverrideReason: '', passportNumber: '', passportExpiry: '', dob: '', mobileCountry: '', mobileNational: '',
    personalEmail: '', employmentType: '', contractType: '', contractEndDate: '', probationEndDate: '', workLocation: '', iban: '', molId: '',
    basic: '', housing: '', transport: '', other: '',
  };
}

/** True when the person has typed or chosen anything beyond the pre-selected company. */
export function hasContent(form: HireForm, defaults: HireForm = emptyHireForm()): boolean {
  return (Object.keys(form) as Array<keyof HireForm>).some(k => k !== 'companyId' && form[k] !== defaults[k]);
}

type CompanyOwnedChoice = { id: string; companyId?: string | null };

export interface HireCompanyChoices {
  branches: CompanyOwnedChoice[];
  departments: CompanyOwnedChoice[];
  designations: CompanyOwnedChoice[];
  grades: CompanyOwnedChoice[];
  /** Managers are already scope-filtered by the employee list endpoint; cross-company managers are valid for group scope. */
  managers: Array<{ id: number }>;
}

/** Keep only organisation references that remain valid when the employing company changes. */
export function rebaseHireCompany(form: HireForm, companyId: string, choices: HireCompanyChoices): HireForm {
  const sharedOrOwnedBy = (id: string, rows: CompanyOwnedChoice[]) => {
    if (!id) return '';
    const row = rows.find(x => x.id === id);
    return row && (!row.companyId || (!!companyId && row.companyId === companyId)) ? id : '';
  };
  const branch = form.branchId && companyId && choices.branches.some(x => x.id === form.branchId && x.companyId === companyId) ? form.branchId : '';
  const manager = form.managerId && choices.managers.some(x => String(x.id) === form.managerId) ? form.managerId : '';
  return {
    ...form,
    companyId,
    designationId: sharedOrOwnedBy(form.designationId, choices.designations),
    gradeId: sharedOrOwnedBy(form.gradeId, choices.grades),
    branchId: branch,
    departmentId: sharedOrOwnedBy(form.departmentId, choices.departments),
    managerId: manager,
  };
}
