import test from 'node:test';
import assert from 'node:assert/strict';
import { MFA_SHEET_CAPTURE_KEY, createSecureSheet } from '../src/features/auth/secureSheet.ts';

// Mock of the two expo-screen-capture calls the sheet uses, logging into one
// timeline together with the Modal's visible state.
function harness(options: { preventFails?: boolean } = {}) {
  const log: string[] = [];
  const capture = {
    async preventScreenCaptureAsync(key?: string) {
      log.push(`prevent:${key}`);
      if (options.preventFails) throw new Error('unavailable');
    },
    async allowScreenCaptureAsync(key?: string) {
      log.push(`allow:${key}`);
    },
  };
  const sheet = createSecureSheet<string>(capture, (value) => log.push(value === null ? 'close' : `open:${value}`));
  return { log, sheet };
}

test('Android FLAG_SECURE: the window is secured BEFORE the Modal opens, and released AFTER it closes', async () => {
  const { log, sheet } = harness();
  await sheet.open('token-1');
  sheet.close();
  assert.deepEqual(log, [`prevent:${MFA_SHEET_CAPTURE_KEY}`, 'open:token-1', 'close', `allow:${MFA_SHEET_CAPTURE_KEY}`]);
  assert.equal(MFA_SHEET_CAPTURE_KEY, 'mfa-sheet');
});

test('unmount releases once; a second release is a no-op', async () => {
  const { log, sheet } = harness();
  await sheet.open('token-1');
  sheet.dispose();
  sheet.dispose();
  sheet.close();
  assert.deepEqual(log, ['prevent:mfa-sheet', 'open:token-1', 'allow:mfa-sheet', 'close']);
});

test('restart: close-then-open re-secures before the new sheet appears', async () => {
  const { log, sheet } = harness();
  await sheet.open('token-1');
  sheet.close();
  await sheet.open('token-2');
  assert.deepEqual(log.slice(-2), ['prevent:mfa-sheet', 'open:token-2']);
});

test('if securing fails the sheet still opens (never block enrolment) and nothing is released', async () => {
  const { log, sheet } = harness({ preventFails: true });
  await sheet.open('token-1');
  sheet.close();
  assert.deepEqual(log, ['prevent:mfa-sheet', 'open:token-1', 'close']);
});

test('the banner opens and closes the sheet only through the secure controller', async () => {
  const { readFileSync } = await import('node:fs');
  const banner = readFileSync(new URL('../src/features/auth/MfaSetupBanner.tsx', import.meta.url), 'utf8');
  assert.match(banner, /createSecureSheet\(ScreenCapture, setEnrollment\)/);
  assert.equal(banner.match(/setEnrollment\(/g), null, 'no direct setEnrollment(...) call can open the Modal unsecured');
  assert.match(banner, /await sheet\.open\(/);
  assert.match(banner, /\(\) => sheet\.dispose\(\)/, 'released on unmount');
});
