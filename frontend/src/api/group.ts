import client from './client';

export interface GroupDashboardCompany {
  companyId: string;
  name: string;
  code: string;
  countryCode: string;
  isActive: boolean;
  approvalStatus: 'Active' | 'Draft' | 'PendingActivation';
  headcount: number;
  latestPayrollStatus: string | null;
  latestPayrollPeriod: string | null;
  pendingLeave: number;
  absences30d: number;
  expiringDocs60d: number;
  pendingApprovals: number;
  complianceReady: boolean;
  complianceMissingCount: number;
}

export interface GroupDashboard {
  accountType: 'SingleCompany' | 'Group';
  companies: GroupDashboardCompany[];
}

export const groupApi = {
  dashboard: () => client.get<GroupDashboard>('/api/group/dashboard').then((r) => r.data),
};
