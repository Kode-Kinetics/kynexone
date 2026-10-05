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

/** Shell env with every config-affecting key replaced by exactly the profile's values. */
export function envForProfile(baseEnv, eas, profile) {
  const { env } = profileEnv(eas, profile);
  const out = {};
  for (const [key, value] of Object.entries(baseEnv)) {
    if (!CONFIG_ENV_KEY.test(key)) out[key] = value;
  }
  return { ...out, ...env };
}

/** Keys a local .env would add on top of the profile (app.config.js falls back to .env). */
export function dotEnvLeaks(root, eas, profile) {
  const file = path.join(root, '.env');
  if (!fs.existsSync(file)) return [];
  const { env } = profileEnv(eas, profile);
  return fs.readFileSync(file, 'utf8')
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter((line) => line && !line.startsWith('#') && line.includes('='))
    .map((line) => line.slice(0, line.indexOf('=')).trim())
    .filter((key) => CONFIG_ENV_KEY.test(key) && !(key in env));
}
