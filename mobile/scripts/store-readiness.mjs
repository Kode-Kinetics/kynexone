import fs from 'node:fs';
import path from 'node:path';

const root = process.cwd();
const pkg = JSON.parse(fs.readFileSync(path.join(root, 'package.json'), 'utf8'));
const app = JSON.parse(fs.readFileSync(path.join(root, 'app.json'), 'utf8')).expo;
const failures = [];
const warnings = [];

const expoMajor = Number(String(pkg.dependencies?.expo ?? '').match(/\d+/)?.[0]);
if (expoMajor !== 57) failures.push(`Expo SDK 57 is required for release; found ${pkg.dependencies?.expo}.`);
if (!app.ios?.bundleIdentifier) failures.push('ios.bundleIdentifier is missing.');
if (!app.android?.package) failures.push('android.package is missing.');
if (!app.ios?.buildNumber) failures.push('ios.buildNumber is missing.');
if (!Number.isInteger(app.android?.versionCode) || app.android.versionCode < 1) failures.push('android.versionCode must be a positive integer.');

for (const asset of [
  app.icon,
  app.ios?.icon,
  app.android?.adaptiveIcon?.foregroundImage,
  app.android?.adaptiveIcon?.monochromeImage,
  app.web?.favicon,
  './assets/splash-icon.png',
  './assets/notification-icon.png',
]) {
  if (!asset || !fs.existsSync(path.resolve(root, asset))) failures.push(`Missing release asset: ${asset ?? '<undefined>'}`);
}

const apiUrl = process.env.EXPO_PUBLIC_API_BASE_URL;
if (!apiUrl) warnings.push('EXPO_PUBLIC_API_BASE_URL is not set in this shell; EAS profiles do set it.');
else {
  let parsed;
  try { parsed = new URL(apiUrl); } catch { failures.push(`Invalid EXPO_PUBLIC_API_BASE_URL: ${apiUrl}`); }
  if (parsed && parsed.protocol !== 'https:') failures.push('Production API URL must use HTTPS.');
  if (parsed && /^(localhost|127\.0\.0\.1|0\.0\.0\.0)$/i.test(parsed.hostname)) failures.push('A store build cannot use a loopback API URL.');
}

const easProjectId = process.env.EXPO_PUBLIC_EAS_PROJECT_ID || app.extra?.eas?.projectId;
if (!easProjectId) warnings.push('EAS project ID is not set yet; push-token registration remains disabled until EAS initialization.');

console.log(`KynexOne Mobile ${pkg.version}`);
console.log(`iOS: ${app.ios.bundleIdentifier} build ${app.ios.buildNumber}`);
console.log(`Android: ${app.android.package} versionCode ${app.android.versionCode}`);
for (const warning of warnings) console.warn(`WARN: ${warning}`);
if (failures.length) {
  for (const failure of failures) console.error(`FAIL: ${failure}`);
  process.exit(1);
}
console.log('Release configuration checks passed.');
