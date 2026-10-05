import test from 'node:test';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import { checkUpdatesConfig, loadEffectiveConfig } from '../scripts/updatesConfig.mjs';

const root = fileURLToPath(new URL('..', import.meta.url));
const PROD = {
  EXPO_PUBLIC_APP_ENV: 'production',
  EXPO_PUBLIC_API_BASE_URL: 'https://zayra-ai-workforce.onrender.com/api',
  EXPO_PUBLIC_EAS_PROJECT_ID: undefined,
};

test('effective config carries the OTA channel for production and development builds', () => {
  for (const env of [PROD, { EXPO_PUBLIC_APP_ENV: 'staging', EXPO_PUBLIC_EAS_PROJECT_ID: undefined }]) {
    const { failures, config } = checkUpdatesConfig(root, env);
    assert.deepEqual(failures, []);
    assert.equal(config.updates.url, 'https://u.expo.dev/d8a0078a-97d3-493a-bfdb-866cc03962d0');
    assert.deepEqual(config.runtimeVersion, { policy: 'fingerprint' });
    assert.equal(config.updates.checkAutomatically, 'ON_LOAD');
    assert.equal(config.updates.fallbackToCacheTimeout, 0);
  }
});

test('every EAS build profile names its update channel', () => {
  const { eas } = checkUpdatesConfig(root, PROD);
  assert.equal(eas.build.production.channel, 'production');
  assert.equal(eas.build.preview.channel, 'preview');
  assert.equal(eas.build.simulator.channel, 'development');
});

test('an EAS project override moves the update URL with it, so the two can never disagree', () => {
  const config = loadEffectiveConfig(root, { ...PROD, EXPO_PUBLIC_EAS_PROJECT_ID: '11111111-2222-3333-4444-555555555555' });
  assert.equal(config.extra.eas.projectId, '11111111-2222-3333-4444-555555555555');
  assert.equal(config.updates.url, 'https://u.expo.dev/11111111-2222-3333-4444-555555555555');
});
