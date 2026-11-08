// The employee's own older profile change requests (GET /api/ess/profile-change-requests/mine) and where each stands.
// Import-free so tests/profileChangeRequests.test.ts runs it under node --test.

export type ChangeRequestState = 'pendingHr' | 'awaitingFinance' | 'approved' | 'rejected' | 'expired' | 'other';

export interface ChangeRequest {
  id: string;
  state: ChangeRequestState;
  /** The server's status word, kept for an unknown state. */
  status: string;
  createdAt: string | null;
  fields: string[];
  /** HR's reason when the request was sent back. */
  note: string | null;
}

export function changeRequestStatus(status: string | null | undefined): ChangeRequestState {
  switch (status) {
    case 'PendingHR': return 'pendingHr';
    case 'AwaitingFinance': return 'awaitingFinance';
    case 'Approved': return 'approved';
    case 'Rejected': return 'rejected';
    case 'Expired': return 'expired';
    default: return 'other';
  }
}

export function mapChangeRequests(raw: unknown): ChangeRequest[] {
  if (!Array.isArray(raw)) return [];
  const rows: ChangeRequest[] = [];
  for (const r of raw) {
    if (!r || typeof r !== 'object') continue;
    const row = r as Record<string, unknown>;
    const id = typeof row.id === 'string' ? row.id : null;
    if (!id) continue;
    const note = typeof row.reviewNote === 'string' && row.reviewNote.trim() ? row.reviewNote.trim() : null;
    rows.push({
      id,
      state: changeRequestStatus(typeof row.status === 'string' ? row.status : null),
      status: typeof row.status === 'string' ? row.status : '',
      createdAt: typeof row.createdAtUtc === 'string' ? row.createdAtUtc : null,
      fields: Array.isArray(row.fields) ? row.fields.filter((f): f is string => typeof f === 'string') : [],
      note,
    });
  }
  return rows.sort((a, b) => (b.createdAt ?? '').localeCompare(a.createdAt ?? ''));
}
