/**
 * Seed helpers for the attendance CSV import.
 *
 * The server refuses a punch more than five minutes in the future, and the import reports that as a failed
 * ROW inside a 200 response, so a status-only check lets a seed "succeed" while writing nothing. Two rules
 * follow: only import instants that have already happened, and read `failedRows` on every import.
 */

const MIN = 60_000;
const HOUR = 60 * MIN;

const iso = (ms: number) => new Date(ms).toISOString().replace('.000Z', 'Z');

/**
 * The In / Out instants for "today", for a world seeded at `now`.
 *
 * The intent is a day with BOTH punches (Present, not MissingPunch, so no exception for every employee). The
 * natural 08:00-17:00 KSA day (05:00Z-14:00Z) is used whenever it is already in the past. Before that, each
 * instant is pulled back to at most now minus 10 minutes, keeping In before Out. Returns null in the first
 * minutes of the UTC day, when there is no past instant left on today's date to punch at.
 */
export function todayPunchTimes(now: Date): { inUtc: string; outUtc: string } | null {
  const dayStart = Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate());
  const latest = Math.floor((now.getTime() - 10 * MIN) / MIN) * MIN;
  const out = Math.min(dayStart + 14 * HOUR, latest);
  const inn = Math.min(dayStart + 5 * HOUR, out - 10 * MIN);
  if (inn < dayStart) return null;
  return { inUtc: iso(inn), outUtc: iso(out) };
}

/** Throws unless the import (or preview) response reports zero failed rows, naming the first few errors. */
export function assertImportClean(body: unknown, what: string): void {
  const r = (body && typeof body === 'object' ? body : {}) as { failedRows?: unknown; totalRows?: unknown; errors?: Array<{ rowNumber?: number; message?: string }> };
  if (typeof r.failedRows !== 'number') {
    throw new Error(`[bootstrap] ${what}: the import response has no failedRows, so it cannot be shown that every row was accepted.`);
  }
  if (r.failedRows === 0) return;
  const first = (Array.isArray(r.errors) ? r.errors : []).slice(0, 3).map((e) => `row ${e.rowNumber}: ${e.message}`).join('\n  ');
  throw new Error(`[bootstrap] ${what}: ${r.failedRows} of ${r.totalRows ?? '?'} row(s) failed.\n  ${first}`);
}
