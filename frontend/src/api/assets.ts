import client from './client';

// ── W2-C: asset and equipment custody ──────────────────────────────────────────
// Mirrors backend Application/Assets/AssetDtos.cs. Field names are the camelCase
// form of the C# records.

export type AssetStatus = 'InStock' | 'Assigned' | 'InRepair' | 'Retired' | 'Lost';
export type AssignmentStatus = 'Active' | 'Returned' | 'Transferred' | 'WrittenOff';
export type WriteOffStatus = 'Pending' | 'Approved' | 'Rejected';

export const ASSET_STATUSES: AssetStatus[] = ['InStock', 'Assigned', 'InRepair', 'Retired', 'Lost'];

export interface AssetHolder {
  assignmentId: string;
  employeeId: number;
  employeeName: string;
  employeeCode: string;
  issuedOn: string;
  expectedReturnDate: string | null;
  isOverdue: boolean;
}

export interface AssetListItem {
  id: string;
  assetTag: string;
  name: string;
  serialNumber: string;
  categoryCode: string;
  make: string;
  model: string;
  status: AssetStatus;
  condition: string;
  companyId: string | null;
  branchId: string | null;
  locationId: string | null;
  locationNote: string;
  purchaseDate: string | null;
  purchaseCost: number | null;
  currency: string;
  currentHolder: AssetHolder | null;
  hasPendingWriteOff: boolean;
  version: number;
}

export interface AssetAssignment {
  id: string;
  assetId: string;
  assetTag: string;
  assetName: string;
  categoryCode: string;
  employeeId: number;
  employeeName: string;
  employeeCode: string;
  status: AssignmentStatus;
  issuedOn: string;
  issuedAtUtc: string;
  issuedByName: string;
  conditionOnIssue: string;
  issueNotes: string;
  expectedReturnDate: string | null;
  returnedOn: string | null;
  closedAtUtc: string | null;
  closedByName: string;
  conditionOnReturn: string;
  returnNotes: string;
  transferredToAssignmentId: string | null;
  writeOffRequestId: string | null;
  isOverdue: boolean;
}

export interface AssetWriteOff {
  id: string;
  assetId: string;
  assignmentId: string | null;
  employeeId: number | null;
  kind: 'Lost' | 'Damaged';
  reason: string;
  status: WriteOffStatus;
  approvalRequestId: string | null;
  requestedByName: string;
  requestedAtUtc: string;
  decidedAtUtc: string | null;
  decisionComments: string;
}

export interface AssetAuditEntry {
  action: string;
  atUtc: string;
  userId: string | null;
  metadata: string | null;
}

export interface AssetDetail {
  asset: AssetListItem;
  assignments: AssetAssignment[];
  writeOffs: AssetWriteOff[];
  auditTrail: AssetAuditEntry[];
}

export interface AssetSummary {
  total: number;
  byStatus: Record<AssetStatus, number>;
  overdue: number;
  dueSoon: number;
  pendingWriteOffs: number;
  byCategory: { categoryCode: string; count: number }[];
}

export interface AssetLookupValue { code: string; label: string }
export interface AssetLookups { categories: AssetLookupValue[]; conditions: AssetLookupValue[] }

export interface AssetClearance {
  employeeId: number;
  clear: boolean;
  outstandingCount: number;
  outstanding: AssetAssignment[];
  pendingWriteOffs: AssetWriteOff[];
}

export interface EmployeeAssets {
  employeeId: number;
  current: AssetAssignment[];
  history: AssetAssignment[];
}

export interface AssetPagedResult {
  total: number;
  page: number;
  pageSize: number;
  items: AssetListItem[];
}

export interface AssetUpsert {
  assetTag: string;
  name?: string;
  serialNumber?: string;
  categoryCode?: string;
  make?: string;
  model?: string;
  purchaseDate?: string | null;
  purchaseCost?: number | null;
  currency?: string;
  condition?: string;
  companyId?: string | null;
  branchId?: string | null;
  locationId?: string | null;
  locationNote?: string;
  notes?: string;
}

export interface AssetListQuery {
  status?: string;
  category?: string;
  employeeId?: number;
  companyId?: string;
  q?: string;
  overdue?: boolean;
  page?: number;
  pageSize?: number;
}

export const assetsApi = {
  list: (params: AssetListQuery) =>
    client.get<AssetPagedResult>('/api/assets', { params }).then(r => r.data),
  summary: () => client.get<AssetSummary>('/api/assets/summary').then(r => r.data),
  lookups: () => client.get<AssetLookups>('/api/assets/lookups').then(r => r.data),
  get: (id: string) => client.get<AssetDetail>(`/api/assets/${id}`).then(r => r.data),
  forEmployee: (employeeId: number) =>
    client.get<EmployeeAssets>(`/api/assets/employees/${employeeId}`).then(r => r.data),
  clearance: (employeeId: number) =>
    client.get<AssetClearance>(`/api/assets/clearance/${employeeId}`).then(r => r.data),

  create: (body: AssetUpsert) => client.post<AssetDetail>('/api/assets', body).then(r => r.data),
  update: (id: string, body: AssetUpsert) => client.put<AssetDetail>(`/api/assets/${id}`, body).then(r => r.data),
  issue: (id: string, body: { employeeId: number; issuedOn?: string | null; expectedReturnDate?: string | null; condition?: string; notes?: string }) =>
    client.post<AssetDetail>(`/api/assets/${id}/issue`, body).then(r => r.data),
  return: (id: string, body: { condition: string; returnedOn?: string | null; notes?: string; sendToRepair?: boolean }) =>
    client.post<AssetDetail>(`/api/assets/${id}/return`, body).then(r => r.data),
  transfer: (id: string, body: { toEmployeeId: number; expectedReturnDate?: string | null; condition?: string; notes?: string }) =>
    client.post<AssetDetail>(`/api/assets/${id}/transfer`, body).then(r => r.data),
  writeOff: (id: string, body: { kind: 'Lost' | 'Damaged'; reason: string }) =>
    client.post<AssetDetail>(`/api/assets/${id}/write-off`, body).then(r => r.data),
  retire: (id: string, body: { reason: string }) =>
    client.post<AssetDetail>(`/api/assets/${id}/retire`, body).then(r => r.data),
  repair: (id: string, body: { action: 'Send' | 'Complete'; condition?: string; notes?: string }) =>
    client.post<AssetDetail>(`/api/assets/${id}/repair`, body).then(r => r.data),
  extendReturn: (assignmentId: string, expectedReturnDate: string | null) =>
    client.patch<AssetDetail>(`/api/assets/assignments/${assignmentId}/expected-return`, { expectedReturnDate }).then(r => r.data),

  /** ESS: the caller's own custody rows only (api/ess/assets). */
  mine: () => client.get<EmployeeAssets>('/api/ess/assets').then(r => r.data),
};

export const ASSET_STATUS_TONE: Record<AssetStatus, 'blue' | 'cyan' | 'emerald' | 'amber' | 'rose' | 'slate'> = {
  InStock: 'emerald',
  Assigned: 'blue',
  InRepair: 'amber',
  Retired: 'slate',
  Lost: 'rose',
};

export const ASSET_STATUS_LABEL: Record<AssetStatus, string> = {
  InStock: 'In stock',
  Assigned: 'Assigned',
  InRepair: 'In repair',
  Retired: 'Retired',
  Lost: 'Lost',
};

export const ASSIGNMENT_STATUS_LABEL: Record<AssignmentStatus, string> = {
  Active: 'Held',
  Returned: 'Returned',
  Transferred: 'Transferred',
  WrittenOff: 'Written off',
};
