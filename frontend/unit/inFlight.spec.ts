import { expect, test } from '@playwright/test';
import { createInFlight } from '../src/lib/inFlight';

/** Double-click protection for async row actions (Leave Approve / Reject / Withdraw, Compliance, Learning, Benefits). */

test('a second call for the same key while the first runs is ignored, so one request is sent', async () => {
  const guard = createInFlight();
  let calls = 0;
  let release!: () => void;
  const gate = new Promise<void>(resolve => { release = resolve; });
  const first = guard.run('approve:1', async () => { calls++; await gate; return 'done'; });
  const second = guard.run('approve:1', async () => { calls++; return 'again'; });
  expect(guard.has('approve:1')).toBe(true);
  expect(await second).toBeUndefined();
  release();
  expect(await first).toBe('done');
  expect(calls).toBe(1);
  expect(guard.has('approve:1')).toBe(false);
});

test('different keys run side by side, and the key frees up after a failure so Retry works', async () => {
  const guard = createInFlight();
  const results = await Promise.all([guard.run('a', async () => 1), guard.run('b', async () => 2)]);
  expect(results).toEqual([1, 2]);
  await expect(guard.run('a', async () => { throw new Error('boom'); })).rejects.toThrow('boom');
  expect(guard.has('a')).toBe(false);
  expect(await guard.run('a', async () => 3)).toBe(3);
});

test('the change callback fires on start and on finish so the button can disable and re-enable', async () => {
  const guard = createInFlight();
  const seen: number[] = [];
  await guard.run('k', async () => undefined, () => seen.push(guard.size));
  expect(seen).toEqual([1, 0]);
});
