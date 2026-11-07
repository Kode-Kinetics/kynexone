import { expect, test } from '@playwright/test';
import {
  initialMassRun,
  isTransientBulkError,
  massRunReducer,
  planChunks,
  recordedIds,
  retryIds,
  runChunks,
  runSummary,
  trackMounted,
  type MassRunAction,
  type MassRunState,
} from '../src/lib/attendanceMassRun';
import * as fs from 'fs';
import * as path from 'path';

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

// Finding 1: React Strict Mode (dev) runs effect setup, cleanup, setup again on the same refs.
test('trackMounted sets the flag on setup, so Strict Mode setup-cleanup-setup leaves the panel mounted', () => {
  const ref = { current: false };
  const cleanup1 = trackMounted(ref);
  expect(ref.current).toBe(true);
  cleanup1();
  expect(ref.current).toBe(false);
  const cleanup2 = trackMounted(ref);
  expect(ref.current).toBe(true);
  cleanup2();
  expect(ref.current).toBe(false);
});

test('the panel wires its mounted flag through trackMounted (no cleanup-only effect)', () => {
  const src = fs.readFileSync(path.resolve(__dirname, '../src/components/attendance/AttendanceRecordPanel.tsx'), 'utf8').replace(/\s+/g, ' ');
  expect(src).toMatch(/useEffect\(\(\) => trackMounted\(mountedRef\), \[\]\)/);
  expect(src).not.toMatch(/useEffect\(\(\) => \(\) => \{ mountedRef\.current = false; \}, \[\]\)/);
});

test('after a Strict Mode double effect the run still sends every chunk and dispatches end', async () => {
  const mounted = { current: true };
  trackMounted(mounted)();   // setup + cleanup, as the dev double-invoke does
  trackMounted(mounted);     // setup again; refs are preserved
  const actions: MassRunAction[] = [];
  const sent: number[][] = [];
  const out = await runChunks({
    chunks: [[1], [2]],
    sendChunk: async (ids) => { sent.push(ids); return result({ created: 1 }); },
    dispatch: (a) => actions.push(a),
    isStopped: () => false,
    isMounted: () => mounted.current,
    describeError: String,
    sleep: async () => {},
  });
  expect(sent).toEqual([[1], [2]]);
  expect(out).toEqual({ recorded: 2, finished: true });
  expect(actions.map((a) => a.type)).toEqual(['chunkDone', 'chunkDone', 'end']);
});

test('a really unmounted panel sends nothing more and dispatches nothing', async () => {
  const actions: MassRunAction[] = [];
  const out = await runChunks({
    chunks: [[1]], sendChunk: async () => result(), dispatch: (a) => actions.push(a),
    isStopped: () => false, isMounted: () => false, describeError: String, sleep: async () => {},
  });
  expect(actions).toEqual([]);
  expect(out.finished).toBe(false);
});

test('runChunks retries a transient failure, then reports a permanent one as chunkFailed', async () => {
  const actions: MassRunAction[] = [];
  let calls = 0;
  await runChunks({
    chunks: [[1], [2]],
    sendChunk: async (ids) => { calls++; if (ids[0] === 1 && calls === 1) throw new Error('Network Error'); if (ids[0] === 2) throw { response: { status: 400 } }; return result({ created: 1 }); },
    dispatch: (a) => actions.push(a), isStopped: () => false, isMounted: () => true, describeError: () => 'bad', sleep: async () => {},
  });
  expect(actions.map((a) => a.type)).toEqual(['chunkDone', 'chunkFailed', 'end']);
});

// Finding 2: Retry must keep the people who were already recorded.
const firstPassWithOneFailure = (): MassRunState => {
  let s: MassRunState = massRunReducer(initialMassRun, { type: 'start', people: people(3), days: 1, chunksTotal: 3 });
  s = massRunReducer(s, { type: 'chunkDone', ids: [1], result: result({ created: 1 }) });
  s = massRunReducer(s, { type: 'chunkDone', ids: [2], result: result({ failed: 1, errors: [{ employeeCode: 'E-2', date: '2026-11-01', message: 'Payroll is locked for this date.' }] }) });
  s = massRunReducer(s, { type: 'chunkDone', ids: [3], result: result({ created: 1 }) });
  return massRunReducer(s, { type: 'end' });
};

test('retry keeps the earlier successes in the results and resends only the unsuccessful ones', () => {
  let s = firstPassWithOneFailure();
  expect(retryIds(s)).toEqual([2]);
  s = massRunReducer(s, { type: 'retry', ids: retryIds(s), chunksTotal: 1 });
  expect(s.phase).toBe('running');
  expect(s.people.map((p) => [p.id, p.status])).toEqual([[1, 'done'], [2, 'waiting'], [3, 'done']]);
  expect(s.people[1].reasons).toEqual([]);
  // progress counts the people already answered, so a retry of 1 in 3 starts at 67% and does not drop to 0
  expect(runSummary(s)).toMatchObject({ total: 3, done: 2, waiting: 1, percent: 67 });
  s = massRunReducer(s, { type: 'chunkDone', ids: [2], result: result({ created: 1 }) });
  s = massRunReducer(s, { type: 'end' });
  expect(s.phase).toBe('finished');
  expect(s.people.map((p) => p.status)).toEqual(['done', 'done', 'done']);
  expect(recordedIds(s)).toEqual([1, 2, 3]);
  expect(retryIds(s)).toEqual([]);
  expect(runSummary(s)).toMatchObject({ total: 3, done: 3, percent: 100, created: 3, refused: 0 });
});

test('a retry that is refused again does not double-count the refusal', () => {
  let s = firstPassWithOneFailure();
  expect(runSummary(s).refused).toBe(1);
  s = massRunReducer(s, { type: 'retry', ids: [2], chunksTotal: 1 });
  expect(runSummary(s).refused).toBe(0);   // the old refusal is being re-tried, not still outstanding
  s = massRunReducer(s, { type: 'chunkDone', ids: [2], result: result({ failed: 1, errors: [{ employeeCode: 'E-2', date: '2026-11-01', message: 'Payroll is locked for this date.' }] }) });
  s = massRunReducer(s, { type: 'end' });
  expect(runSummary(s)).toMatchObject({ done: 2, notDone: 1, percent: 100, refused: 1 });
  expect(recordedIds(s)).toEqual([1, 3]);
  expect(retryIds(s)).toEqual([2]);
});

test('stopping a retry leaves the unsent retried people waiting and keeps the earlier successes', () => {
  let s = firstPassWithOneFailure();
  s = massRunReducer(s, { type: 'retry', ids: [2], chunksTotal: 1 });
  s = massRunReducer(s, { type: 'requestStop' });
  s = massRunReducer(s, { type: 'end' });
  expect(s.phase).toBe('stopped');
  expect(recordedIds(s)).toEqual([1, 3]);
  expect(retryIds(s)).toEqual([2]);
});

test('the panel retries through the retry action, not by restarting the run', () => {
  const src = fs.readFileSync(path.resolve(__dirname, '../src/components/attendance/AttendanceRecordPanel.tsx'), 'utf8').replace(/\s+/g, ' ');
  expect(src).toContain("type: 'retry'");
});
