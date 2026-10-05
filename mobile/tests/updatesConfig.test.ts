import test from 'node:test';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { checkUpdatesConfig, loadEffectiveConfig } from '../scripts/updatesConfig.mjs';
import { CONFIG_ENV_KEY, envForProfile, readEasJson } from '../scripts/profileEnv.mjs';
import { updateCommand } from '../scripts/eas-update.mjs';

const root = fileURLToPath(new URL('..', import.meta.url));
const eas = readEasJson(root);
const PROD_ENV = eas.build.production.env;

function configKeys(env: Record<string, string | undefined>) {
  return Object.fromEntries(Object.entries(env).filter(([key]) => CONFIG_ENV_KEY.test(key)));
}

test('effective config carries the OTA channel for production and staging builds', () => {
  for (const profile of ['production', 'preview']) {
    const { failures, config } = checkUpdatesConfig(root, envForProfile({}, eas, profile));
    assert.deepEqual(failures, []);
    assert.equal(config.updates.url, 'https://u.expo.dev/d8a0078a-97d3-493a-bfdb-866cc03962d0');
    assert.deepEqual(config.runtimeVersion, { policy: 'fingerprint' });
    assert.equal(config.updates.checkAutomatically, 'ON_LOAD');
    assert.equal(config.updates.fallbackToCacheTimeout, 0);
    assert.equal(config.updates.codeSigningCertificate, undefined, 'unsigned unless the cert env is set');
  }
});

test('every EAS build profile names its update channel', () => {
  assert.equal(eas.build.production.channel, 'production');
  assert.equal(eas.build.preview.channel, 'preview');
  assert.equal(eas.build.simulator.channel, 'development');
});

test('update:production publishes with exactly build.production.env, whatever the shell holds', () => {
  const shell = {
    PATH: '/usr/bin',
    EXPO_PUBLIC_API_BASE_URL: 'http://localhost:5117/api',
    EXPO_PUBLIC_APP_ENV: 'development',
    EXPO_PUBLIC_EAS_PROJECT_ID: '11111111-2222-3333-4444-555555555555',
    EXPO_UPDATES_CODE_SIGNING_CERT: './stray.pem',
  };
  const { command, args, env } = updateCommand({ eas, profile: 'production', baseEnv: shell, message: 'Fix sign-in copy' });
  assert.equal(command, 'npx');
  assert.deepEqual(args.slice(0, 6), ['eas', 'update', '--channel', 'production', '--message', 'Fix sign-in copy']);
  assert.deepEqual(configKeys(env), PROD_ENV, 'config-affecting env is exactly the profile env');
  assert.equal(env.PATH, '/usr/bin', 'unrelated shell env passes through');
  // Same config in, same fingerprint out: the update's app config equals the build's.
  assert.deepEqual(loadEffectiveConfig(root, configKeys(env)), loadEffectiveConfig(root, PROD_ENV));
  assert.throws(() => updateCommand({ eas, profile: 'production', baseEnv: shell, message: ' ' }), /MSG/);
  assert.throws(() => updateCommand({ eas, profile: 'nope', baseEnv: shell, message: 'x' }), /no build profile/);
});

test('code signing is wired only when the certificate env is set, and then demands the private key', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'kx-cert-'));
  const cert = path.join(dir, 'certificate.pem');
  fs.writeFileSync(cert, 'test');
  const config = loadEffectiveConfig(root, { ...PROD_ENV, EXPO_UPDATES_CODE_SIGNING_CERT: cert });
  assert.equal(config.updates.codeSigningCertificate, cert);
  assert.deepEqual(config.updates.codeSigningMetadata, { keyid: 'main', alg: 'rsa-v1_5-sha256' });
  assert.throws(() => loadEffectiveConfig(root, { ...PROD_ENV, EXPO_UPDATES_CODE_SIGNING_CERT: path.join(dir, 'missing.pem') }), /missing file/);

  const signedEas = { build: { production: { ...eas.build.production, env: { ...PROD_ENV, EXPO_UPDATES_CODE_SIGNING_CERT: cert } } } };
  assert.throws(() => updateCommand({ eas: signedEas, profile: 'production', baseEnv: {}, message: 'x' }), /EXPO_UPDATES_CODE_SIGNING_KEY/);
  const signed = updateCommand({ eas: signedEas, profile: 'production', baseEnv: { EXPO_UPDATES_CODE_SIGNING_KEY: '/secure/private-key.pem' }, message: 'x' });
  assert.deepEqual(signed.args.slice(-2), ['--private-key-path', '/secure/private-key.pem']);
});

test('an EAS project override moves the update URL with it, so the two can never disagree', () => {
  const config = loadEffectiveConfig(root, { ...PROD_ENV, EXPO_PUBLIC_EAS_PROJECT_ID: '11111111-2222-3333-4444-555555555555' });
  assert.equal(config.extra.eas.projectId, '11111111-2222-3333-4444-555555555555');
  assert.equal(config.updates.url, 'https://u.expo.dev/11111111-2222-3333-4444-555555555555');
});

test('eas.json is outside the fingerprint; the API URL still reaches it through extra', () => {
  const fingerprintConfig = fs.readFileSync(path.join(root, 'fingerprint.config.js'), 'utf8');
  assert.match(fingerprintConfig, /ignorePaths:\s*\['eas\.json'\]/);
  assert.doesNotMatch(fingerprintConfig, /sourceSkips/, 'extra must stay in the fingerprint');
  assert.equal(loadEffectiveConfig(root, PROD_ENV).extra.apiBaseUrl, PROD_ENV.EXPO_PUBLIC_API_BASE_URL);
});
