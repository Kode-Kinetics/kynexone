import client from './client';
import type { PagedResult } from './organization';

export interface OrgChartNodeDto {
  id: number;
  employeeCode: string;
  fullName: string;
  designation: string;
  department: string;
  profilePhotoUrl?: string;
  directReports: OrgChartNodeDto[];
}

export interface ReportingLineDto {
  id: string;
  employeeId: number;
  employeeName: string;
  managerEmployeeId: number;
  managerName: string;
  relationshipType: string;
  effectiveFrom: string;
  effectiveTo?: string;
  isPrimary: boolean;
  isActive: boolean;
}

export interface EmployeeListItem {
  id: number;
  employeeCode: string;
  fullName: string;
  arabicName: string;
  department: string;
  designation: string;
  branch: string;
  managerEmployeeId?: number;
  status: string;
  profileCompletenessScore: number;
  visaExpiryDate?: string;
  passportExpiryDate?: string;
  iqamaNumber: string;
}

export interface EmployeePayrollProfileRequest {
  bankName?: string;
  iban?: string;
  accountNumber?: string;
  paymentMethod?: string;
  salaryCurrency?: string;
  payrollGroup?: string;
  salaryStructureReference?: string;
  wpsEligible: boolean;
  eosbEligible: boolean;
  socialInsuranceReference?: string;
  molId?: string;
  bankRoutingCode?: string;
}

export interface EmployeeSalaryBreakdownRequest {
  basicSalary?: number;
  housingAllowance?: number;
  transportAllowance?: number;
  foodAllowance?: number;
  mobileAllowance?: number;
  otherAllowance?: number;
  fixedDeduction?: number;
  salaryStructureCode?: string;
  effectiveDate?: string;
  currency?: string;
}

export interface EmployeeComplianceRecordRequest {
  countryCode: string;
  fieldKey: string;
  fieldLabel: string;
  fieldValue?: string;
  issueDate?: string;
  expiryDate?: string;
  isSensitive: boolean;
  isRequired: boolean;
}

export interface EmployeeCreateRequest {
  employeeCode?: string;
  manualEmployeeCode: boolean;
  englishName: string;
  arabicName?: string;
  preferredName?: string;
  gender: string;
  dateOfBirth?: string;
  nationality?: string;
  maritalStatus?: string;
  personalEmail?: string;
  workEmail?: string;
  mobileNumber?: string;
  profilePhotoUrl?: string;
  companyId?: string;
  branchId?: string;
  departmentId?: string;
  designationId?: string;
  gradeId?: string;
  costCenterId?: string;
  jobTitle?: string;
  reportingManagerEmployeeId?: number;
  secondLevelManagerEmployeeId?: number;
  employmentType?: string;
  contractType?: string;
  joiningDate?: string;
  confirmationDate?: string;
  probationStartDate?: string;
  probationEndDate?: string;
  noticePeriodDays?: number;
  workLocation?: string;
  payrollGroup?: string;
  shiftPolicyCode?: string;
  leavePolicyCode?: string;
  attendancePolicyCode?: string;
  payrollProfile?: EmployeePayrollProfileRequest;
  salaryBreakdown?: EmployeeSalaryBreakdownRequest;
  complianceRecords?: EmployeeComplianceRecordRequest[];
}

export interface EmployeeEntity {
  id: number;
  tenantId?: string;
  employeeCode: string;
  fullName: string;
  englishName: string;
  arabicName: string;
  preferredName: string;
  profilePhotoUrl: string;
  personalEmail: string;
  workEmail: string;
  phone: string;
  gender: string;
  dateOfBirth?: string;
  maritalStatus: string;
  nationality: string;
  countryCode: string;
  companyId?: string;
  branchId?: string;
  departmentId?: string;
  designationId?: string;
  gradeId?: string;
  costCenterId?: string;
  department: string;
  designation: string;
  branch: string;
  workLocation: string;
  managerEmployeeId?: number;
  secondLevelManagerEmployeeId?: number;
  status: string;
  joiningDate?: string;
  confirmationDate?: string;
  probationStartDate?: string;
  probationEndDate?: string;
  contractType: string;
  employmentType: string;
  jobTitle: string;
  grade: string;
  costCenter: string;
  noticePeriodDays?: number;
  payrollProfileCode: string;
  shiftPolicyCode: string;
  leavePolicyCode: string;
  attendancePolicyCode: string;
  profileCompletenessScore: number;
}

export interface EmployeeDocument {
  id: string;
  employeeId?: number;
  documentType: string;
  fileName: string;
  contentType: string;
  storageUrl: string;
  isRequired: boolean;
  issueDate?: string;
  expiryDate?: string;
  renewalReminderDate?: string;
  approvalStatus: string;
  versionNumber: number;
  uploadedAtUtc: string;
}

export interface EmployeeHistory {
  id: string;
  eventType: string;
  fieldName: string;
  oldValue: string;
  newValue: string;
  effectiveDate: string;
  reason: string;
  createdAtUtc: string;
}

export interface EmployeeTransferRequest {
  id: string;
  currentBranch: string;
  currentDepartment: string;
  currentDesignation: string;
  newBranch: string;
  newDepartment: string;
  newDesignation: string;
  newManagerEmployeeId?: number;
  effectiveDate: string;
  reason: string;
  status: string;
  createdAtUtc: string;
}

export interface EmployeeDetail extends EmployeeEntity {
  payrollProfile?: EmployeePayrollProfileRequest;
  complianceRecords: EmployeeComplianceRecordRequest[];
  documents: EmployeeDocument[];
  history: EmployeeHistory[];
  transfers: EmployeeTransferRequest[];
}

export interface EmployeeStatusChangeRequest {
  status: string;
  effectiveDate: string;
  reason: string;
}

export interface EmployeeTransferCreateRequest {
  newBranch?: string;
  newDepartment?: string;
  newDesignation?: string;
  newManagerEmployeeId?: number;
  effectiveDate: string;
  reason: string;
}

export interface EmployeeGroupCount {
  name: string;
  count: number;
}

export interface EmployeeHeadcountReport {
  total: number;
  byCompany: EmployeeGroupCount[];
  byDepartment: EmployeeGroupCount[];
  byStatus: EmployeeGroupCount[];
}

export interface EmployeeMissingDocumentsReport {
  employeeId: number;
  employeeCode: string;
  fullName: string;
  missingDocumentTypes: string[];
}

export interface EmployeeExpiringDocument {
  employeeId: number;
  employeeCode: string;
  fullName: string;
  documentType: string;
  expiryDate?: string;
}

export const employeesApi = {
  list: (params: { search?: string; status?: string; department?: string; page?: number; pageSize?: number } = {}) =>
    client.get<PagedResult<EmployeeListItem>>('/api/employees', {
      params: { page: 1, pageSize: 25, ...params },
    }).then((r) => r.data),

  get: (id: number) => client.get<EmployeeDetail>(`/api/employees/${id}`).then((r) => r.data),

  create: (data: EmployeeCreateRequest) => client.post<EmployeeDetail>('/api/employees', data).then((r) => r.data),

  /** Sends only the changed fields; sensitive fields (salary, passport, bank…) return 202 and go to an approval workflow. */
  update: (id: number, effectiveDate: string, changes: Record<string, unknown>) =>
    client.put(`/api/employees/${id}`, { effectiveDate, changes }),

  remove: (id: number) => client.delete(`/api/employees/${id}`),

  changeStatus: (id: number, data: EmployeeStatusChangeRequest) =>
    client.patch<EmployeeDetail>(`/api/employees/${id}/status`, data).then((r) => r.data),

  activate: (id: number, data: Omit<EmployeeStatusChangeRequest, 'status'>) =>
    client.post<EmployeeDetail>(`/api/employees/${id}/activate`, { ...data, status: 'Active' }).then((r) => r.data),

  terminate: (id: number, data: Omit<EmployeeStatusChangeRequest, 'status'>) =>
    client.post<EmployeeDetail>(`/api/employees/${id}/terminate`, { ...data, status: 'Terminated' }).then((r) => r.data),

  uploadDocument: (id: number, metadata: { documentType: string; issueDate?: string; expiryDate?: string; renewalReminderDate?: string; isRequired: boolean; approvalStatus?: string }, file: File) => {
    const form = new FormData();
    form.append('file', file);
    Object.entries(metadata).forEach(([key, value]) => {
      if (value !== undefined && value !== null) form.append(key, String(value));
    });
    return client.post<EmployeeDocument>(`/api/employees/${id}/documents`, form).then((r) => r.data);
  },

  documents: (id: number) => client.get<EmployeeDocument[]>(`/api/employees/${id}/documents`).then((r) => r.data),

  history: (id: number) => client.get<EmployeeHistory[]>(`/api/employees/${id}/history`).then((r) => r.data),

  transfer: (id: number, data: EmployeeTransferCreateRequest) =>
    client.post<EmployeeTransferRequest>(`/api/employees/${id}/transfer`, data).then((r) => r.data),

  reports: {
    headcount: () => client.get<EmployeeHeadcountReport>('/api/employees/reports/headcount').then((r) => r.data),
    expiringDocuments: (days = 60) =>
      client.get<EmployeeExpiringDocument[]>('/api/employees/reports/expiring-documents', { params: { days } }).then((r) => r.data),
    missingDocuments: () =>
      client.get<EmployeeMissingDocumentsReport[]>('/api/employees/reports/missing-documents').then((r) => r.data),
    statusSummary: () =>
      client.get<{ statuses: EmployeeGroupCount[] }>('/api/employees/reports/status-summary').then((r) => r.data),
  },

  orgChart: (rootEmployeeId?: number, maxDepth = 5) =>
    client.get<OrgChartNodeDto[]>('/api/employees/org-chart', { params: { rootEmployeeId, maxDepth } }).then((r) => r.data),

  setManager: (id: number, managerEmployeeId: number | null) =>
    client.put(`/api/employees/${id}/manager`, { managerEmployeeId }).then((r) => r.data),

  reportingLines: (id: number) =>
    client.get<ReportingLineDto[]>(`/api/employees/${id}/reporting-lines`).then((r) => r.data),
};
