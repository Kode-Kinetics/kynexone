import test from 'node:test';
import assert from 'node:assert/strict';
import { registerHooks } from 'node:module';
import { pathToFileURL } from 'node:url';

function moduleUrl(source: string): string {
  return `data:text/javascript,${encodeURIComponent(source)}`;
}

const mockModules = new Map<string, string>([
  ['expo-file-system/legacy', moduleUrl(`
    const harness = () => globalThis.__sensitiveFilesHarness;
    export const cacheDirectory = 'file:///cache/';
    export async function makeDirectoryAsync(...args) { return harness().makeDirectory(...args); }
    export async function downloadAsync(...args) { return harness().download(...args); }
    export async function deleteAsync(...args) { return harness().remove(...args); }
  `)],
  ['@/config', moduleUrl(`export const STORAGE_KEYS = { DEVICE_ID: 'device-id' };`)],
  ['@/storage', moduleUrl(`
    const harness = () => globalThis.__sensitiveFilesHarness;
    export const appStorage = {
      get: async (key) => harness().appValues.get(key) ?? null,
      set: async (key, value) => { harness().appValues.set(key, value); },
    };
    export const userStorage = { saveUser: async () => undefined };
    export async function loadCredentialEnvelope() { return harness().credentialEnvelope; }
    export async function saveCredentialEnvelope() {}
    export async function clearAllAuthData() { harness().clearAuthCalls += 1; }
  `)],
  ['@/api/services', moduleUrl(`
    const harness = () => globalThis.__sensitiveFilesHarness;
    export const authApi = {
      login: async () => { throw new Error('unused'); },
      logout: async () => { harness().logoutCalls += 1; },
      getMe: async () => harness().user,
      getMfaStatus: async () => null,
      verifyMfaChallenge: async () => { throw new Error('unused'); },
    };
    export const deviceApi = {
      register: async () => undefined,
      unregister: async () => undefined,
    };
  `)],
  ['@/api/client', moduleUrl(`
    export function initApiClient() {}
    export function setSessionExpiredHandler() {}
  `)],
  ['@/api/sessionTeardown', moduleUrl(`
    const harness = () => globalThis.__sensitiveFilesHarness;
    export function createSessionTeardownTransport() {
      return {
        revoke: async () => { harness().logoutCalls += 1; },
        unregister: async () => undefined,
      };
    }
  `)],
  ['@/utils/device', moduleUrl(`export async function generateDeviceId() { return 'device-1'; }`)],
  ['@/features/notifications/pushNotifications', moduleUrl(`export async function registerPushToken() {}`)],
  ['expo-device', moduleUrl(`export const modelName = 'Test'; export const osVersion = '1';`)],
  ['expo-constants', moduleUrl(`export default { expoConfig: { version: '1.0.0' } };`)],
  ['react-native', moduleUrl(`export const Platform = { OS: 'ios' };`)],
]);

const sourceRoot = pathToFileURL(`${process.cwd()}/src/`);
registerHooks({
  resolve(specifier, context, nextResolve) {
    const mock = mockModules.get(specifier);
    if (mock) return { url: mock, shortCircuit: true };
    if (specifier.startsWith('@/')) {
      return nextResolve(new URL(`${specifier.slice(2)}.ts`, sourceRoot).href, context);
    }
    if (
      specifier.startsWith('.')
      && !specifier.endsWith('.ts')
      && context.parentURL?.startsWith(sourceRoot.href)
    ) {
      return nextResolve(`${specifier}.ts`, context);
    }
    return nextResolve(specifier, context);
  },
});

interface DownloadCall {
  url: string;
  destination: string;
  options: { headers?: Record<string, string> };
}

const testUser = {
  id: 'user-1',
  tenantId: 'tenant-1',
  employeeId: 'employee-1',
  username: 'user@example.com',
  email: 'user@example.com',
  fullName: 'Test User',
  role: 'EMPLOYEE',
  accessMode: 'Mobile',
  permissions: [],
  isFirstLogin: false,
  isActive: true,
  mustChangePassword: false,
};

const harness = {
  appValues: new Map<string, unknown>(),
  credentialEnvelope: null as null | {
    version: 1;
    accessToken: string;
    refreshToken: string;
    expiresAt: number;
    tenantId: string;
    persistedAt: number;
  },
  user: testUser,
  directories: [] as string[],
  downloadCalls: [] as DownloadCall[],
  deleted: [] as string[],
  events: [] as string[],
  clearAuthCalls: 0,
  logoutCalls: 0,
  makeDirectory: async (uri: string) => {
    harness.directories.push(uri);
  },
  download: async (
    url: string,
    destination: string,
    options: { headers?: Record<string, string> },
  ) => {
    harness.downloadCalls.push({ url, destination, options });
    harness.events.push('download');
    return { uri: destination, status: 200, headers: {}, mimeType: 'application/pdf' };
  },
  remove: async (uri: string) => {
    harness.deleted.push(uri);
    harness.events.push('delete');
  },
};
(globalThis as typeof globalThis & { __sensitiveFilesHarness: typeof harness })
  .__sensitiveFilesHarness = harness;
(globalThis as typeof globalThis & { __DEV__: boolean }).__DEV__ = false;

function resetHarness(): void {
  harness.appValues.clear();
  harness.credentialEnvelope = null;
  harness.directories.length = 0;
  harness.downloadCalls.length = 0;
  harness.deleted.length = 0;
  harness.events.length = 0;
  harness.clearAuthCalls = 0;
  harness.logoutCalls = 0;
  harness.makeDirectory = async (uri) => {
    harness.directories.push(uri);
  };
  harness.download = async (url, destination, options) => {
    harness.downloadCalls.push({ url, destination, options });
    harness.events.push('download');
    return { uri: destination, status: 200, headers: {}, mimeType: 'application/pdf' };
  };
  harness.remove = async (uri) => {
    harness.deleted.push(uri);
    harness.events.push('delete');
  };
}

const { purgeSensitiveFiles, withSensitiveDownload } = await import('../src/security/sensitiveFiles.ts');
const { useAuthStore } = await import('../src/auth/authStore.ts');

test('downloads to distinct randomized paths under the cache directory', async () => {
  resetHarness();
  const options = {
    url: 'https://api.example.test/payslips/payroll-record-42',
    headers: { Authorization: 'Bearer secret' },
    fileExtension: 'pdf',
  };

  await withSensitiveDownload(options, async () => undefined);
  await withSensitiveDownload(options, async () => undefined);

  const destinations = harness.downloadCalls.map((call) => call.destination);
  assert.equal(new Set(destinations).size, 2);
  for (const destination of destinations) {
    assert.match(destination, /^file:\/\/\/cache\/kynexone-sensitive\/[a-z0-9-]+\.pdf$/);
    assert.equal(destination.includes('payroll-record-42'), false);
  }
  assert.deepEqual(harness.downloadCalls[0]?.options, {
    headers: { Authorization: 'Bearer secret' },
  });
});

test('deletes the temporary file after a successful operation', async () => {
  resetHarness();

  await withSensitiveDownload({ url: 'https://api.example.test/file' }, async (uri) => {
    assert.equal(uri, harness.downloadCalls[0]?.destination);
    harness.events.push('operation');
  });

  assert.deepEqual(harness.events, ['download', 'operation', 'delete']);
  assert.deepEqual(harness.deleted, [harness.downloadCalls[0]?.destination]);
});

test('deletes the temporary file when a share operation is cancelled', async () => {
  resetHarness();
  const cancellation = Object.assign(new Error('Share cancelled'), { code: 'ERR_CANCELED' });

  await assert.rejects(
    withSensitiveDownload(
      { url: 'https://api.example.test/file' },
      async () => { throw cancellation; },
    ),
    (error) => error === cancellation,
  );

  assert.deepEqual(harness.deleted, [harness.downloadCalls[0]?.destination]);
});

test('deletes the temporary file after an operation failure', async () => {
  resetHarness();
  const operationFailure = new Error('preview unavailable');

  await assert.rejects(
    withSensitiveDownload(
      { url: 'https://api.example.test/file' },
      async () => { throw operationFailure; },
    ),
    (error) => error === operationFailure,
  );

  assert.deepEqual(harness.deleted, [harness.downloadCalls[0]?.destination]);
});

test('a cleanup failure does not replace the operation failure', async () => {
  resetHarness();
  const operationFailure = new Error('preview unavailable');
  harness.remove = async () => {
    throw new Error('cache delete unavailable');
  };

  await assert.rejects(
    withSensitiveDownload(
      { url: 'https://api.example.test/file' },
      async () => { throw operationFailure; },
    ),
    (error) => error === operationFailure,
  );
});

test('deletes a partial temporary file when the download fails', async () => {
  resetHarness();
  const downloadFailure = new Error('network interrupted');
  harness.download = async (url, destination, options) => {
    harness.downloadCalls.push({ url, destination, options });
    throw downloadFailure;
  };
  let operations = 0;

  await assert.rejects(
    withSensitiveDownload(
      { url: 'https://api.example.test/file' },
      async () => { operations += 1; },
    ),
    (error) => error === downloadFailure,
  );

  assert.equal(operations, 0);
  assert.deepEqual(harness.deleted, [harness.downloadCalls[0]?.destination]);
});

test('purges the sensitive cache directory idempotently', async () => {
  resetHarness();

  await purgeSensitiveFiles();

  assert.deepEqual(harness.deleted, ['file:///cache/kynexone-sensitive/']);
});

test('orphan purge is best-effort when cache deletion is unavailable', async () => {
  resetHarness();
  harness.remove = async () => {
    throw new Error('cache delete unavailable');
  };

  await assert.doesNotReject(purgeSensitiveFiles());
});

test('authenticated startup purges orphaned sensitive files', async () => {
  resetHarness();
  harness.credentialEnvelope = {
    version: 1,
    accessToken: 'access-token',
    refreshToken: 'refresh-token',
    expiresAt: 1_800_000_000_000,
    tenantId: 'tenant-1',
    persistedAt: 1_700_000_000_000,
  };
  useAuthStore.setState({
    user: null,
    tenantId: null,
    isAuthenticated: false,
    isLoading: false,
    isInitialized: false,
    error: null,
    sessionExpired: false,
  });

  await useAuthStore.getState().initialize();

  assert.equal(useAuthStore.getState().isAuthenticated, true);
  assert.deepEqual(harness.deleted, ['file:///cache/kynexone-sensitive/']);
});

test('logout purges orphaned sensitive files before session teardown completes', async () => {
  resetHarness();
  harness.credentialEnvelope = {
    version: 1,
    accessToken: 'access-token',
    refreshToken: 'refresh-token',
    expiresAt: 1_800_000_000_000,
    tenantId: 'tenant-1',
    persistedAt: 1_700_000_000_000,
  };
  useAuthStore.setState({
    user: testUser,
    tenantId: 'tenant-1',
    isAuthenticated: true,
    isLoading: false,
    isInitialized: true,
    error: null,
    sessionExpired: false,
  });

  await useAuthStore.getState().logout();

  assert.equal(harness.logoutCalls, 1);
  assert.equal(harness.clearAuthCalls, 1);
  assert.deepEqual(harness.deleted, ['file:///cache/kynexone-sensitive/']);
});
