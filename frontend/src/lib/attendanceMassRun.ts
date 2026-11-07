import type { BulkEntryResultShape } from './attendanceBulkEntry';

/**
 * A mass entry sent in steps, so HR sees it move: the chosen people are split into chunks, each chunk is one bulk-entry
 * request (idempotent: a repeat counts as "already recorded"), and each person is marked from the server's per-day
 * refusals once their chunk answers. The server stays the only judge of what was recorded; this only tallies its answers.
 */

export type PersonStatus = 'waiting' | 'done' | 'partial' | 'notDone';

export interface RunPerson {
  id: number;
  code: string;
  name: string;
  status: PersonStatus;
  /** The server's reasons for the refused days (distinct), or the error that stopped this person's chunk. */
  reasons: string[];
}

export interface MassRunState {
  phase: 'idle' | 'running' | 'finished' | 'stopped';
  people: RunPerson[];
  /** Days in the requested range: a person refused on every one of them is "not done". */
  days: number;
  chunksTotal: number;
  chunksDone: number;
  created: number;
  duplicates: number;
  refused: number;
  /** Refused punches per person (an estimate from the chunk's totals), so a retry can take them back out of `refused`. */
  refusedBy: Record<number, number>;
  stopRequested: boolean;
}

export type MassRunAction =
  | { type: 'start'; people: Array<{ id: number; code: string; name: string }>; days: number; chunksTotal: number }
  /** Resend only `ids` (the unsuccessful people): everyone else keeps their status and results. */
  | { type: 'retry'; ids: number[]; chunksTotal: number }
  | { type: 'chunkDone'; ids: number[]; result: BulkEntryResultShape }
  | { type: 'chunkFailed'; ids: number[]; message: string }
  | { type: 'requestStop' }
  | { type: 'end' }
  | { type: 'reset' };

export const initialMassRun: MassRunState = {
  phase: 'idle', people: [], days: 0, chunksTotal: 0, chunksDone: 0, created: 0, duplicates: 0, refused: 0, refusedBy: {}, stopRequested: false,
};

/**
 * Splits ids into about `targetSteps` chunks of at most `maxChunk`, in order. A run of `targetSteps` people or fewer goes
 * one person per request, so the bar moves per person; a big run stays at about `targetSteps` requests.
 */
export function planChunks(ids: readonly number[], { targetSteps = 20, maxChunk = 25 }: { targetSteps?: number; maxChunk?: number } = {}): number[][] {
  if (ids.length === 0) return [];
  const size = Math.min(maxChunk, Math.max(1, Math.ceil(ids.length / targetSteps)));
  const chunks: number[][] = [];
  for (let i = 0; i < ids.length; i += size) chunks.push(ids.slice(i, i + size));
  return chunks;
}

const unique = (values: string[]) => [...new Set(values)];

export function massRunReducer(state: MassRunState, action: MassRunAction): MassRunState {
  switch (action.type) {
    case 'start':
      return {
        ...initialMassRun,
        phase: 'running',
        days: Math.max(1, action.days),
        chunksTotal: action.chunksTotal,
        people: action.people.map((p) => ({ ...p, status: 'waiting', reasons: [] })),
      };
    case 'retry': {
      const again = new Set(action.ids);
      const taken = action.ids.reduce((sum, id) => sum + (state.refusedBy[id] ?? 0), 0);
      const refusedBy = { ...state.refusedBy };
      for (const id of again) delete refusedBy[id];
      return {
        ...state,
        phase: 'running',
        chunksTotal: action.chunksTotal,
        chunksDone: 0,
        refused: Math.max(0, state.refused - taken),
        refusedBy,
        stopRequested: false,
        people: state.people.map((p) => (again.has(p.id) ? { ...p, status: 'waiting', reasons: [] } : p)),
      };
    }
    case 'chunkDone': {
      const ids = new Set(action.ids);
      const refusals = new Map<string, { dates: Set<string>; reasons: string[]; rows: number }>();
      for (const e of action.result.errors) {
        const key = (e.employeeCode ?? '').trim().toLowerCase();
        const entry = refusals.get(key) ?? { dates: new Set<string>(), reasons: [], rows: 0 };
        entry.rows += 1;
        entry.dates.add(e.date ?? '');
        if (e.message) entry.reasons.push(e.message);
        refusals.set(key, entry);
      }
      // `failed` counts punches and each error row is one person-day, so a person's share is their rows of the total.
      const perRow = action.result.errors.length > 0 ? action.result.failed / action.result.errors.length : 0;
      const refusedBy = { ...state.refusedBy };
      return {
        ...state,
        chunksDone: state.chunksDone + 1,
        created: state.created + action.result.created,
        duplicates: state.duplicates + action.result.duplicates,
        refused: state.refused + action.result.failed,
        refusedBy,
        people: state.people.map((p) => {
          if (!ids.has(p.id)) return p;
          const refused = refusals.get(p.code.trim().toLowerCase());
          if (!refused) return { ...p, status: 'done', reasons: [] };
          refusedBy[p.id] = Math.round(refused.rows * perRow);
          return { ...p, status: refused.dates.size >= state.days ? 'notDone' : 'partial', reasons: unique(refused.reasons) };
        }),
      };
    }
    case 'chunkFailed': {
      const ids = new Set(action.ids);
      return {
        ...state,
        chunksDone: state.chunksDone + 1,
        people: state.people.map((p) => (ids.has(p.id) ? { ...p, status: 'notDone', reasons: [action.message] } : p)),
      };
    }
    case 'requestStop':
      return state.phase === 'running' ? { ...state, stopRequested: true } : state;
    case 'end':
      return { ...state, phase: state.people.some((p) => p.status === 'waiting') ? 'stopped' : 'finished' };
    case 'reset':
      return initialMassRun;
    default:
      return state;
  }
}

export interface RunSummary {
  total: number;
  done: number;
  partial: number;
  notDone: number;
  waiting: number;
  /** Share of people the server has answered for, 0–100. */
  percent: number;
  created: number;
  duplicates: number;
  refused: number;
}

export function runSummary(state: MassRunState): RunSummary {
  const count = (s: PersonStatus) => state.people.filter((p) => p.status === s).length;
  const total = state.people.length;
  const waiting = count('waiting');
  return {
    total, done: count('done'), partial: count('partial'), notDone: count('notDone'), waiting,
    percent: total === 0 ? 0 : Math.round(((total - waiting) / total) * 100),
    created: state.created, duplicates: state.duplicates, refused: state.refused,
  };
}

/** The people worth sending again: refused in whole or part, or never sent. Safe to resend: recorded days come back as duplicates. */
export const retryIds = (state: MassRunState): number[] =>
  state.people.filter((p) => p.status !== 'done').map((p) => p.id);

/** The people whose attendance is now on file for at least one day: what "Process now" should cover. */
export const recordedIds = (state: MassRunState): number[] =>
  state.people.filter((p) => p.status === 'done' || p.status === 'partial').map((p) => p.id);

/** Worth an automatic retry: no answer at all, a server fault, another bulk write holding the company (409), or a rate limit (429). */
export function isTransientBulkError(err: unknown): boolean {
  const status = (err as { response?: { status?: number } } | null)?.response?.status;
  if (status === undefined) return true;
  return status >= 500 || status === 409 || status === 429;
}

/**
 * Effect body for a "still mounted" flag. It must set the flag on setup, not only clear it on cleanup: React Strict Mode (dev)
 * runs setup, cleanup, setup again on the same ref, and a cleanup-only effect would leave the flag false for good.
 */
export function trackMounted(ref: { current: boolean }): () => void {
  ref.current = true;
  return () => { ref.current = false; };
}

export interface RunChunksDeps {
  chunks: number[][];
  sendChunk: (ids: number[]) => Promise<BulkEntryResultShape>;
  dispatch: (action: MassRunAction) => void;
  isStopped: () => boolean;
  isMounted: () => boolean;
  describeError: (err: unknown) => string;
  sleep: (ms: number) => Promise<void>;
}

/**
 * Sends the chunks one after another, dispatching what the server answered. Each chunk is idempotent (a repeat comes back
 * "already recorded"), so a lost answer or a busy company is retried. Ends with `end` unless the panel went away.
 */
export async function runChunks(deps: RunChunksDeps): Promise<{ recorded: number; finished: boolean }> {
  const { chunks, sendChunk, dispatch, isStopped, isMounted, describeError, sleep } = deps;
  let recorded = 0;
  for (const chunk of chunks) {
    if (isStopped() || !isMounted()) break;
    for (let attempt = 0; ; attempt++) {
      try {
        const result = await sendChunk(chunk);
        recorded += result.created;
        if (isMounted()) dispatch({ type: 'chunkDone', ids: chunk, result });
        break;
      } catch (err) {
        if (isTransientBulkError(err) && attempt < 2 && !isStopped()) { await sleep(800 * (attempt + 1)); continue; }
        if (isMounted()) dispatch({ type: 'chunkFailed', ids: chunk, message: describeError(err) });
        break;
      }
    }
  }
  if (!isMounted()) return { recorded, finished: false };
  dispatch({ type: 'end' });
  return { recorded, finished: true };
}
