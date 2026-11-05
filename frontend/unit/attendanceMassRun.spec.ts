import { expect, test } from '@playwright/test';
import {
  initialMassRun,
  isTransientBulkError,
  massRunReducer,
  planChunks,
  retryIds,
  runSummary,
  type MassRunState,
} from '../src/lib/attendanceMassRun';

const people = (n: number) => Array.from({ length: n }, (_, i) => ({ id: i + 1, code: `E-${i + 1}`, name: `Person ${i + 1}` }));
const result = (over: Partial<{ created: number; duplicates: number; failed: number; errors: Array<{ employeeCode: string; date: string; message: string }> }> = {}) =>
  ({ requested: 0, created: 0, duplicates: 0, failed: 0, errors: [], ...over });

test('planChunks keeps every id once, in order, and gives about 20 steps for a big run', () => {
  const ids = Array.from({ length: 500 }, (_, i) => i + 1);
  const chunks = planChunks(ids);
  expect(chunks.flat()).toEqual(ids);
  expect(chunks.length).toBe(20);
  expect(Math.max(...chunks.map((c) => c.length))).toBeLessThanOrEqual(25);
});

test('planChunks sends a small run one person at a time, so progress moves per person', () => {
  expect(planChunks([3, 1, 2])).toEqual([[3], [1], [2]]);
  expect(planChunks([])).toEqual([]);
});

test('planChunks never exceeds the chunk cap', () => {
  const chunks = planChunks(Array.from({ length: 1000 }, (_, i) => i), { targetSteps: 4, maxChunk: 25 });
  expect(chunks.every((c) => c.length <= 25)).toBe(true);
  expect(chunks.length).toBe(40);
});

test('start lists everyone as waiting at 0%', () => {
  const s = massRunReducer(initialMassRun, { type: 'start', people: people(4), days: 2, chunksTotal: 4 });
  expect(s.phase).toBe('running');
  expect(runSummary(s)).toEqual({ total: 4, done: 0, partial: 0, notDone: 0, waiting: 4, percent: 0, created: 0, duplicates: 0, refused: 0 });
});

test('a chunk marks people done, partly done or not done from the per-day refusals', () => {
  let s: MassRunState = massRunReducer(initialMassRun, { type: 'start', people: people(4), days: 2, chunksTotal: 1 });
  s = massRunReducer(s, {
    type: 'chunkDone', ids: [1, 2, 3, 4],
    result: result({
      created: 6, duplicates: 2, failed: 3,
      errors: [
        { employeeCode: 'E-2', date: '2026-11-01', message: 'Payroll is locked for this date.' },
        { employeeCode: 'e-3', date: '2026-11-01', message: 'You cannot record your own attendance.' },
        { employeeCode: 'E-3', date: '2026-11-02', message: 'You cannot record your own attendance.' },
      ],
    }),
  });
  const byId = new Map(s.people.map((p) => [p.id, p]));
  expect(byId.get(1)!.status).toBe('done');
  expect(byId.get(2)!.status).toBe('partial');
  expect(byId.get(2)!.reasons).toEqual(['Payroll is locked for this date.']);
  expect(byId.get(3)!.status).toBe('notDone');
  expect(byId.get(3)!.reasons).toEqual(['You cannot record your own attendance.']);
  expect(byId.get(4)!.status).toBe('done');
  expect(runSummary(s)).toMatchObject({ done: 2, partial: 1, notDone: 1, waiting: 0, percent: 100, created: 6, duplicates: 2, refused: 3 });
});

test('the same day refused for In and Out counts as one day, not two', () => {
  let s = massRunReducer(initialMassRun, { type: 'start', people: people(1), days: 2, chunksTotal: 1 });
  s = massRunReducer(s, {
    type: 'chunkDone', ids: [1],
    result: result({ failed: 2, errors: [
      { employeeCode: 'E-1', date: '2026-11-01', message: 'Locked.' },
      { employeeCode: 'E-1', date: '2026-11-01', message: 'Locked.' },
    ] }),
  });
  expect(s.people[0].status).toBe('partial');
});

test('progress is the share of people answered, rounded', () => {
  let s = massRunReducer(initialMassRun, { type: 'start', people: people(3), days: 1, chunksTotal: 3 });
  s = massRunReducer(s, { type: 'chunkDone', ids: [1], result: result({ created: 1 }) });
  expect(runSummary(s).percent).toBe(33);
  expect(s.chunksDone).toBe(1);
});

test('a failed chunk marks its people not done with the error, and the run carries on', () => {
  let s = massRunReducer(initialMassRun, { type: 'start', people: people(2), days: 1, chunksTotal: 2 });
  s = massRunReducer(s, { type: 'chunkFailed', ids: [1], message: 'Network error' });
  expect(s.phase).toBe('running');
  expect(s.people[0]).toMatchObject({ status: 'notDone', reasons: ['Network error'] });
  expect(s.people[1].status).toBe('waiting');
});

test('stopping leaves the unsent people waiting and says the run was stopped', () => {
  let s = massRunReducer(initialMassRun, { type: 'start', people: people(3), days: 1, chunksTotal: 3 });
  s = massRunReducer(s, { type: 'chunkDone', ids: [1], result: result({ created: 1 }) });
  s = massRunReducer(s, { type: 'requestStop' });
  expect(s.stopRequested).toBe(true);
  s = massRunReducer(s, { type: 'end' });
  expect(s.phase).toBe('stopped');
  expect(runSummary(s).waiting).toBe(2);
});

test('a run that answered everyone ends finished', () => {
  let s = massRunReducer(initialMassRun, { type: 'start', people: people(1), days: 1, chunksTotal: 1 });
  s = massRunReducer(s, { type: 'chunkDone', ids: [1], result: result({ duplicates: 1 }) });
  s = massRunReducer(s, { type: 'end' });
  expect(s.phase).toBe('finished');
  expect(s.people[0].status).toBe('done');
});

test('retryIds offers the people not done, partly done or never sent', () => {
  let s = massRunReducer(initialMassRun, { type: 'start', people: people(4), days: 1, chunksTotal: 4 });
  s = massRunReducer(s, { type: 'chunkDone', ids: [1], result: result({ created: 1 }) });
  s = massRunReducer(s, { type: 'chunkFailed', ids: [2], message: 'x' });
  s = massRunReducer(s, { type: 'requestStop' });
  s = massRunReducer(s, { type: 'end' });
  expect(retryIds(s)).toEqual([2, 3, 4]);
});

test('reset returns to idle', () => {
  const s = massRunReducer(massRunReducer(initialMassRun, { type: 'start', people: people(1), days: 1, chunksTotal: 1 }), { type: 'reset' });
  expect(s).toEqual(initialMassRun);
});

test('only network failures, 5xx, 409 (busy) and 429 are retried automatically', () => {
  expect(isTransientBulkError(new Error('Network Error'))).toBe(true);
  expect(isTransientBulkError({ response: { status: 503 } })).toBe(true);
  expect(isTransientBulkError({ response: { status: 409 } })).toBe(true);
  expect(isTransientBulkError({ response: { status: 429 } })).toBe(true);
  expect(isTransientBulkError({ response: { status: 400 } })).toBe(false);
  expect(isTransientBulkError({ response: { status: 403 } })).toBe(false);
});
