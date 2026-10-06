import { test, expect } from '@playwright/test';
import { isMalformedListResponse, requireList, requirePage } from '../src/lib/listResponse';
import { fetchAllPages } from '../src/lib/paging';
import { requestFailureReason } from '../src/lib/requestFailure';

// The reported defect: the loan-policies list crashed on a reply that was not an array, and
// elsewhere `data ?? []` turned a failed load into "No … yet". A bad reply is a FAILED load:
// it must reach the screen's error state, never a false empty state.

const PROXY_HTML = '<!doctype html><html><body>502 Bad Gateway</body></html>';

test('a real list passes through unchanged, including an empty one', () => {
  expect(requireList([{ id: 1 }])).toEqual([{ id: 1 }]);
  expect(requireList([])).toEqual([]);
});

test('anything that is not an array is a failed load, not an empty list', () => {
  for (const bad of [null, undefined, PROXY_HTML, { items: [] }, { message: 'error' }, 42]) {
    expect(() => requireList(bad, 'loan policies')).toThrow(/unexpected reply for loan policies/);
  }
  try { requireList(PROXY_HTML); } catch (e) { expect(isMalformedListResponse(e)).toBe(true); }
});

test('a paged reply must carry an items array', () => {
  expect(requirePage({ items: [], total: 0 })).toEqual({ items: [], total: 0 });
  for (const bad of [null, PROXY_HTML, [], { total: 3 }, { items: null }, { items: 'x' }]) {
    expect(() => requirePage(bad)).toThrow(/unexpected reply/);
  }
});

test('fetchAllPages rejects a malformed page instead of ending the loop with zero rows', async () => {
  // Before: `result.items ?? []` read an HTML string as "no rows" and returned [] (a false empty list).
  await expect(fetchAllPages(async () => PROXY_HTML as never)).rejects.toThrow(/unexpected reply/);
  await expect(fetchAllPages(async () => ({ total: 5 }) as never)).rejects.toThrow(/unexpected reply/);
});

test('fetchAllPages still returns a genuinely empty list as empty', async () => {
  await expect(fetchAllPages(async () => ({ items: [], total: 0 }))).resolves.toEqual([]);
});

test('the failure reason for a malformed reply says so, rather than a vague "something went wrong"', () => {
  let err: unknown;
  try { requireList(null); } catch (e) { err = e; }
  expect(requestFailureReason(err)).toBe('The server sent an unexpected reply. Retry in a moment.');
});

test('a 403 keeps the server reason in plain words', () => {
  const sod = { isAxiosError: true, response: { status: 403, data: { code: 'forbidden', message: 'The user who processed this run cannot approve it.' } } };
  expect(requestFailureReason(sod)).toBe('You do not have permission for this. The user who processed this run cannot approve it.');
  // An empty-body 403 (a policy refusal) or a bare status word still reads as plain words.
  expect(requestFailureReason({ isAxiosError: true, response: { status: 403 } })).toBe('You do not have permission for this.');
  expect(requestFailureReason({ isAxiosError: true, response: { status: 403, data: { message: 'Forbidden' } } })).toBe('You do not have permission for this.');
  expect(requestFailureReason({ isAxiosError: true, response: { status: 403, data: { message: 42 } } })).toBe('You do not have permission for this.');
});
