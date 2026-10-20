import { expect, test } from '@playwright/test';
import {
  cancelIntent,
  confirmIntent,
  createNavigationGuardState,
  queueIntent,
} from '../src/lib/navigationGuardState';

test('queueIntent keeps only the latest requested destination', () => {
  const state = createNavigationGuardState();

  expect(queueIntent(state, { destination: '/people/1', run: () => {} })).toBe('queued');
  expect(queueIntent(state, { destination: '/payroll', run: () => {} })).toBe('queued');

  expect(state.snapshot().intent?.destination).toBe('/payroll');
});

test('cancelIntent clears the queued destination and restores focus to its trigger', () => {
  let focusCount = 0;
  const state = createNavigationGuardState();
  const restoreFocus = { focus: () => { focusCount += 1; } } as unknown as HTMLElement;
  queueIntent(state, { destination: '/people', run: () => {}, restoreFocus });

  cancelIntent(state);

  expect(state.snapshot().intent).toBeNull();
  expect(focusCount).toBe(1);
});

test('confirmIntent discards every active draft before running navigation', async () => {
  const order: string[] = [];
  const state = createNavigationGuardState();
  queueIntent(state, {
    destination: '/dashboard',
    run: () => { order.push('navigate'); },
  });

  await confirmIntent(state, [
    async () => { order.push('discard employee'); },
    async () => { order.push('discard setup'); },
  ]);

  expect(order).toEqual(['discard employee', 'discard setup', 'navigate']);
  expect(state.snapshot().intent).toBeNull();
});

test('confirmed navigation runs once when interception re-enters through its one-shot bypass', async () => {
  let actionCount = 0;
  const state = createNavigationGuardState();
  const intent = {
    destination: '/people',
    run: () => {
      actionCount += 1;
      expect(queueIntent(state, intent)).toBe('bypass');
    },
  };
  queueIntent(state, intent);

  await Promise.all([
    confirmIntent(state, [async () => {}]),
    confirmIntent(state, [async () => {}]),
  ]);

  expect(actionCount).toBe(1);
  expect(state.snapshot().intent).toBeNull();
  expect(queueIntent(state, intent)).toBe('queued');
});

test('failed discard keeps the intent open with an announced error and never navigates', async () => {
  let actionCount = 0;
  const state = createNavigationGuardState();
  queueIntent(state, { destination: '/people', run: () => { actionCount += 1; } });

  await expect(confirmIntent(state, [async () => { throw new Error('Draft could not be discarded.'); }]))
    .rejects.toThrow('Draft could not be discarded.');

  expect(actionCount).toBe(0);
  expect(state.snapshot().intent?.destination).toBe('/people');
  expect(state.snapshot().error).toBe('Draft could not be discarded.');
  expect(state.snapshot().busy).toBe(false);
});

test('replacing an intent advances identity and clears a same-message failure', async () => {
  const state = createNavigationGuardState();
  queueIntent(state, { destination: '/people', run: () => {} });
  const firstSequence = state.snapshot().sequence;
  await expect(confirmIntent(state, [async () => { throw new Error('Could not discard.'); }])).rejects.toThrow();

  queueIntent(state, { destination: '/payroll', run: () => {} });

  expect(state.snapshot().sequence).toBe(firstSequence + 1);
  expect(state.snapshot().error).toBe('');
});
