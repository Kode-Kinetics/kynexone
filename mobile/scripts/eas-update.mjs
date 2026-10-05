// npm run update:production / update:preview
//   MSG="Fix: clearer sign-in errors" npm run update:production
//
// Publishes an OTA update with exactly the build profile's eas.json env, so the
// update's runtime version matches the store binaries built from that profile.
import { spawnSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';
import { dotEnvLeaks, envForProfile, profileEnv, readEasJson } from './profileEnv.mjs';

export function updateCommand({ eas, profile, baseEnv, message }) {
  if (!message || !message.trim()) throw new Error('Set MSG to a short description of the update.');
  const { channel, env } = profileEnv(eas, profile);
  const args = ['eas', 'update', '--channel', channel, '--message', message, '--non-interactive'];
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
    const leaks = dotEnvLeaks(root, eas, profile);
    if (leaks.length) {
      throw new Error(`.env sets ${leaks.join(', ')}, which the ${profile} build does not have; it would change the runtime version. Remove it or move it into eas.json.`);
    }
    const { command, args, env } = updateCommand({ eas, profile, baseEnv: process.env, message: process.env.MSG });
    const result = spawnSync(command, args, { stdio: 'inherit', env });
    process.exit(result.status ?? 1);
  } catch (error) {
    console.error(error instanceof Error ? error.message : String(error));
    process.exit(1);
  }
}
