/**
 * Guards for list replies from the API.
 *
 * WHY. A list endpoint that answers with something other than a list — a proxy's HTML error page
 * served with 200 during a deploy, `null`, an error object — used to reach `.map` and crash the
 * screen, or (where a caller wrote `data ?? []`) render as "nothing here". Both are wrong: the
 * load FAILED, and the screen must say so. A false empty state tells an HR officer there are no
 * pending approvals when the server simply did not answer.
 *
 * These throw `MalformedListResponseError`, so the caller's existing failure path (its error
 * banner / "could not be loaded" state) runs, exactly as for a network error. They never return
 * an empty list in place of a bad reply.
 */

export class MalformedListResponseError extends Error {
  readonly malformedListResponse = true;
  constructor(what: string) {
    super(`The server sent an unexpected reply for ${what}, so it could not be loaded.`);
    this.name = 'MalformedListResponseError';
  }
}

export function isMalformedListResponse(err: unknown): err is MalformedListResponseError {
  return !!(err as { malformedListResponse?: boolean } | null)?.malformedListResponse;
}

/** The reply must be an array; anything else is a failed load. */
export function requireList<T>(value: unknown, what = 'this list'): T[] {
  if (!Array.isArray(value)) throw new MalformedListResponseError(what);
  return value as T[];
}

/** A paged reply must be an object whose `items` is an array; anything else is a failed load. */
export function requirePage<P extends { items: readonly unknown[] }>(value: unknown, what = 'this list'): P {
  if (!value || typeof value !== 'object' || !Array.isArray((value as { items?: unknown }).items)) {
    throw new MalformedListResponseError(what);
  }
  return value as P;
}
