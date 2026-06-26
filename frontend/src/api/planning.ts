import client from './client';

export interface EstablishmentRow {
  departmentId: string;
  departmentName: string;
  costCenterId: string | null;
  costCenterName: string;
  approvedHeadcount: number;
  currentHeadcount: number;
  gap: number;
  openRequisitionHeadcount: number;
  monthlyBudgetAmount: number;
  currentMonthlySpend: number;
}

export interface HeadcountCheckResult {
  hasEstablishment: boolean;
  approvedHeadcount: number;
  currentHeadcount: number;
  openRequisitionHeadcount: number;
  requested: number;
  projected: number;
  withinBudget: boolean;
  message: string;
}

export const planningApi = {
  establishment: () =>
    client.get<EstablishmentRow[]>('/api/planning/establishment').then(r => r.data),

  setEstablishment: (departmentId: string, body: { approvedHeadcount: number; monthlyBudgetAmount: number; costCenterId?: string | null }) =>
    client.patch(`/api/planning/departments/${departmentId}/establishment`, body).then(r => r.data),

  headcountCheck: (params: { departmentId?: string; departmentName?: string; headCount: number }) =>
    client.get<HeadcountCheckResult>('/api/planning/headcount-check', { params }).then(r => r.data),
};
