import type { HireForm } from './hireForm';

/** Where a server field name (camelCase, dotted for nested) lands in the form. Unknown names return null (shown as a banner). */
const MAP: Record<string, keyof HireForm> = {
  englishName: 'englishName', arabicName: 'arabicName', preferredName: 'preferredName', dateOfBirth: 'dob',
  nationality: 'nationalityCode', nationalityCode: 'nationalityCode', mobileNumber: 'mobileNational', personalEmail: 'personalEmail',
  workEmail: 'workEmailLocal', companyId: 'companyId', branchId: 'branchId', departmentId: 'departmentId', designationId: 'designationId',
  gradeId: 'gradeId', reportingManagerEmployeeId: 'managerId', joiningDate: 'startDate', contractEndDate: 'contractEndDate',
  contractType: 'contractType', employmentType: 'employmentType', workLocation: 'workLocation', employeeCode: 'employeeCode',
  idNumber: 'idNumber', 'payrollProfile.iban': 'iban', 'payrollProfile.molId': 'molId',
  'salaryBreakdown.basicSalary': 'basic', 'salaryBreakdown.housingAllowance': 'housing',
  'salaryBreakdown.transportAllowance': 'transport', 'salaryBreakdown.otherAllowance': 'other',
};

export function serverFieldToForm(field: string): keyof HireForm | null {
  const direct = MAP[field];
  if (direct) return direct;
  if (/^complianceRecords\[\d+\]\.expiryDate$/.test(field)) return 'idExpiry';
  if (/^complianceRecords\[\d+\]\.fieldValue$/.test(field)) return 'idNumber';
  return null;
}
