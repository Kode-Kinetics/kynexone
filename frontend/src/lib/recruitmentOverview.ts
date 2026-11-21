import type { JobOpening } from '../api/recruitment';

/**
 * The openings the Recruitment overview counts and lists. The "Open Positions" tile (and the API stats behind it) count an opening
 * as live while it is Open or In Progress (it has an applicant), so the "Active Job Openings" card lists the same two statuses.
 */
export const OVERVIEW_OPENING_STATUSES: readonly string[] = ['Open', 'InProgress'];

/** Merge the per-status pages into one preview: only the overview statuses, each opening once, newest first, at most `limit`. */
export function mergeOverviewOpenings(lists: JobOpening[][], limit = 5): JobOpening[] {
  const seen = new Set<string>();
  return lists.flat()
    .filter(o => OVERVIEW_OPENING_STATUSES.includes(o.status) && !seen.has(o.id) && !!seen.add(o.id))
    .sort((a, b) => (b.createdAtUtc ?? '').localeCompare(a.createdAtUtc ?? ''))
    .slice(0, limit);
}
