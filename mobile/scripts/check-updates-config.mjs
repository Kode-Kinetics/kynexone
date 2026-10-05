// npm run updates:check: validate the OTA channel config, then print the
// fingerprint runtime version per platform. An OTA update reaches only store
// builds whose runtime version matches; compare with the build on expo.dev.
import { execFileSync } from 'node:child_process';
import { checkUpdatesConfig } from './updatesConfig.mjs';

const root = process.cwd();
const { failures, config } = checkUpdatesConfig(root, {
  EXPO_PUBLIC_APP_ENV: 'production',
  EXPO_PUBLIC_API_BASE_URL: 'https://zayra-ai-workforce.onrender.com/api',
});

console.log(`Update URL: ${config.updates?.url ?? '<none>'}`);
for (const platform of ['ios', 'android']) {
  try {
    const out = execFileSync('npx', ['expo-updates', 'runtimeversion:resolve', '--platform', platform], {
      cwd: root,
      encoding: 'utf8',
      maxBuffer: 64 * 1024 * 1024,
      stdio: ['ignore', 'pipe', 'ignore'],
    });
    console.log(`Runtime version (${platform}): ${JSON.parse(out).runtimeVersion}`);
  } catch {
    failures.push(`Could not resolve the ${platform} runtime version.`);
  }
}

if (failures.length) {
  for (const failure of failures) console.error(`FAIL ${failure}`);
  process.exit(1);
}
console.log('OTA config OK.');
