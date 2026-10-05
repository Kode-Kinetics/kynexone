// Loads the EFFECTIVE Expo config (app.json passed through app.config.js, which
// wins) plus eas.json, and lists everything that would stop an OTA update from
// reaching store binaries. Shared by `npm run updates:check` and the unit test.
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { CONFIG_ENV_KEY } from './profileEnv.mjs';

export const EXPECTED_CHANNELS = { production: 'production', preview: 'preview', simulator: 'development' };

/**
 * env: the config-affecting values (EXPO_PUBLIC_*, EXPO_UPDATES_CODE_SIGNING_CERT)
 * to evaluate app.config.js with. Any such key in the shell that env does not
 * name is hidden while loading, so the result matches a build with exactly env.
 */
export function loadEffectiveConfig(root, env = {}) {
  const require = createRequire(path.join(root, 'package.json'));
  const configPath = require.resolve('./app.config.js');
  const saved = {};
  const keys = new Set([...Object.keys(env), ...Object.keys(process.env).filter((key) => CONFIG_ENV_KEY.test(key))]);
  for (const key of keys) {
    saved[key] = process.env[key];
    const value = env[key];
    if (value === undefined) delete process.env[key];
    else process.env[key] = value;
  }
  try {
    delete require.cache[configPath];
    const factory = require(configPath);
    const app = JSON.parse(fs.readFileSync(path.join(root, 'app.json'), 'utf8')).expo;
    return factory({ config: app });
  } finally {
    for (const [key, value] of Object.entries(saved)) {
      if (value === undefined) delete process.env[key];
      else process.env[key] = value;
    }
    delete require.cache[configPath];
  }
}

export function checkUpdatesConfig(root, env) {
  const failures = [];
  const config = loadEffectiveConfig(root, env);
  const pkg = JSON.parse(fs.readFileSync(path.join(root, 'package.json'), 'utf8'));
  const eas = JSON.parse(fs.readFileSync(path.join(root, 'eas.json'), 'utf8'));
  const projectId = config.extra?.eas?.projectId;

  if (!pkg.dependencies?.['expo-updates']) failures.push('expo-updates is not a dependency.');
  if (!projectId) failures.push('extra.eas.projectId is missing.');
  if (config.updates?.url !== `https://u.expo.dev/${projectId}`) {
    failures.push(`updates.url must be https://u.expo.dev/${projectId}; found ${config.updates?.url}.`);
  }
  if (config.runtimeVersion?.policy !== 'fingerprint') {
    failures.push(`runtimeVersion must use the fingerprint policy; found ${JSON.stringify(config.runtimeVersion)}.`);
  }
  if (config.updates?.checkAutomatically !== 'ON_LOAD') failures.push('updates.checkAutomatically must be ON_LOAD.');
  if (config.updates?.fallbackToCacheTimeout !== 0) failures.push('updates.fallbackToCacheTimeout must be 0 (never block startup).');
  if (config.updates?.enabled === false) failures.push('updates.enabled is false.');
  for (const [profile, channel] of Object.entries(EXPECTED_CHANNELS)) {
    const found = eas.build?.[profile]?.channel;
    if (found !== channel) failures.push(`eas.json build.${profile}.channel must be "${channel}"; found ${found}.`);
  }
  return { failures, config, eas };
}
