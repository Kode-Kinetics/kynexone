import test from 'node:test';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { checkUpdatesConfig, loadEffectiveConfig } from '../scripts/updatesConfig.mjs';
import { CONFIG_ENV_KEY, DOTENV_FILES, dotEnvLeaks, envForProfile, readEasJson } from '../scripts/profileEnv.mjs';
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
  assert.equal(env.EXPO_NO_DOTENV, '1', 'local .env files are switched off for the publish');
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

// ---- local dotenv files ----------------------------------------------------------

function tempProject(files: Record<string, string>): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'kx-dotenv-'));
  for (const name of ['app.config.js', 'app.json', 'package.json', 'eas.json']) {
    fs.copyFileSync(path.join(root, name), path.join(dir, name));
  }
  for (const [name, body] of Object.entries(files)) fs.writeFileSync(path.join(dir, name), body);
  return dir;
}

test('every production and preview dotenv file is scanned, and any build-config key in one is refused', () => {
  for (const name of ['.env', '.env.local', '.env.production', '.env.production.local', '.env.preview', '.env.preview.local']) {
    assert.ok(DOTENV_FILES.includes(name), `${name} is scanned`);
  }
  const dir = tempProject({
    '.env': '# local dev\nEXPO_PUBLIC_API_BASE_URL=http://localhost:5117/api\nSOME_TOOL_TOKEN=x\n',
    '.env.production.local': 'export EXPO_UPDATES_CODE_SIGNING_CERT=./x.pem\n',
    '.env.preview': 'EXPO_PUBLIC_APP_ENV=staging\n',
  });
  assert.deepEqual(dotEnvLeaks(dir), [
    '.env: EXPO_PUBLIC_API_BASE_URL',
    '.env.production.local: EXPO_UPDATES_CODE_SIGNING_CERT',
    '.env.preview: EXPO_PUBLIC_APP_ENV',
  ]);
  assert.deepEqual(dotEnvLeaks(tempProject({ '.env': 'SOME_TOOL_TOKEN=x\n' })), [], 'unrelated keys are fine');
});

test('with EXPO_NO_DOTENV (as the publish sets it) a local .env cannot change the app config', () => {
  const dir = tempProject({ '.env': 'EXPO_PUBLIC_EAS_PROJECT_ID=11111111-2222-3333-4444-555555555555\n' });
  const leaked = loadEffectiveConfig(dir, { ...PROD_ENV });
  assert.equal(leaked.updates.url, 'https://u.expo.dev/11111111-2222-3333-4444-555555555555', 'control: .env is read by default');
  const publish = envForProfile({}, eas, 'production');
  const clean = loadEffectiveConfig(dir, publish);
  assert.equal(clean.updates.url, 'https://u.expo.dev/d8a0078a-97d3-493a-bfdb-866cc03962d0');
  assert.deepEqual(clean, loadEffectiveConfig(root, PROD_ENV));
});

// ---- eas.json env keys -----------------------------------------------------------

// Keys a build profile may set that are not EXPO_PUBLIC_*, each with the reason it
// still reaches the app config (and so the fingerprint) or why it cannot affect it.
const NON_PUBLIC_ENV_ALLOWLIST: Record<string, string> = {
  EXPO_UPDATES_CODE_SIGNING_CERT: 'read by app.config.js into updates.codeSigningCertificate, which is fingerprinted',
};

test('every build.*.env key reaches the fingerprint via EXPO_PUBLIC_* or is allow-listed with a reason', () => {
  for (const [profile, build] of Object.entries(eas.build as Record<string, { env?: Record<string, string> }>)) {
    for (const key of Object.keys(build.env ?? {})) {
      assert.ok(
        key.startsWith('EXPO_PUBLIC_') || NON_PUBLIC_ENV_ALLOWLIST[key],
        `build.${profile}.env.${key} is neither EXPO_PUBLIC_* nor allow-listed: an update published without it could not be told apart from the build`
      );
      assert.ok(CONFIG_ENV_KEY.test(key), `build.${profile}.env.${key} must be replaced exactly by update:<profile>`);
    }
  }
});
