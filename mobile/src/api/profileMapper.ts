import type { EmployeeProfile } from '../types';

/** Maps the camel-cased EssEmployeeProfileDto contract to the native model. */
export function mapEmployeeProfile(raw: Record<string, any>): EmployeeProfile {
  return {
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
