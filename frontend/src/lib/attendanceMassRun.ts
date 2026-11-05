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
  stopRequested: boolean;
}

export type MassRunAction =
  | { type: 'start'; people: Array<{ id: number; code: string; name: string }>; days: number; chunksTotal: number }
  | { type: 'chunkDone'; ids: number[]; result: BulkEntryResultShape }
  | { type: 'chunkFailed'; ids: number[]; message: string }
  | { type: 'requestStop' }
  | { type: 'end' }
  | { type: 'reset' };

export const initialMassRun: MassRunState = {
  phase: 'idle', people: [], days: 0, chunksTotal: 0, chunksDone: 0, created: 0, duplicates: 0, refused: 0, stopRequested: false,
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
    case 'chunkDone': {
      const ids = new Set(action.ids);
      const refusals = new Map<string, { dates: Set<string>; reasons: string[] }>();
      for (const e of action.result.errors) {
        const key = (e.employeeCode ?? '').trim().toLowerCase();
        const entry = refusals.get(key) ?? { dates: new Set<string>(), reasons: [] };
        entry.dates.add(e.date ?? '');
        if (e.message) entry.reasons.push(e.message);
        refusals.set(key, entry);
      }
      return {
        ...state,
        chunksDone: state.chunksDone + 1,
        created: state.created + action.result.created,
        duplicates: state.duplicates + action.result.duplicates,
        refused: state.refused + action.result.failed,
        people: state.people.map((p) => {
          if (!ids.has(p.id)) return p;
          const refused = refusals.get(p.code.trim().toLowerCase());
          if (!refused) return { ...p, status: 'done', reasons: [] };
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
