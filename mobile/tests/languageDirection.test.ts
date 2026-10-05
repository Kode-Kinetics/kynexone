import test from 'node:test';
import assert from 'node:assert/strict';
import {
  applyDirection,
  markRestartPrompted,
  offerRestartOnce,
  readStoredLanguage,
  shouldPromptRestart,
  type DirectionManager,
  type LanguageStorage,
} from '../src/config/languageDirection.ts';

const KEY = 'zayra_language';
const PROMPTED = 'zayra_language_restart_prompted';

function memoryStorage(initial: Record<string, string> = {}): LanguageStorage & { data: Record<string, string> } {
  const data = { ...initial };
  return {
    data,
    async getItem(k) { return k in data ? data[k] : null; },
    async setItem(k, v) { data[k] = v; },
    async removeItem(k) { delete data[k]; },
  };
}

/** I18nManager as it behaves: forceRTL is recorded, but isRTL changes only on the next start. */
function manager(isRTL: boolean): DirectionManager & { forced: boolean | null; allowed: boolean | null } {
  return {
    isRTL,
    forced: null,
    allowed: null,
    allowRTL(a) { this.allowed = a; },
    forceRTL(f) { this.forced = f; },
  };
}

test('readStoredLanguage reads the JSON form appStorage writes, and a bare value', async () => {
  assert.equal(await readStoredLanguage(memoryStorage({ [KEY]: '"ar"' }), KEY), 'ar');
  assert.equal(await readStoredLanguage(memoryStorage({ [KEY]: 'en' }), KEY), 'en');
});

test('readStoredLanguage returns null for nothing, junk, other languages and storage errors', async () => {
  assert.equal(await readStoredLanguage(memoryStorage(), KEY), null);
  assert.equal(await readStoredLanguage(memoryStorage({ [KEY]: '"fr"' }), KEY), null);
  assert.equal(await readStoredLanguage(memoryStorage({ [KEY]: '"ar' }), KEY), null); // malformed JSON
  const failing = { getItem: async () => { throw new Error('disk'); } };
  assert.equal(await readStoredLanguage(failing, KEY), null);
});

test('applyDirection forces the direction and reports a mismatch until restart', () => {
  const ltrApp = manager(false);
  assert.equal(applyDirection('ar', ltrApp), true);
  assert.equal(ltrApp.forced, true);
  assert.equal(ltrApp.allowed, true);

  const rtlApp = manager(true);
  assert.equal(applyDirection('ar', rtlApp), false);
  assert.equal(applyDirection('en', rtlApp), true);
  assert.equal(rtlApp.forced, false);

  assert.equal(applyDirection('en', manager(false)), false);
});

test('a direction mismatch at startup prompts once per language, never in a loop', async () => {
  const storage = memoryStorage();
  let shown = 0;
  const show = () => { shown++; };
  assert.equal(await offerRestartOnce('ar', true, storage, PROMPTED, show), true);
  // The restart did not flip the layout (dev client): later launches must not ask again.
  assert.equal(await offerRestartOnce('ar', true, storage, PROMPTED, show), false);
  assert.equal(await offerRestartOnce('ar', true, storage, PROMPTED, show), false);
  assert.equal(shown, 1);
  // Switching to the other language is a new question.
  assert.equal(await offerRestartOnce('en', true, storage, PROMPTED, show), true);
  assert.equal(shown, 2);
});

test('the marker is written only after the prompt was shown', async () => {
  const storage = memoryStorage();
  // Deciding alone writes nothing.
  assert.equal(await shouldPromptRestart('ar', true, storage, PROMPTED), true);
  assert.equal(PROMPTED in storage.data, false);
  // If showing fails (no window yet), nothing is recorded and the next launch asks again.
  await assert.rejects(offerRestartOnce('ar', true, storage, PROMPTED, () => { throw new Error('no window'); }));
  assert.equal(PROMPTED in storage.data, false);
  const order: string[] = [];
  const watching: LanguageStorage = {
    ...storage,
    setItem: async (k, v) => { order.push('mark'); await storage.setItem(k, v); },
  };
  await offerRestartOnce('ar', true, watching, PROMPTED, () => { order.push('show'); });
  assert.deepEqual(order, ['show', 'mark']);
  assert.equal(storage.data[PROMPTED], 'ar');
});

test('no mismatch: no prompt, and the marker is cleared for a later switch', async () => {
  const storage = memoryStorage({ [PROMPTED]: 'ar' });
  assert.equal(await offerRestartOnce('ar', false, storage, PROMPTED, () => assert.fail('must not prompt')), false);
  assert.equal(PROMPTED in storage.data, false);
  assert.equal(await shouldPromptRestart('ar', true, storage, PROMPTED), true);
});

test('the Settings switch prompts itself, so startup does not prompt a second time', async () => {
  const storage = memoryStorage();
  await markRestartPrompted('ar', storage, PROMPTED);
  assert.equal(await offerRestartOnce('ar', true, storage, PROMPTED, () => assert.fail('must not prompt')), false);
});

test('unreadable storage does not prompt', async () => {
  const broken: LanguageStorage = {
    getItem: async () => { throw new Error('disk'); },
    setItem: async () => {},
    removeItem: async () => {},
  };
  assert.equal(await shouldPromptRestart('ar', true, broken, PROMPTED), false);
});
