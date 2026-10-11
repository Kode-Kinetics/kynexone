// npm run update:production / update:preview
//   MSG="Fix: clearer sign-in errors" npm run update:production
//
// Publishes an OTA update with exactly the build profile's eas.json env, so the
// update's runtime version matches the store binaries built from that profile.
import { spawnSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';
import { CONFIG_ENV_KEY, dotEnvLeaks, envForProfile, profileEnv, readEasJson } from './profileEnv.mjs';

/** Variable names from `eas env:list --format short` output ("NAME=value" lines; "No variables found…" = none). */
export function parseEnvListNames(text) {
  return String(text ?? '')
    .split(/\r?\n/)
    .map((line) => /^([A-Za-z_][A-Za-z0-9_]*)=/.exec(line.trim())?.[1])
    .filter(Boolean);
}

/**
 * `--environment` makes eas-cli fetch the dashboard variables of that EAS environment and overlay them on
 * the process env before bundling — AFTER the profile env this script supplies. A dashboard variable that
 * reaches the bundle (any EXPO_PUBLIC_*, or a key the profile sets) would therefore silently replace the
 * build's config in the update. Returns the offending names; the publish refuses if there are any.
 */
export function dashboardOverrides(names, profileEnvKeys) {
  const profileKeys = new Set(profileEnvKeys);
  return [...new Set(names)].filter((name) => CONFIG_ENV_KEY.test(name) || name.startsWith('EXPO_PUBLIC_') || profileKeys.has(name));
}

/** EAS environments are development / preview / production; build profiles map onto them by name. */
export function easEnvironment(profile) {
  return profile === 'production' || profile === 'preview' ? profile : 'development';
}

export function updateCommand({ eas, profile, baseEnv, message }) {
  if (!message || !message.trim()) throw new Error('Set MSG to a short description of the update.');
  const { channel, env } = profileEnv(eas, profile);
  // `npx --yes eas-cli`, not `npx eas`: eas-cli is not a project dependency (adding it would move the
  // runtime fingerprint), and `npx eas` fails with "could not determine executable" without a global install.
  // eas-cli 16+ refuses --non-interactive without --environment. The EAS dashboard holds no variables for
  // these environments (see scripts/profileEnv.mjs): the profile env passed below stays the only config.
  const args = ['--yes', 'eas-cli', 'update', '--channel', channel, '--environment', easEnvironment(profile),
    '--message', message, '--non-interactive'];
  if (env.EXPO_UPDATES_CODE_SIGNING_CERT) {
    const key = baseEnv.EXPO_UPDATES_CODE_SIGNING_KEY;
    if (!key) throw new Error('This profile signs updates: set EXPO_UPDATES_CODE_SIGNING_KEY to the private key path (kept outside the repo).');
    args.push('--private-key-path', key);
  }
  return { command: 'npx', args, env: envForProfile(baseEnv, eas, profile) };
}

const isMain = process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href;
if (isMain) {
  const profile = process.argv[2];
  const root = process.cwd();
  try {
    const eas = readEasJson(root);
    const leaks = dotEnvLeaks(root);
    if (leaks.length) {
      throw new Error(`Local dotenv files set build config (${leaks.join('; ')}). Move them aside before publishing; the ${profile} build only has eas.json build.${profile}.env.`);
    }
    const { command, args, env } = updateCommand({ eas, profile, baseEnv: process.env, message: process.env.MSG });
    const environment = easEnvironment(profile);
    const names = [];
    for (const scope of ['project', 'account']) {
      const listed = spawnSync('npx', ['--yes', 'eas-cli', 'env:list', '--environment', environment, '--scope', scope, '--format', 'short'],
        { encoding: 'utf8', env });
      if (listed.status !== 0) throw new Error(`Could not list EAS ${scope} variables for ${environment}; refusing to publish blind.\n${listed.stderr || listed.stdout}`);
      names.push(...parseEnvListNames(listed.stdout));
    }
    const overrides = dashboardOverrides(names, Object.keys(profileEnv(eas, profile).env));
    if (overrides.length) {
      throw new Error(`EAS dashboard variables for '${environment}' would override the ${profile} build config in this update: ${overrides.join(', ')}. Remove them from the dashboard (eas.json build.${profile}.env is the only source of build config).`);
    }
    const result = spawnSync(command, args, { stdio: 'inherit', env });
    process.exit(result.status ?? 1);
  } catch (error) {
    console.error(error instanceof Error ? error.message : String(error));
    process.exit(1);
  }
}
