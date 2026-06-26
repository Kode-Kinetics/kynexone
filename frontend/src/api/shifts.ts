import client from './client';

export interface ShiftDefinition {
  id: string;
  tenantId: string;
  code: string;
  name: string;
  startTime: string;
  endTime: string;
  breakMinutes: number;
  color: string;
  isActive: boolean;
  createdAtUtc: string;
}

export interface RosterEmployee {
  id: number;
  fullName: string;
  department: string;
  employeeCode: string;
}

export interface RosterAssignment {
  id: string;
  employeeId: number;
  date: string;
  shiftDefinitionId: string;
  shiftName: string;
  shiftCode: string;
  shiftColor: string;
}

export interface RosterResponse {
  from: string;
  to: string;
  employees: RosterEmployee[];
  assignments: RosterAssignment[];
}

// ── Intelligent rostering policy + AI planner ─────────────────────────────────

export interface GenderShiftRule { gender: string; shiftCodes: string[]; mode: 'required' | 'preferred'; }
export interface DemandTarget { shiftCode: string; headcount: number; }
export interface ShiftPolicy {
  genderRules: GenderShiftRule[];
  voluntaryShiftCodes: string[];
  weekendDemand: DemandTarget[];
  holidayDemand: DemandTarget[];
  minRestHours: number;
  maxConsecutiveDays: number;
}

export interface ProposedAssignment {
  employeeId: number;
  employeeName: string;
  date: string;
  shiftDefinitionId: string;
  shiftCode: string;
  shiftName: string;
  shiftColor: string;
  reason: string;
}

export interface RosterPlanResult {
  assignments: ProposedAssignment[];
  warnings: string[];
  engine: string;
  summary: string;
}

export const shiftsApi = {
  listDefinitions: () =>
    client.get<ShiftDefinition[]>('/api/shifts/definitions').then((r) => r.data),

  createDefinition: (body: {
    code: string;
    name: string;
    startTime: string;
    endTime: string;
    breakMinutes: number;
    color: string;
  }) => client.post<ShiftDefinition>('/api/shifts/definitions', body).then((r) => r.data),

  updateDefinition: (
    id: string,
    body: { code: string; name: string; startTime: string; endTime: string; breakMinutes: number; color: string }
  ) => client.put<ShiftDefinition>(`/api/shifts/definitions/${id}`, body).then((r) => r.data),

  deleteDefinition: (id: string) => client.delete(`/api/shifts/definitions/${id}`),

  getRoster: (from: string, to: string) =>
    client.get<RosterResponse>('/api/shifts/roster', { params: { from, to } }).then((r) => r.data),

  assign: (body: { employeeId: number; shiftDefinitionId: string; date: string; notes?: string }) =>
    client.post('/api/shifts/roster/assign', body).then((r) => r.data),

  removeAssignment: (id: string) => client.delete(`/api/shifts/roster/${id}`),

  autoPlan: (body: {
    dateFrom: string;
    dateTo: string;
    shiftIds: string[];
    pattern: 'fixed' | 'alternating' | 'rotating';
    skipWeekend: boolean;
    overwriteExisting: boolean;
    employeeIds?: number[];
  }) => client.post<{ created: number; skipped: number; employees: number; days: number }>('/api/shifts/roster/auto-plan', body).then(r => r.data),

  getPolicy: () => client.get<ShiftPolicy>('/api/shifts/policy').then(r => r.data),

  savePolicy: (policy: ShiftPolicy) => client.put<ShiftPolicy>('/api/shifts/policy', policy).then(r => r.data),

  aiPlan: (body: { dateFrom: string; dateTo: string; employeeIds?: number[]; weekendDays?: string[] }) =>
    client.post<RosterPlanResult>('/api/shifts/roster/ai-plan', body).then(r => r.data),

  commitPlan: (body: {
    dateFrom: string;
    dateTo: string;
    overwriteExisting: boolean;
    assignments: { employeeId: number; date: string; shiftDefinitionId: string }[];
  }) => client.post<{ created: number; updated: number; skipped: number }>('/api/shifts/roster/ai-plan/commit', body).then(r => r.data),
};
