import type { HireForm } from './hireForm';

/** Where a server field name (camelCase, dotted for nested) lands in the form. Unknown names return null (shown as a banner). */
const MAP: Record<string, keyof HireForm> = {
  englishName: 'englishName', arabicName: 'arabicName', preferredName: 'preferredName', dateOfBirth: 'dob',
  nationality: 'nationalityCode', nationalityCode: 'nationalityCode', mobileNumber: 'mobileNational', personalEmail: 'personalEmail',
  workEmail: 'workEmail', companyId: 'companyId', branchId: 'branchId', departmentId: 'departmentId', designationId: 'designationId',
  gradeId: 'gradeId', reportingManagerEmployeeId: 'managerId', joiningDate: 'startDate', contractEndDate: 'contractEndDate',
  probationEndDate: 'probationEndDate', contractType: 'contractType', employmentType: 'employmentType', workLocation: 'workLocation', employeeCode: 'employeeCode',
  idNumber: 'idNumber', 'payrollProfile.iban': 'iban', 'payrollProfile.molId': 'molId',
  'salaryBreakdown.basicSalary': 'basic', 'salaryBreakdown.housingAllowance': 'housing',
  'salaryBreakdown.transportAllowance': 'transport', 'salaryBreakdown.otherAllowance': 'other',
};

export function serverFieldToForm(field: string): keyof HireForm | null {
  const direct = MAP[field];
  if (direct) return direct;
  const rec = /^complianceRecords\[(\d+)\]\.(expiryDate|fieldValue)$/.exec(field);
  if (rec) {
    // Record 0 is the primary identity; record 1 is the passport (CD27).
    const second = rec[1] === '1';
    return rec[2] === 'expiryDate' ? (second ? 'passportExpiry' : 'idExpiry') : (second ? 'passportNumber' : 'idNumber');
  }
  return null;
}
