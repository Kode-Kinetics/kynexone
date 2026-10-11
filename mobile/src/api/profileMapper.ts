import type { EmployeeProfile } from '../types';

/**
 * EssEmployeeProfileDto fields the shared EmployeeProfile type does not carry yet. Optional, and
 * left undefined when the record has no value — the screen hides what it does not know.
 */
export interface EmployeeProfileRecord extends EmployeeProfile {
  /** Employee.Status: Draft, Invited, Active, Suspended, Offboarded, Archived. */
  employmentStatus?: string;
  contractStartDate?: string;
  contractEndDate?: string;
  probationEndDate?: string;
  qid?: string;
  qidExpiry?: string;
  civilId?: string;
  civilIdExpiry?: string;
  /** The employing company's country (ISO-3166 alpha-2), from the session's companies. */
  companyCountry?: string;
}

/** Maps the camel-cased EssEmployeeProfileDto contract to the native model. */
export function mapEmployeeProfile(raw: Record<string, any>): EmployeeProfileRecord {
  return {
    employmentStatus: raw.status || undefined,
    contractStartDate: raw.contractStartDate || undefined,
    contractEndDate: raw.contractEndDate || undefined,
    probationEndDate: raw.probationEndDate || undefined,
    qid: raw.qid || undefined,
    qidExpiry: raw.qidExpiryDate || undefined,
    civilId: raw.civilId || undefined,
    civilIdExpiry: raw.civilIdExpiryDate || undefined,
    id: String(raw.id ?? raw.employeeId ?? ''),
    employeeNumber: raw.employeeCode ?? raw.employeeNumber ?? String(raw.id ?? ''),
    fullName: raw.fullName ?? raw.employeeName ?? '',
    fullNameAr: raw.arabicName || undefined,
    jobTitle: raw.jobTitle ?? raw.designation ?? '',
    department: raw.department ?? '',
    email: raw.workEmail ?? raw.email ?? '',
    workEmail: raw.workEmail ?? raw.email ?? '',
    personalEmail: raw.personalEmail || undefined,
    phone: raw.phone ?? raw.mobile ?? raw.mobilePhone ?? '',
    mobile: raw.phone ?? raw.mobile ?? raw.mobilePhone ?? '',
    mobilePhone: raw.phone ?? raw.mobile ?? raw.mobilePhone ?? '',
    nationality: raw.nationality || undefined,
    dateOfBirth: raw.dateOfBirth || undefined,
    gender: raw.gender || undefined,
    maritalStatus: raw.maritalStatus || undefined,
    dateOfJoining: raw.joiningDate ?? raw.dateOfJoining,
    joinDate: raw.joiningDate ?? raw.dateOfJoining,
    contractType: raw.contractType ?? raw.employmentType,
    employmentType: raw.employmentType ?? raw.contractType,
    workLocation: raw.workLocation || raw.branch || undefined,
    profilePhotoUrl: raw.profilePhotoUrl || undefined,
    passportNumber: raw.passportNumber || undefined,
    passportExpiry: raw.passportExpiryDate ?? raw.passportExpiry ?? undefined,
    visaNumber: raw.visaNumber || undefined,
    visaExpiry: raw.visaExpiryDate ?? raw.visaExpiry ?? undefined,
    iqamaNumber: raw.iqamaNumber || undefined,
    iqamaExpiry: raw.iqamaExpiryDate ?? raw.iqamaExpiry ?? undefined,
    emiratesId: raw.emiratesId || undefined,
    emiratesIdExpiry: raw.emiratesIdExpiryDate ?? raw.emiratesIdExpiry ?? undefined,
    emergencyContacts:
      raw.emergencyContactName || raw.emergencyContactPhone
        ? [{
            id: 'primary',
            name: raw.emergencyContactName ?? '',
            relationship: 'Emergency contact',
            phone: raw.emergencyContactPhone ?? '',
          }]
        : [],
    identityDocuments: [],
  };
}
