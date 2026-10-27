import { expect, test } from '@playwright/test';
import { createAutosave } from '../src/lib/hire/autosave';

function harness(saveImpl?: (payload: string, base: string) => Promise<{ updatedAtUtc: string }>) {
  const timers: Array<{ id: number; due: number; fn: () => void }> = [];
  let clock = 0; let nextId = 1;
  const saves: Array<{ payload: string; base: string }> = [];
  const states: string[] = [];
  const auto = createAutosave<string>({
    delayMs: 1200,
    schedule: (fn, ms) => { const id = nextId++; timers.push({ id, due: clock + ms, fn }); return id; },
    cancel: id => { const i = timers.findIndex(t => t.id === id); if (i >= 0) timers.splice(i, 1); },
    save: async (payload, base) => { saves.push({ payload, base }); return saveImpl ? saveImpl(payload, base) : { updatedAtUtc: `v${saves.length}` }; },
    onState: s => states.push(s),
  });
  const advance = async (ms: number) => { clock += ms; for (const t of timers.filter(x => x.due <= clock)) { timers.splice(timers.indexOf(t), 1); t.fn(); } await new Promise(r => setTimeout(r, 0)); };
  return { auto, saves, states, advance, pending: () => timers.length };
}

test.describe('autosave_debounceVersion409', () => {
  test('debounces: rapid edits send one save with the latest payload and the adopted version', async () => {
    const h = harness();
    h.auto.adopt('v0');
    h.auto.markDirty('a'); await h.advance(500); h.auto.markDirty('ab'); await h.advance(500); h.auto.markDirty('abc');
    expect(h.saves).toHaveLength(0);
    await h.advance(1200);
    expect(h.saves).toEqual([{ payload: 'abc', base: 'v0' }]);
    expect(h.states.at(-1)).toBe('saved');
  });
  test('each save names the version the previous save returned', async () => {
    const h = harness();
    h.auto.adopt('v0');
    h.auto.markDirty('a'); await h.advance(1200);
    h.auto.markDirty('b'); await h.advance(1200);
    expect(h.saves.map(s => s.base)).toEqual(['v0', 'v1']);
  });
  test('nothing is saved before a draft exists (no adopted version)', async () => {
    const h = harness();
    h.auto.markDirty('a'); await h.advance(2000);
    expect(h.saves).toHaveLength(0);
    expect(h.auto.hasUnsaved()).toBe(true);
  });
  test('a blank or missing version is never adopted, so a save never goes out without a basis', async () => {
    const h = harness();
    h.auto.adopt('');
    h.auto.adopt('   ');
    expect(h.auto.version()).toBeNull();
    h.auto.markDirty('a'); await h.advance(2000);
    expect(await h.auto.flush()).toBe(false);
    expect(h.saves).toHaveLength(0);
  });
  test('a save response without a version keeps the previous basis', async () => {
    const h = harness(async () => ({ updatedAtUtc: '' }));
    h.auto.adopt('v0');
    h.auto.markDirty('a'); await h.advance(1200);
    expect(h.auto.version()).toBe('v0');
  });
  test('409 stops autosave for good and reports conflict', async () => {
    const h = harness(async () => { throw { response: { status: 409, data: { code: 'draft_changed_elsewhere' } } }; });
    h.auto.adopt('v0');
    h.auto.markDirty('a'); await h.advance(1200);
    expect(h.states.at(-1)).toBe('conflict');
    h.auto.markDirty('b'); await h.advance(5000);
    expect(h.saves).toHaveLength(1);
    expect(h.auto.isConflict()).toBe(true);
  });
  test('a failed save keeps the change dirty and retries on the next edit', async () => {
    let n = 0;
    const h = harness(async () => { if (n++ === 0) throw new Error('offline'); return { updatedAtUtc: 'v1' }; });
    h.auto.adopt('v0');
    h.auto.markDirty('a'); await h.advance(1200);
    expect(h.states.at(-1)).toBe('error');
    expect(h.auto.hasUnsaved()).toBe(true);
    expect(await h.auto.flush()).toBe(true);
    expect(h.auto.hasUnsaved()).toBe(false);
  });
  test('flush saves now and resolves true only when the server holds what is on screen', async () => {
    const h = harness();
    h.auto.adopt('v0');
    h.auto.markDirty('a');
    expect(await h.auto.flush()).toBe(true);
    expect(h.saves).toHaveLength(1);
    expect(h.pending()).toBe(0);
  });
});
