import { MalformedListResponseError, requirePage } from './listResponse';

/**
 * Open positions (Self-Service): the jobs open in the employee's own company, read-only.
 * Pure helpers for the screen and its API module; nothing here touches the network or React.
 */

export const ESS_JOBS_PATH = '/ess/jobs';
/** Well under the API's 100-row cap, and a screenful of rows. */
export const ESS_JOBS_PAGE_SIZE = 25;

/** One open position as GET /api/ess/jobs answers. Pay is deliberately not part of it. */
export interface EssJob {
  id: string;
  jobCode: string;
  title: string;
  departmentName: string;
  designationTitle: string;
  employmentType: string;
  location: string;
  description: string;
  /** Newline-separated. */
  requirements: string;
  /** Newline-separated. */
  responsibilities: string;
  publishedAtUtc: string | null;
}

export interface EssJobsPage {
  items: EssJob[];
  total: number;
  page: number;
  pageSize: number;
}

const text = (value: unknown): string => (typeof value === 'string' ? value : '');

/** A job with every text field a string, so the screen never prints "undefined". */
export function normalizeJob(raw: unknown): EssJob {
  const r = (raw && typeof raw === 'object' ? raw : {}) as Record<string, unknown>;
  const published = typeof r.publishedAtUtc === 'string' && r.publishedAtUtc ? r.publishedAtUtc : null;
  return {
    id: text(r.id), jobCode: text(r.jobCode), title: text(r.title), departmentName: text(r.departmentName),
    designationTitle: text(r.designationTitle), employmentType: text(r.employmentType), location: text(r.location),
    description: text(r.description), requirements: text(r.requirements), responsibilities: text(r.responsibilities),
    publishedAtUtc: published,
  };
}

/**
 * The list reply, checked. A reply that is not `{ items: [...], total: n }` (a proxy's HTML page, null, a page with no
 * count) is a FAILED load: the screen would otherwise show "no open positions" or a range it cannot vouch for.
 */
export function readJobsPage(value: unknown): EssJobsPage {
  const page = requirePage<{ items: unknown[]; total?: unknown; page?: unknown; pageSize?: unknown }>(value, 'open positions');
  if (typeof page.total !== 'number' || !Number.isFinite(page.total) || page.total < 0) throw new MalformedListResponseError('open positions');
  const num = (v: unknown, fallback: number) => (typeof v === 'number' && Number.isFinite(v) ? v : fallback);
  return {
    items: page.items.map(normalizeJob),
    total: Math.floor(page.total),
    page: num(page.page, 1),
    pageSize: num(page.pageSize, ESS_JOBS_PAGE_SIZE),
  };
}

/** Query params: an explicit page and pageSize every time, and `q` only when there is something to search for. */
export function jobsParams({ q, page }: { q?: string; page: number }): { q?: string; page: number; pageSize: number } {
  const search = (q ?? '').trim();
  const safePage = Number.isFinite(page) ? Math.max(1, Math.floor(page)) : 1;
  return { ...(search ? { q: search } : {}), page: safePage, pageSize: ESS_JOBS_PAGE_SIZE };
}

export function lastPage(total: number, pageSize: number): number {
  return Math.max(1, Math.ceil(total / pageSize));
}

/** "26 to 50 of 60": from the server's total and the rows that actually arrived, never from the page size alone. */
export function pageRange(page: number, pageSize: number, total: number, count: number): { from: number; to: number; pages: number } {
  const pages = lastPage(total, pageSize);
  if (total <= 0 || count <= 0) return { from: 0, to: 0, pages };
  const from = (page - 1) * pageSize + 1;
  return { from, to: from + count - 1, pages };
}

/** A newline-separated text as list items: trimmed, blank lines dropped, a typed bullet marker removed. */
export function splitLines(value: string | null | undefined): string[] {
  if (!value) return [];
  return value
    .split(/\r?\n/)
    .map((line) => line.replace(/^\s*[-–—•*·]+\s*/, '').trim())
    .filter((line) => line.length > 0);
}

export type JobsLoadProblem =
  | { kind: 'unlinked'; message: string | null }
  | { kind: 'forbidden' }
  | { kind: 'failed'; error: unknown };

/**
 * Why the list or a position could not be read. 404 means the login has no employee record (retrying cannot fix
 * that, so it is explained instead); 403 means the role lacks self-service access; anything else is a failed load.
 */
export function jobsLoadProblem(err: unknown, locale: string): JobsLoadProblem {
  const res = (err as { response?: { status?: number; data?: { message?: unknown; messageAr?: unknown } } } | null)?.response;
  if (res?.status === 404) {
    const pick = locale === 'ar' ? res.data?.messageAr ?? res.data?.message : res.data?.message;
    return { kind: 'unlinked', message: typeof pick === 'string' && pick.trim() ? pick.trim() : null };
  }
  if (res?.status === 403) return { kind: 'forbidden' };
  return { kind: 'failed', error: err };
}
