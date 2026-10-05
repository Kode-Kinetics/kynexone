// Dynamic Expo config. Static identifiers and native plugin settings live in
// app.json; this file injects environment-specific values and release guards.
//
// Store identifiers are defined in app.json and must remain stable after the
// first App Store / Play listing is created. The initial KynexOne identity is
// com.kodekinetics.kynexone with URL scheme kynexone.
const fs = require('fs');
const path = require('path');

function readDotEnv() {
  // EXPO_NO_DOTENV is Expo's switch for "no .env files"; the OTA publish and the
  // runtime-version check set it so a developer's local .env can never reach a
  // production update. This reader honours it too.
  if (process.env.EXPO_NO_DOTENV) return {};
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

// OTA updates (expo-updates). The update URL is derived from the SAME project ID
// the build is linked to, so an env override can never point a binary at another
// project's updates. The fingerprint runtime version changes whenever native code
// or native config changes, so an OTA update can only reach binaries it fits.
// fallbackToCacheTimeout 0: launch from the cached bundle immediately and apply a
// downloaded update on the next launch; startup never waits on the network.
// Optional code signing (see README "Code signing"). Only the PUBLIC certificate
// path is configured here; the private key never enters the repo or the app.
// Unset = unsigned updates. Turning it on changes the runtime version, so decide
// before the first store build.
const codeSigningCertificate = value('EXPO_UPDATES_CODE_SIGNING_CERT');

function codeSigningConfig() {
  if (!codeSigningCertificate) return {};
  if (!fs.existsSync(path.resolve(__dirname, codeSigningCertificate))) {
    throw new Error(`EXPO_UPDATES_CODE_SIGNING_CERT points to a missing file: ${codeSigningCertificate}`);
  }
  return {
    codeSigningCertificate,
    codeSigningMetadata: { keyid: 'main', alg: 'rsa-v1_5-sha256' },
  };
}

function updatesConfig(projectId) {
  if (!projectId) {
    if (appEnvironment === 'production') {
      throw new Error('Production mobile builds need an EAS project ID for the OTA update URL.');
    }
    return {};
  }
  return {
    runtimeVersion: { policy: 'fingerprint' },
    updates: {
      url: `https://u.expo.dev/${projectId}`,
      checkAutomatically: 'ON_LOAD',
      fallbackToCacheTimeout: 0,
      ...codeSigningConfig(),
    },
  };
}

module.exports = ({ config }) => {
  const projectId = easProjectId || config.extra?.eas?.projectId;
  return {
    ...config,
    ...updatesConfig(projectId),
    extra: {
      ...(config.extra || {}),
      apiBaseUrl,
      releaseChannel: appEnvironment,
      appEnvironment,
      ...(projectId ? { eas: { ...(config.extra?.eas || {}), projectId } } : {}),
    },
  };
};
