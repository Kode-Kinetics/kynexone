import fs from 'node:fs';
import path from 'node:path';

const root = process.cwd();
const pkg = JSON.parse(fs.readFileSync(path.join(root, 'package.json'), 'utf8'));
const app = JSON.parse(fs.readFileSync(path.join(root, 'app.json'), 'utf8')).expo;
const eas = JSON.parse(fs.readFileSync(path.join(root, 'eas.json'), 'utf8'));
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

const productionEnv = eas.build?.production?.env ?? {};
if (productionEnv.EXPO_PUBLIC_APP_ENV !== 'production') {
  failures.push('EAS production profile must set EXPO_PUBLIC_APP_ENV=production.');
}
const apiUrl = process.env.EXPO_PUBLIC_API_BASE_URL || productionEnv.EXPO_PUBLIC_API_BASE_URL;
if (!apiUrl) failures.push('Production EXPO_PUBLIC_API_BASE_URL is missing from the shell and EAS production profile.');
else {
  let parsed;
  try { parsed = new URL(apiUrl); } catch { failures.push(`Invalid EXPO_PUBLIC_API_BASE_URL: ${apiUrl}`); }
  if (parsed && parsed.protocol !== 'https:') failures.push('Production API URL must use HTTPS.');
  if (parsed && /^(localhost|127\.0\.0\.1|0\.0\.0\.0)$/i.test(parsed.hostname)) failures.push('A store build cannot use a loopback API URL.');
}

const easProjectId = process.env.EXPO_PUBLIC_EAS_PROJECT_ID || app.extra?.eas?.projectId;
if (!easProjectId) warnings.push('EAS project ID is not set yet; push-token registration remains disabled until EAS initialization.');

if (eas.cli?.appVersionSource !== 'remote') {
  failures.push('eas.cli.appVersionSource must be remote so EAS owns production build-number increments.');
}

const infoPlist = fs.readFileSync(path.join(root, 'ios/KynexOne/Info.plist'), 'utf8');
if (!infoPlist.includes('<string>$(MARKETING_VERSION)</string>')) {
  failures.push('Info.plist CFBundleShortVersionString must use $(MARKETING_VERSION).');
}
if (!infoPlist.includes('<string>$(CURRENT_PROJECT_VERSION)</string>')) {
  failures.push('Info.plist CFBundleVersion must use $(CURRENT_PROJECT_VERSION).');
}

const entitlements = fs.readFileSync(path.join(root, 'ios/KynexOne/KynexOne.entitlements'), 'utf8');
if (!entitlements.includes('<string>$(APS_ENVIRONMENT)</string>')) {
  failures.push('Push entitlement must use the configuration-specific $(APS_ENVIRONMENT) build setting.');
}

console.log(`KynexOne Mobile ${pkg.version}`);
console.log(`iOS: ${app.ios.bundleIdentifier} build ${app.ios.buildNumber}`);
console.log(`Android: ${app.android.package} versionCode ${app.android.versionCode}`);
for (const warning of warnings) console.warn(`WARN: ${warning}`);
if (failures.length) {
  for (const failure of failures) console.error(`FAIL: ${failure}`);
  process.exit(1);
}
console.log('Release configuration checks passed.');
