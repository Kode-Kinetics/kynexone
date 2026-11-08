import { expect, test } from '@playwright/test';
import { clearSessionKeepingLocale } from '../src/api/clearSession';
import { FLASH_RESULT_KEY, FLASH_TTL_MS, clearFlashResult, parseFlashResult, setFlashResult, takeFlashResult } from '../src/lib/flashResult';

const memory = () => {
  const data = new Map<string, string>();
  return { getItem: (k: string) => data.get(k) ?? null, setItem: (k: string, v: string) => void data.set(k, v), removeItem: (k: string) => void data.delete(k), data };
};
const flash = { changed: 'Ali Hassan was saved as a draft.', where: 'You are on the draft record.', next: { label: 'Back to the People list', href: '/people' } };
const T0 = 1_000_000;

test('a result survives one navigation: set before, taken once at the expected page, gone on reload', () => {
  const storage = memory();
  setFlashResult(storage, flash, '/people/12/edit', T0);
  expect(takeFlashResult(storage, '/people/12/edit', T0 + 500)).toEqual({ ...flash, pending: undefined });
  expect(takeFlashResult(storage, '/people/12/edit', T0 + 600)).toBeNull();
  expect(storage.data.has(FLASH_RESULT_KEY)).toBe(false);
});

test('it is shown only on the page it was left for (query and trailing slash ignored)', () => {
  const storage = memory();
  setFlashResult(storage, flash, '/people/12/edit?section=personal', T0);
  expect(takeFlashResult(storage, '/people', T0 + 100)).toBeNull();
  expect(storage.data.has(FLASH_RESULT_KEY), 'a wrong page does not consume it').toBe(true);
  expect(takeFlashResult(storage, '/people/12/edit/', T0 + 200)?.changed).toBe(flash.changed);
});

test('it expires after 60 seconds: a stale flash never greets a later visit, and is cleared when found stale', () => {
  expect(FLASH_TTL_MS).toBe(60_000);
  const storage = memory();
  setFlashResult(storage, flash, '/people/12/edit', T0);
  expect(takeFlashResult(storage, '/people/12/edit', T0 + FLASH_TTL_MS + 1)).toBeNull();
  expect(storage.data.has(FLASH_RESULT_KEY)).toBe(false);
  setFlashResult(storage, flash, '/people/12/edit', T0);
  expect(takeFlashResult(storage, '/people/12/edit', T0 + FLASH_TTL_MS)?.changed).toBe(flash.changed);
});

test('a value with no timestamp or no page is never shown (an old-format or forged entry)', () => {
  const storage = memory();
  storage.setItem(FLASH_RESULT_KEY, JSON.stringify({ changed: 'Saved' }));
  expect(takeFlashResult(storage, '/people', T0)).toBeNull();
  storage.setItem(FLASH_RESULT_KEY, JSON.stringify({ changed: 'Saved', at: T0 }));
  expect(takeFlashResult(storage, '/people', T0)).toBeNull();
});

test('signing out clears it, so the next user on a shared computer never sees it', () => {
  const session = memory();
  setFlashResult(session, flash, '/people/12/edit', T0);
  const local = new Map<string, string>([['zayra_access_token', 'a']]);
  clearSessionKeepingLocale({ getItem: k => local.get(k) ?? null, setItem: (k, v) => void local.set(k, v), clear: () => local.clear() }, session);
  expect(session.data.has(FLASH_RESULT_KEY)).toBe(false);
  clearFlashResult(session); // idempotent
});

test('a malformed, empty or hostile value shows nothing', () => {
  expect(parseFlashResult(null)).toBeNull();
  expect(parseFlashResult('not json')).toBeNull();
  expect(parseFlashResult(JSON.stringify({ where: 'x' }))).toBeNull();
  expect(parseFlashResult(JSON.stringify({ changed: '   ' }))).toBeNull();
});

test('the next action must be an internal path, never an open redirect, and text is capped', () => {
  for (const href of ['https://evil.example/', '//evil.example', 'javascript:alert(1)', '/has space']) {
    expect(parseFlashResult(JSON.stringify({ changed: 'Saved', next: { label: 'Go', href } }))?.next).toBeUndefined();
  }
  expect(parseFlashResult(JSON.stringify({ changed: 'Saved', next: { label: 'Go', href: '/people?x=1' } }))?.next).toEqual({ label: 'Go', href: '/people?x=1' });
  expect(parseFlashResult(JSON.stringify({ changed: 'x'.repeat(5000) }))?.changed).toHaveLength(300);
});

test('a storage that throws never breaks the save', () => {
  const broken = { getItem: () => { throw new Error('denied'); }, setItem: () => { throw new Error('denied'); }, removeItem: () => { throw new Error('denied'); } };
  expect(() => setFlashResult(broken, { changed: 'Saved' }, '/people', T0)).not.toThrow();
  expect(takeFlashResult(broken, '/people', T0)).toBeNull();
});
