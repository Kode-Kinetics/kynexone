import { expect, test } from '@playwright/test';
import { RefreshQueue } from '../src/api/refreshQueue';

test('one successful refresh releases every queued request and clears the queue', async () => {
  const queue = new RefreshQueue();
  const first = queue.wait();
  const second = queue.wait();

  expect(queue.size).toBe(2);
  queue.resolve('replacement-access-token');

  await expect(first).resolves.toBe('replacement-access-token');
  await expect(second).resolves.toBe('replacement-access-token');
  expect(queue.size).toBe(0);
});

test('a failed refresh rejects every queued request instead of leaving requests pending', async () => {
  const queue = new RefreshQueue();
  const first = queue.wait();
  const second = queue.wait();
  const failure = new Error('refresh rejected');

  queue.reject(failure);

  await expect(first).rejects.toThrow('refresh rejected');
  await expect(second).rejects.toThrow('refresh rejected');
  expect(queue.size).toBe(0);
});
