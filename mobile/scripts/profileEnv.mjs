// The environment an EAS build profile bakes into the app, reproduced locally.
//
// app.config.js copies EXPO_PUBLIC_* values into `extra`, and `extra` is part of
// the fingerprint runtime version. `eas update` does NOT read eas.json build
// profile env, so publishing from a shell with different values would produce
// a different runtime version and the update would reach nobody (or, worse, a
// localhost API URL would be fingerprinted into it). Every local step that
// computes or publishes a runtime version therefore uses EXACTLY the profile's
// env for these keys, and nothing inherited from the shell or .env.
import fs from 'node:fs';
import path from 'node:path';

/** Keys that change the app config, and so the runtime version. */
export const CONFIG_ENV_KEY = /^(EXPO_PUBLIC_|EXPO_UPDATES_CODE_SIGNING_CERT$)/;

export function readEasJson(root) {
  return JSON.parse(fs.readFileSync(path.join(root, 'eas.json'), 'utf8'));
}

export function profileEnv(eas, profile) {
  const build = eas.build?.[profile];
  if (!build) throw new Error(`eas.json has no build profile "${profile}".`);
  if (!build.channel) throw new Error(`eas.json build.${profile} has no channel.`);
  return { channel: build.channel, env: { ...(build.env ?? {}) } };
}

/**
 * Shell env with every config-affecting key replaced by exactly the profile's
 * values, and .env loading switched off (EAS builds never see local .env files).
 */
export function envForProfile(baseEnv, eas, profile) {
  const { env } = profileEnv(eas, profile);
  const out = {};
  for (const [key, value] of Object.entries(baseEnv)) {
    if (!CONFIG_ENV_KEY.test(key)) out[key] = value;
  }
  return { ...out, ...env, EXPO_NO_DOTENV: '1' };
}

/** Every dotenv file Expo or app.config.js could load for any build profile. */
export const DOTENV_FILES = [
  '.env',
  '.env.local',
  '.env.production',
  '.env.production.local',
  '.env.preview',
  '.env.preview.local',
  '.env.development',
  '.env.development.local',
];

/**
 * Config-affecting keys defined in local dotenv files, as "file: KEY". Publishing
 * refuses while any exist, even though EXPO_NO_DOTENV already ignores them:
 * a value that differs between a developer's machine and the build is the bug
 * this guards against, and it should be fixed, not silently skipped.
 */
export function dotEnvLeaks(root) {
  const leaks = [];
  for (const name of DOTENV_FILES) {
    const file = path.join(root, name);
    if (!fs.existsSync(file)) continue;
    for (const raw of fs.readFileSync(file, 'utf8').split(/\r?\n/)) {
      const line = raw.trim().replace(/^export\s+/, '');
      if (!line || line.startsWith('#') || !line.includes('=')) continue;
      const key = line.slice(0, line.indexOf('=')).trim();
      if (CONFIG_ENV_KEY.test(key)) leaks.push(`${name}: ${key}`);
    }
  }
  return leaks;
}
