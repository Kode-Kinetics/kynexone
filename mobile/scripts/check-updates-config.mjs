// npm run updates:check: validate the OTA channel config, then print the
// fingerprint runtime version each build profile produces, computed with that
// profile's eas.json env (exactly what `npm run update:<profile>` publishes
// with). An OTA update reaches only store builds whose runtime version matches;
// compare with the build on expo.dev.
import { execFileSync } from 'node:child_process';
import { EXPECTED_CHANNELS, checkUpdatesConfig } from './updatesConfig.mjs';
import { dotEnvLeaks, envForProfile, readEasJson } from './profileEnv.mjs';

const root = process.cwd();
const eas = readEasJson(root);
const { failures, config } = checkUpdatesConfig(root, envForProfile({}, eas, 'production'));

console.log(`Update URL: ${config.updates?.url ?? '<none>'}`);
for (const profile of Object.keys(EXPECTED_CHANNELS)) {
  for (const leak of dotEnvLeaks(root, eas, profile)) failures.push(`.env sets ${leak}, which the ${profile} build does not have.`);
  const env = envForProfile(process.env, eas, profile);
  const versions = [];
  for (const platform of ['ios', 'android']) {
    try {
      const out = execFileSync('npx', ['expo-updates', 'runtimeversion:resolve', '--platform', platform], {
        cwd: root,
        env,
        encoding: 'utf8',
        maxBuffer: 64 * 1024 * 1024,
        stdio: ['ignore', 'pipe', 'ignore'],
      });
      versions.push(`${platform} ${JSON.parse(out).runtimeVersion}`);
    } catch {
      failures.push(`Could not resolve the ${platform} runtime version for ${profile}.`);
    }
  }
  console.log(`${profile} (channel ${eas.build[profile].channel}): ${versions.join(', ')}`);
}

if (failures.length) {
  for (const failure of failures) console.error(`FAIL ${failure}`);
  process.exit(1);
}
console.log('OTA config OK.');
