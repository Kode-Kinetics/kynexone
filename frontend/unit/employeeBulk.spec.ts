import { expect, test } from '@playwright/test';
import { bulkFailureMessage, countCannotActivateYet } from '../src/lib/employeeBulk';

const row = (status: string, readinessState: string, activationBlockersCount = 0) => ({ status, readinessState, activationBlockersCount });
const failure = (status?: number, message?: string) => (status === undefined ? new Error('Network Error') : { response: { status, data: message ? { message } : {} } });

test.describe('bulk activate: the hint before the click', () => {
  test('counts Draft employees whose profile is Incomplete or Needs attention, out of everyone selected', () => {
    const selected = [
      row('Draft', 'Blocked', 2), row('Draft', 'NeedsAttention', 1), row('Draft', 'Ready'),
      row('Active', 'Blocked', 3), row('Draft', 'NotReady', 0),
    ];
    expect(countCannotActivateYet(selected)).toEqual({ blocked: 3, total: 5 });
  });
  test('a Ready draft and anyone already past Draft are never counted', () => {
    expect(countCannotActivateYet([row('Draft', 'Ready'), row('Active', 'Blocked', 2), row('Suspended', 'NeedsAttention', 1)])).toEqual({ blocked: 0, total: 3 });
  });
  test('an unknown state with a blocker count still counts; an unknown state without one does not', () => {
    expect(countCannotActivateYet([row('Draft', '', 2), row('Draft', '', 0)])).toEqual({ blocked: 1, total: 2 });
  });
  test('an empty selection has nothing to say', () => {
    expect(countCannotActivateYet([])).toEqual({ blocked: 0, total: 0 });
  });
});

test.describe('bulk failures say what happened', () => {
  test('a server message always wins', () => {
    expect(bulkFailureMessage(failure(500, 'Boom.'), 'activate')).toBe('Boom.');
  });
  test('no answer at all', () => {
    expect(bulkFailureMessage(failure(), 'activate')).toContain('did not answer');
  });
  test('a server error asks the person to check the list before trying again', () => {
    expect(bulkFailureMessage(failure(503), 'activate')).toContain('Refresh the list');
  });
  test('a throttle and a refusal each get their own sentence, the refusal naming the action', () => {
    expect(bulkFailureMessage(failure(429), 'activate')).toContain('Too many requests');
    expect(bulkFailureMessage(failure(403), 'activate')).toBe('You do not have permission to activate employees.');
    expect(bulkFailureMessage(failure(403), 'deactivate')).toBe('You do not have permission to deactivate employees.');
    expect(bulkFailureMessage(failure(403), 'delete')).toBe('You do not have permission to delete employees.');
  });
  test('anything else keeps the old generic sentence', () => {
    expect(bulkFailureMessage(failure(400), 'activate')).toBe('The bulk action could not be completed.');
  });
});
