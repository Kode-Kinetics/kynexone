// Dynamic Expo config. Static identifiers and native plugin settings live in
// app.json; this file injects environment-specific values and release guards.
//
// Store identifiers are defined in app.json and must remain stable after the
// first App Store / Play listing is created. The initial KynexOne identity is
// com.kodekinetics.kynexone with URL scheme kynexone.
const fs = require('fs');
const path = require('path');

function readDotEnv() {
  const envPath = path.join(__dirname, '.env');
  if (!fs.existsSync(envPath)) return {};

  return fs.readFileSync(envPath, 'utf8').split(/\r?\n/).reduce((env, line) => {
    const trimmed = line.trim();
    if (!trimmed || trimmed.startsWith('#')) return env;

    const separator = trimmed.indexOf('=');
    if (separator === -1) return env;

    const key = trimmed.slice(0, separator).trim();
    const value = trimmed.slice(separator + 1).trim().replace(/^['"]|['"]$/g, '');
    env[key] = value;
    return env;
  }, {});
}

const fileEnv = readDotEnv();
const value = (key) => process.env[key] || fileEnv[key] || undefined;
const appEnvironment = value('EXPO_PUBLIC_APP_ENV') || 'development';
const apiBaseUrl = value('EXPO_PUBLIC_API_BASE_URL') || 'http://localhost:5117/api';
const easProjectId = value('EXPO_PUBLIC_EAS_PROJECT_ID');

function assertSafeReleaseConfig() {
  if (appEnvironment !== 'production') return;

  let parsed;
  try {
    parsed = new URL(apiBaseUrl);
  } catch {
    throw new Error(`Production EXPO_PUBLIC_API_BASE_URL is invalid: ${apiBaseUrl}`);
  }

  if (parsed.protocol !== 'https:') {
    throw new Error('Production mobile builds must use an HTTPS API URL.');
  }
  if (/^(localhost|127\.0\.0\.1|0\.0\.0\.0)$/i.test(parsed.hostname)) {
    throw new Error('Production mobile builds cannot use a loopback API URL.');
  }
}

assertSafeReleaseConfig();

module.exports = ({ config }) => ({
  ...config,
  extra: {
    ...(config.extra || {}),
    apiBaseUrl,
    releaseChannel: appEnvironment,
    appEnvironment,
    ...(easProjectId ? { eas: { projectId: easProjectId } } : {}),
  },
});
