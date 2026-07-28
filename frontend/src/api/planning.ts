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
  /** Present when designationId was supplied: advisory per-level verdict merged by the establishment guard. */
  levelWithinBudget?: boolean;
  levelMessage?: string;
}

export const planningApi = {
  establishment: () =>
    client.get<EstablishmentRow[]>('/api/planning/establishment').then(r => r.data),

  /** Reason is required by the server when approvedHeadcount/monthlyBudgetAmount change (audited). */
  setEstablishment: (departmentId: string, body: { approvedHeadcount: number; monthlyBudgetAmount: number; costCenterId?: string | null; reason?: string }) =>
    client.patch(`/api/planning/departments/${departmentId}/establishment`, body).then(r => r.data),

  /** designationId is advisory only (warn-before-submit); the guard's 409 remains authoritative. */
  headcountCheck: (params: { departmentId?: string; departmentName?: string; headCount: number; designationId?: string }) =>
    client.get<HeadcountCheckResult>('/api/planning/headcount-check', { params }).then(r => r.data),
};
