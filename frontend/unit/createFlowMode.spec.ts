import { expect, test } from '@playwright/test';
import { createFlowMode, FLAGS_WAIT_MS } from '../src/lib/hire/createFlowMode';

// CD57: the legacy dialog is the default; the new wizard needs a positive "ON"; the screen is never blank.
test.describe('Add Employee entry mode', () => {
  test('the wait for the flags is 4 seconds', () => expect(FLAGS_WAIT_MS).toBe(4000));
  test('flags still loading, inside the wait: an accessible loading state, not a blank screen', () =>
    expect(createFlowMode({ status: 'loading', wizardOn: false, waitedOut: false })).toBe('loading'));
  test('flags unanswered after the wait: the legacy dialog', () =>
    expect(createFlowMode({ status: 'loading', wizardOn: false, waitedOut: true })).toBe('legacy'));
  test('flags errored: the legacy dialog immediately', () =>
    expect(createFlowMode({ status: 'error', wizardOn: false, waitedOut: false })).toBe('legacy'));
  test('flags ready, hire_wizard_v2 ON: the new wizard', () =>
    expect(createFlowMode({ status: 'ready', wizardOn: true, waitedOut: false })).toBe('wizard'));
  test('flags ready, hire_wizard_v2 OFF: the legacy dialog', () =>
    expect(createFlowMode({ status: 'ready', wizardOn: false, waitedOut: false })).toBe('legacy'));
  test('a late ON answer after the wait still switches to the wizard', () =>
    expect(createFlowMode({ status: 'ready', wizardOn: true, waitedOut: true })).toBe('wizard'));
});
