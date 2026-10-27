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
  workEmailLocal: string;
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
    employeeCode: '', manualCode: false, workEmailLocal: '', arabicName: '', preferredName: '', gender: '', maritalStatus: '',
    nationalityCode: '', idType: '', idNumber: '', idExpiry: '', idChecksumOverrideReason: '', passportNumber: '', passportExpiry: '', dob: '', mobileCountry: '', mobileNational: '',
    personalEmail: '', employmentType: '', contractType: '', contractEndDate: '', probationEndDate: '', workLocation: '', iban: '', molId: '',
    basic: '', housing: '', transport: '', other: '',
  };
}

/** True when the person has typed or chosen anything beyond the pre-selected company. */
export function hasContent(form: HireForm, defaults: HireForm = emptyHireForm()): boolean {
  return (Object.keys(form) as Array<keyof HireForm>).some(k => k !== 'companyId' && form[k] !== defaults[k]);
}
