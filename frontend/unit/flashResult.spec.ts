import { expect, test } from '@playwright/test';
import { FLASH_RESULT_KEY, parseFlashResult, setFlashResult, takeFlashResult } from '../src/lib/flashResult';

const memory = () => {
  const data = new Map<string, string>();
  return { getItem: (k: string) => data.get(k) ?? null, setItem: (k: string, v: string) => void data.set(k, v), removeItem: (k: string) => void data.delete(k), data };
};

test('a result survives one navigation: set before, taken once after, gone on reload', () => {
  const storage = memory();
  setFlashResult(storage, { changed: 'Ali Hassan was saved as a draft.', where: 'You are on the draft record.', next: { label: 'Back to the People list', href: '/people' } });
  expect(takeFlashResult(storage)).toEqual({
    changed: 'Ali Hassan was saved as a draft.', where: 'You are on the draft record.', pending: undefined, next: { label: 'Back to the People list', href: '/people' },
  });
  expect(takeFlashResult(storage)).toBeNull();
  expect(storage.data.has(FLASH_RESULT_KEY)).toBe(false);
});

test('a malformed, empty or hostile value shows nothing', () => {
  expect(parseFlashResult(null)).toBeNull();
  expect(parseFlashResult('not json')).toBeNull();
  expect(parseFlashResult(JSON.stringify({ where: 'x' }))).toBeNull(); // no "what changed"
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
  expect(() => setFlashResult(broken, { changed: 'Saved' })).not.toThrow();
  expect(takeFlashResult(broken)).toBeNull();
});
