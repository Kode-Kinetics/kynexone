import test from 'node:test';
import assert from 'node:assert/strict';
import {
  applyDirection,
  markRestartPrompted,
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
  assert.equal(await shouldPromptRestart('ar', true, storage, PROMPTED), true);
  // The restart did not flip the layout (dev client): the next launch must not ask again.
  assert.equal(await shouldPromptRestart('ar', true, storage, PROMPTED), false);
  assert.equal(await shouldPromptRestart('ar', true, storage, PROMPTED), false);
  // Switching to the other language is a new question.
  assert.equal(await shouldPromptRestart('en', true, storage, PROMPTED), true);
});

test('no mismatch: no prompt, and the marker is cleared for a later switch', async () => {
  const storage = memoryStorage({ [PROMPTED]: 'ar' });
  assert.equal(await shouldPromptRestart('ar', false, storage, PROMPTED), false);
  assert.equal(PROMPTED in storage.data, false);
  assert.equal(await shouldPromptRestart('ar', true, storage, PROMPTED), true);
});

test('the Settings switch prompts itself, so startup does not prompt a second time', async () => {
  const storage = memoryStorage();
  await markRestartPrompted('ar', storage, PROMPTED);
  assert.equal(await shouldPromptRestart('ar', true, storage, PROMPTED), false);
});

test('if the marker cannot be stored, it does not prompt (no unbounded loop)', async () => {
  const broken: LanguageStorage = {
    getItem: async () => null,
    setItem: async () => { throw new Error('full'); },
    removeItem: async () => {},
  };
  assert.equal(await shouldPromptRestart('ar', true, broken, PROMPTED), false);
});
