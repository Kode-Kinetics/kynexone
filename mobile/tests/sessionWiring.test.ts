import test from 'node:test';
import assert from 'node:assert/strict';
import { registerHooks } from 'node:module';
import { pathToFileURL } from 'node:url';
import axios, { AxiosError, type AxiosRequestConfig, type AxiosResponse } from 'axios';
import {
  beginSession,
  captureSession,
  invalidateSession,
  mayCommit,
} from '../src/auth/sessionGeneration.ts';

function moduleUrl(source: string): string {
  return `data:text/javascript,${encodeURIComponent(source)}`;
}

const mockModules = new Map<string, string>([
  ['@/config', moduleUrl(`
    export const APP_CONFIG = { API_BASE_URL: 'https://api.example.test', TENANT_HEADER: 'X-Tenant-Id' };
    export const API_TIMEOUT_MS = 1000;
    export const STORAGE_KEYS = { DEVICE_ID: 'device-id' };
  `)],
  ['@/storage', moduleUrl(`
    const harness = () => globalThis.__sessionWiringHarness;
    export const tokenStorage = {
      getAccessToken: async () => harness().accessToken,
      getRefreshToken: async () => harness().getRefreshToken(),
      saveTokens: async (tokens) => harness().saveTokens(tokens),
      isTokenExpired: async () => false,
    };
    export const userStorage = {
      getUser: async () => harness().storedUser,
      saveUser: async (user) => { harness().storedUser = user; },
    };
    export const appStorage = {
      get: async (key) => harness().appValues.get(key) ?? null,
      set: async (key, value) => { harness().appValues.set(key, value); },
    };
    export async function loadCredentialEnvelope() {
      return harness().loadCredentialEnvelope();
    }
    export async function saveCredentialEnvelope(envelope) {
      return harness().saveCredentialEnvelope(envelope);
    }
    export async function clearAllAuthData() { await harness().clearAuthData(); }
  `)],
  ['@/api/services', moduleUrl(`
    const harness = () => globalThis.__sessionWiringHarness;
    export const authApi = {
      login: async (...args) => harness().login(...args),
      logout: async (...args) => harness().logout(...args),
      getMe: async () => harness().getMe(),
      getMfaStatus: async () => null,
      verifyMfaChallenge: async () => { throw new Error('unused'); },
    };
    export const deviceApi = {
      register: async () => undefined,
      unregister: async (...args) => harness().unregister(...args),
    };
  `)],
  ['@/utils/device', moduleUrl(`export async function generateDeviceId() { return 'device-1'; }`)],
  ['@/features/notifications/pushNotifications', moduleUrl(`export async function registerPushToken() {}`)],
  ['expo-device', moduleUrl(`export const modelName = 'Test'; export const osVersion = '1';`)],
  ['expo-constants', moduleUrl(`export default { expoConfig: { version: '1.0.0' } };`)],
  ['react-native', moduleUrl(`export const Platform = { OS: 'ios' };`)],
]);

const publicAuthClientUrl = moduleUrl(`
  export function createIsolatedPublicAuthClient() {
    return {
      interceptors: { response: { use() {} } },
      post: async (...args) => globalThis.__sessionWiringHarness.requestRefresh(...args),
    };
  }
`);

const sourceRoot = pathToFileURL(`${process.cwd()}/src/`);
registerHooks({
  resolve(specifier, context, nextResolve) {
    const mock = mockModules.get(specifier);
    if (mock) return { url: mock, shortCircuit: true };
    if (specifier === './publicAuthClient' && context.parentURL?.endsWith('/src/api/client.ts')) {
      return { url: publicAuthClientUrl, shortCircuit: true };
    }
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

const { createApiClient, SessionExpiredError, setSessionExpiredHandler } = await import('../src/api/client.ts');
const { useAuthStore } = await import('../src/auth/authStore.ts');
const { commitRefreshSession } = await import('../src/auth/sessionLifecycle.ts');

interface StoredTokens {
  accessToken: string;
  refreshToken: string;
  expiresAt: number;
}

function deferred<T = void>() {
  let resolve!: (value: T | PromiseLike<T>) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

const harness = {
  accessToken: null as string | null,
  refreshToken: null as string | null,
  storedUser: null as any,
  appValues: new Map<string, unknown>(),
  clearCalls: 0,
  logoutCalls: 0,
  envelopeSaveCalls: 0,
  getRefreshToken: async () => harness.refreshToken,
  loadCredentialEnvelope: async () => {
    const refreshToken = await harness.getRefreshToken();
    return harness.accessToken && refreshToken
      ? {
          version: 1,
          accessToken: harness.accessToken,
          refreshToken,
          expiresAt: 1_800_000_000_000,
          tenantId: 'tenant-1',
          persistedAt: 1_700_000_000_000,
        }
      : null;
  },
  saveCredentialEnvelope: async (envelope: StoredTokens & { tenantId: string }) => {
    harness.envelopeSaveCalls += 1;
    harness.accessToken = envelope.accessToken;
    harness.refreshToken = envelope.refreshToken;
  },
  saveTokens: async (tokens: StoredTokens) => {
    harness.accessToken = tokens.accessToken;
    harness.refreshToken = tokens.refreshToken;
  },
  clearAuthData: async () => {
    harness.clearCalls += 1;
    harness.accessToken = null;
    harness.refreshToken = null;
    harness.storedUser = null;
  },
  requestRefresh: async (..._args: unknown[]): Promise<never> => {
    throw new Error('unexpected refresh');
  },
  getMe: async () => harness.storedUser,
  login: async (..._args: unknown[]) => { throw new Error('unexpected login'); },
  logout: async (..._args: unknown[]) => { harness.logoutCalls += 1; },
  unregister: async (..._args: unknown[]) => undefined,
};
(globalThis as any).__sessionWiringHarness = harness;
(globalThis as any).__DEV__ = false;

function resetHarness(): void {
  axios.defaults.adapter = async (config) => {
    if (config.url === '/auth/logout') await harness.logout(config);
    else if (config.url?.startsWith('/mobile/register-device/')) await harness.unregister(config);
    else throw new Error('Unexpected teardown URL');
    return { data: {}, status: 200, statusText: 'OK', headers: {}, config };
  };
  harness.accessToken = null;
  harness.refreshToken = null;
  harness.storedUser = null;
  harness.appValues.clear();
  harness.clearCalls = 0;
  harness.logoutCalls = 0;
  harness.envelopeSaveCalls = 0;
  harness.getRefreshToken = async () => harness.refreshToken;
  harness.loadCredentialEnvelope = async () => {
    const refreshToken = await harness.getRefreshToken();
    return harness.accessToken && refreshToken
      ? {
          version: 1,
          accessToken: harness.accessToken,
          refreshToken,
          expiresAt: 1_800_000_000_000,
          tenantId: 'tenant-1',
          persistedAt: 1_700_000_000_000,
        }
      : null;
  };
  harness.saveCredentialEnvelope = async (envelope) => {
    harness.envelopeSaveCalls += 1;
    harness.accessToken = envelope.accessToken;
    harness.refreshToken = envelope.refreshToken;
  };
  harness.saveTokens = async (tokens) => {
    harness.accessToken = tokens.accessToken;
    harness.refreshToken = tokens.refreshToken;
  };
  harness.clearAuthData = async () => {
    harness.clearCalls += 1;
    harness.accessToken = null;
    harness.refreshToken = null;
    harness.storedUser = null;
  };
  harness.requestRefresh = async () => { throw new Error('unexpected refresh'); };
  harness.getMe = async () => harness.storedUser;
  harness.login = async () => { throw new Error('unexpected login'); };
  harness.logout = async () => { harness.logoutCalls += 1; };
  harness.unregister = async () => undefined;
}

function clientReturning401() {
  const client = createApiClient('tenant-1');
  client.defaults.adapter = async (config: AxiosRequestConfig) => {
    const response: AxiosResponse = {
      data: { message: 'Expired' },
      status: 401,
      statusText: 'Unauthorized',
      headers: {},
      config: config as any,
    };
    throw new AxiosError('Expired', 'ERR_BAD_REQUEST', config as any, undefined, response);
  };
  return client;
}

function testUser() {
  return {
    id: 'user-1', tenantId: 'tenant-1', employeeId: 'employee-1', username: 'user@example.com',
    email: 'user@example.com', fullName: 'Test User', role: 'EMPLOYEE', accessMode: 'Mobile',
    permissions: [], isFirstLogin: false, isActive: true, mustChangePassword: false,
  };
}

test('a delayed 401 from the previous login never refreshes or replays using the new login', async () => {
  resetHarness();
  harness.accessToken = 'access-a';
  harness.refreshToken = 'refresh-a';
  beginSession('refresh-a');
  let refreshes = 0;
  harness.requestRefresh = async () => {
    refreshes += 1;
    throw new Error('must not refresh the new session');
  };
  let expirations = 0;
  setSessionExpiredHandler(() => { expirations += 1; });
  const dispatched = deferred();
  const releaseResponse = deferred();
  const client = createApiClient('tenant-1');
  let attempts = 0;
  client.defaults.adapter = async (config) => {
    attempts += 1;
    dispatched.resolve();
    await releaseResponse.promise;
    throw new AxiosError('Expired', 'ERR_BAD_REQUEST', config, undefined, {
      data: {}, status: 401, statusText: 'Unauthorized', headers: {}, config,
    });
  };
  const request = client.post('/approvals/decision', { action: 'APPROVE' });
  await dispatched.promise;
  beginSession('refresh-b');
  harness.accessToken = 'access-b';
  harness.refreshToken = 'refresh-b';
  releaseResponse.resolve();
  await assert.rejects(request, SessionExpiredError);
  assert.equal(attempts, 1);
  assert.equal(refreshes, 0);
  assert.equal(expirations, 0);
  assert.equal(harness.clearCalls, 0);
  assert.equal(harness.refreshToken, 'refresh-b');
});

test('staggered 401 responses within one login reuse the successfully rotated token', async () => {
  resetHarness();
  beginSession('refresh-a');
  harness.accessToken = 'access-a';
  harness.refreshToken = 'refresh-a';
  let refreshes = 0;
  harness.requestRefresh = async () => {
    refreshes += 1;
    return { data: { accessToken: 'access-rotated', refreshToken: 'refresh-rotated', expiresAtUtc: '2027-01-15T08:00:00.000Z' } } as never;
  };
  const lateDispatched = deferred();
  const releaseLate = deferred();
  const client = createApiClient('tenant-1');
  client.defaults.adapter = async (config) => {
    if (config.headers.Authorization === 'Bearer access-rotated') {
      return { data: { ok: true }, status: 200, statusText: 'OK', headers: {}, config };
    }
    if (config.url === '/late') { lateDispatched.resolve(); await releaseLate.promise; }
    throw new AxiosError('Expired', 'ERR_BAD_REQUEST', config, undefined, {
      data: {}, status: 401, statusText: 'Unauthorized', headers: {}, config,
    });
  };
  const late = client.get('/late');
  await lateDispatched.promise;
  assert.equal((await client.get('/early')).data.ok, true);
  releaseLate.resolve();
  assert.equal((await late).data.ok, true);
  assert.equal(refreshes, 1);
  assert.equal(harness.clearCalls, 0);
});

test('a login change between credential validation and dispatch cannot relabel the old request', async () => {
  resetHarness();
  beginSession('refresh-a');
  harness.accessToken = 'access-a';
  harness.refreshToken = 'refresh-a';
  const read = harness.loadCredentialEnvelope;
  harness.loadCredentialEnvelope = async () => {
    const envelope = await read();
    queueMicrotask(() => queueMicrotask(() => queueMicrotask(() => {
      beginSession('refresh-b');
      harness.accessToken = 'access-b';
      harness.refreshToken = 'refresh-b';
    })));
    return envelope;
  };
  let dispatches = 0;
  const client = createApiClient('tenant-1');
  client.defaults.adapter = async (config) => {
    dispatches += 1;
    return { data: {}, status: 200, statusText: 'OK', headers: {}, config };
  };
  await assert.rejects(client.post('/decision', {}), SessionExpiredError);
  assert.equal(dispatches, 0);
  assert.equal(harness.clearCalls, 0);
});

test('logout sends isolated revocation and unregister requests after immediate invalidation', async () => {
  resetHarness();
  beginSession('refresh-a');
  const oldSession = captureSession();
  harness.accessToken = 'access-a';
  harness.refreshToken = 'refresh-a';
  harness.appValues.set('device-id', 'device/a');
  const started = deferred();
  const release = deferred();
  const requests: { url: string | undefined; token: unknown; tenant: unknown }[] = [];
  axios.defaults.adapter = async (config) => {
    requests.push({ url: config.url, token: config.headers.Authorization, tenant: config.headers['X-Tenant-Id'] });
    if (requests.length === 2) started.resolve();
    await release.promise;
    throw new AxiosError('Already expired', 'ERR_BAD_REQUEST', config, undefined, {
      data: {}, status: 401, statusText: 'Unauthorized', headers: {}, config,
    });
  };
  const logout = useAuthStore.getState().logout();
  assert.equal(mayCommit(oldSession, 'refresh-a'), false);
  await started.promise;
  const newLogin = useAuthStore.getState().finishLogin(testUser(), {
    accessToken: 'access-b', refreshToken: 'refresh-b', expiresAt: 1000,
  }, 'tenant-2');
  release.resolve();
  await logout;
  await newLogin;
  assert.deepEqual(requests, [
    { url: '/auth/logout', token: 'Bearer access-a', tenant: 'tenant-1' },
    { url: '/mobile/register-device/device%2Fa', token: 'Bearer access-a', tenant: 'tenant-1' },
  ]);
  assert.equal(harness.refreshToken, 'refresh-b');
  assert.equal(useAuthStore.getState().isAuthenticated, true);
});

test('a new login uses its own refresh while the previous login refresh is still pending', async () => {
  resetHarness();
  harness.accessToken = 'access-a';
  harness.refreshToken = 'refresh-a';
  beginSession('refresh-a');
  const oldStarted = deferred();
  const oldResult = deferred<never>();
  const refreshed: unknown[] = [];
  harness.requestRefresh = async (...args) => {
    refreshed.push(args[1]);
    if (refreshed.length === 1) {
      oldStarted.resolve();
      return oldResult.promise;
    }
    return { data: { accessToken: 'access-c', refreshToken: 'refresh-c', expiresAtUtc: '2027-01-15T08:00:00.000Z' } } as never;
  };
  const oldRequest = clientReturning401().get('/old');
  const oldRejection = assert.rejects(oldRequest, SessionExpiredError);
  await oldStarted.promise;
  beginSession('refresh-b');
  harness.accessToken = 'access-b';
  harness.refreshToken = 'refresh-b';
  const client = createApiClient('tenant-1');
  client.defaults.adapter = async (config) => {
    if (config.headers.Authorization !== 'Bearer access-c') {
      // Settle the old refresh only once the new login's request has dispatched.
      oldResult.reject(new Error('old refresh failed'));
      throw new AxiosError('Expired', 'ERR_BAD_REQUEST', config, undefined, {
        data: {}, status: 401, statusText: 'Unauthorized', headers: {}, config,
      });
    }
    return { data: { ok: true }, status: 200, statusText: 'OK', headers: {}, config };
  };
  const response = await client.get('/new');
  await oldRejection;
  assert.deepEqual(response.data, { ok: true });
  assert.deepEqual(refreshed, [{ refreshToken: 'refresh-a' }, { refreshToken: 'refresh-b' }]);
  assert.equal(harness.refreshToken, 'refresh-c');
  assert.equal(harness.clearCalls, 0);
});

test('API interceptor does not clear a newer login when an old refresh fails', async () => {
  resetHarness();
  harness.accessToken = 'access-a';
  harness.refreshToken = 'refresh-a';
  beginSession('refresh-a');
  const networkStarted = deferred();
  const network = deferred<never>();
  harness.requestRefresh = async () => {
    networkStarted.resolve();
    return network.promise;
  };
  let expirations = 0;
  setSessionExpiredHandler(() => { expirations += 1; });

  const request = clientReturning401().get('/protected');
  await networkStarted.promise;
  beginSession('refresh-b');
  harness.accessToken = 'access-b';
  harness.refreshToken = 'refresh-b';
  network.reject(new Error('old refresh rejected'));

  await assert.rejects(request, SessionExpiredError);
  assert.equal(harness.clearCalls, 0);
  assert.equal(expirations, 0);
  assert.equal(harness.refreshToken, 'refresh-b');
});

test('API interceptor expires a current session whose refresh token is missing', async () => {
  resetHarness();
  harness.accessToken = 'access-a';
  harness.refreshToken = null;
  beginSession('refresh-a');
  let expirations = 0;
  setSessionExpiredHandler(() => { expirations += 1; });

  await assert.rejects(clientReturning401().get('/protected'), SessionExpiredError);

  assert.equal(harness.clearCalls, 1);
  assert.equal(expirations, 1);
  assert.equal(harness.accessToken, null);
});

test('API interceptor expires a current session whose refresh token is unreadable', async () => {
  resetHarness();
  harness.accessToken = 'access-a';
  harness.refreshToken = 'refresh-a';
  harness.getRefreshToken = async () => { throw new Error('secure storage unavailable'); };
  beginSession('refresh-a');
  let expirations = 0;
  setSessionExpiredHandler(() => { expirations += 1; });

  await assert.rejects(clientReturning401().get('/protected'), SessionExpiredError);

  assert.equal(harness.clearCalls, 1);
  assert.equal(expirations, 1);
  assert.equal(harness.accessToken, null);
});

test('real auth store receives missing and unreadable token expiration from the API interceptor', async () => {
  for (const unreadable of [false, true]) {
    resetHarness();
    useAuthStore.setState({
      user: null,
      tenantId: null,
      isAuthenticated: false,
      isLoading: false,
      isInitialized: false,
      error: null,
      sessionExpired: false,
    });
    const user = testUser();
    harness.login = async () => ({
      kind: 'authenticated',
      user,
      tokens: { accessToken: 'access-login', refreshToken: 'refresh-login', expiresAt: 1000 },
    });

    await useAuthStore.getState().login('user@example.com', 'password', 'tenant-1');
    assert.equal(useAuthStore.getState().isAuthenticated, true);
    if (unreadable) {
      harness.getRefreshToken = async () => { throw new Error('secure storage unavailable'); };
    } else {
      harness.refreshToken = null;
    }

    await assert.rejects(clientReturning401().get('/protected'), SessionExpiredError);

    assert.equal(useAuthStore.getState().isAuthenticated, false, unreadable ? 'unreadable' : 'missing');
    assert.equal(useAuthStore.getState().sessionExpired, true, unreadable ? 'unreadable' : 'missing');
    assert.equal(harness.clearCalls, 1, unreadable ? 'unreadable' : 'missing');
  }
});

test('login does not report an authenticated state when the credential envelope write fails', async () => {
  resetHarness();
  useAuthStore.setState({
    user: null,
    tenantId: null,
    isAuthenticated: false,
    isLoading: false,
    isInitialized: false,
    error: null,
    sessionExpired: false,
  });
  const user = testUser();
  harness.login = async () => ({
    kind: 'authenticated',
    user,
    tokens: { accessToken: 'access-login', refreshToken: 'refresh-login', expiresAt: 1000 },
  });
  harness.saveCredentialEnvelope = async () => {
    harness.envelopeSaveCalls += 1;
    throw new Error('secure storage unavailable');
  };

  await assert.rejects(
    useAuthStore.getState().login('user@example.com', 'password', 'tenant-1'),
    /secure storage unavailable/,
  );

  assert.equal(harness.envelopeSaveCalls, 1);
  assert.equal(useAuthStore.getState().isAuthenticated, false);
});

test('cold restore establishes a generation from the envelope before protected traffic', async () => {
  resetHarness();
  useAuthStore.setState({
    user: null,
    tenantId: null,
    isAuthenticated: false,
    isLoading: false,
    isInitialized: false,
    error: null,
    sessionExpired: false,
  });
  harness.accessToken = 'access-restored';
  harness.refreshToken = 'refresh-restored';
  harness.storedUser = null;
  harness.getMe = async () => testUser();

  await useAuthStore.getState().initialize();

  assert.equal(useAuthStore.getState().isAuthenticated, true);
  assert.equal(useAuthStore.getState().tenantId, 'tenant-1');
  assert.equal(mayCommit(captureSession(), 'refresh-restored'), true);
});

test('successful API refresh commits one updated credential envelope', async () => {
  resetHarness();
  harness.accessToken = 'access-a';
  harness.refreshToken = 'refresh-a';
  beginSession('refresh-a');
  harness.requestRefresh = async () => ({
    data: {
      accessToken: 'access-b',
      refreshToken: 'refresh-b',
      expiresAtUtc: '2027-01-15T08:00:00.000Z',
    },
  });
  const client = createApiClient('tenant-1');
  let attempts = 0;
  client.defaults.adapter = async (config: AxiosRequestConfig) => {
    attempts += 1;
    if (attempts === 1) {
      const response: AxiosResponse = {
        data: { message: 'Expired' },
        status: 401,
        statusText: 'Unauthorized',
        headers: {},
        config: config as any,
      };
      throw new AxiosError('Expired', 'ERR_BAD_REQUEST', config as any, undefined, response);
    }
    return {
      data: { ok: true },
      status: 200,
      statusText: 'OK',
      headers: {},
      config,
    } as AxiosResponse;
  };

  const response = await client.get('/protected');

  assert.deepEqual(response.data, { ok: true });
  assert.equal(harness.envelopeSaveCalls, 1);
  assert.equal(harness.accessToken, 'access-b');
  assert.equal(harness.refreshToken, 'refresh-b');
});

test('auth store login and logout use serialized session persistence', async () => {
  resetHarness();
  useAuthStore.setState({
    user: null,
    tenantId: null,
    isAuthenticated: false,
    isLoading: false,
    isInitialized: false,
    error: null,
    sessionExpired: false,
  });
  const user = testUser();
  const loginTokens = { accessToken: 'access-login', refreshToken: 'refresh-login', expiresAt: 1000 };
  harness.login = async () => ({ kind: 'authenticated', user, tokens: loginTokens });
  const loginSaveStarted = deferred();
  const releaseLoginSave = deferred();
  harness.saveCredentialEnvelope = async (tokens) => {
    harness.envelopeSaveCalls += 1;
    loginSaveStarted.resolve();
    await releaseLoginSave.promise;
    harness.accessToken = tokens.accessToken;
    harness.refreshToken = tokens.refreshToken;
  };

  const login = useAuthStore.getState().login('user@example.com', 'password', 'tenant-1');
  await loginSaveStarted.promise;
  assert.equal(mayCommit(captureSession(), 'refresh-login'), true);
  releaseLoginSave.resolve();
  await login;
  assert.equal(useAuthStore.getState().isAuthenticated, true);

  const oldRefresh = captureSession();
  const refreshSaveStarted = deferred();
  const releaseRefreshSave = deferred();
  const refreshCommit = commitRefreshSession(
    oldRefresh,
    'refresh-rotated',
    async () => harness.refreshToken,
    async () => {
      refreshSaveStarted.resolve();
      await releaseRefreshSave.promise;
      harness.refreshToken = 'refresh-rotated';
    },
  );
  await refreshSaveStarted.promise;

  const logout = useAuthStore.getState().logout();
  assert.equal(mayCommit(oldRefresh, 'refresh-login'), false);
  releaseRefreshSave.resolve();

  assert.equal(await refreshCommit, false);
  await logout;
  assert.equal(harness.logoutCalls, 1);
  assert.equal(harness.clearCalls, 1);
  assert.equal(harness.refreshToken, null);
  assert.equal(useAuthStore.getState().isAuthenticated, false);
});

test('initialize clears mandatory in-memory state when credential cleanup fails', async () => {
  resetHarness();
  useAuthStore.setState({
    user: testUser(),
    tenantId: 'tenant-1',
    isAuthenticated: true,
    isLoading: false,
    isInitialized: false,
    error: null,
    sessionExpired: false,
  });
  harness.loadCredentialEnvelope = async () => null;
  harness.clearAuthData = async () => {
    harness.clearCalls += 1;
    throw new Error('secure delete unavailable');
  };

  await assert.rejects(
    useAuthStore.getState().initialize(),
    /secure delete unavailable/,
  );

  assert.equal(useAuthStore.getState().user, null);
  assert.equal(useAuthStore.getState().tenantId, null);
  assert.equal(useAuthStore.getState().isAuthenticated, false);
  assert.equal(useAuthStore.getState().isInitialized, true);
  assert.equal(useAuthStore.getState().isLoading, false);
});

test('MFA enrollment clears mandatory in-memory state when credential cleanup fails', async () => {
  resetHarness();
  const user = testUser();
  useAuthStore.setState({
    user,
    tenantId: 'tenant-1',
    isAuthenticated: true,
    isLoading: false,
    isInitialized: true,
    error: null,
    sessionExpired: false,
    mfaEnrolledNotice: null,
  });
  harness.clearAuthData = async () => {
    harness.clearCalls += 1;
    throw new Error('secure delete unavailable');
  };

  await assert.rejects(
    useAuthStore.getState().endSessionAfterMfaEnrollment(),
    /secure delete unavailable/,
  );

  assert.equal(useAuthStore.getState().user, null);
  assert.equal(useAuthStore.getState().tenantId, null);
  assert.equal(useAuthStore.getState().isAuthenticated, false);
  assert.equal(useAuthStore.getState().isLoading, false);
  assert.deepEqual(useAuthStore.getState().mfaEnrolledNotice, {
    email: user.email,
    tenantId: 'tenant-1',
  });
});

test('an invalidated request with no envelope cannot expire a newer login', async () => {
  resetHarness();
  invalidateSession();
  useAuthStore.setState({
    user: null,
    tenantId: null,
    isAuthenticated: false,
    isLoading: false,
    isInitialized: true,
    error: null,
    sessionExpired: false,
  });
  setSessionExpiredHandler(() => useAuthStore.getState().handleSessionExpired());
  const readStarted = deferred();
  const releaseRead = deferred();
  harness.loadCredentialEnvelope = async () => {
    readStarted.resolve();
    await releaseRead.promise;
    return null;
  };
  const client = createApiClient('tenant-1');
  const request = client.get('/protected');
  await readStarted.promise;

  const newerUser = testUser();
  beginSession('refresh-new');
  harness.accessToken = 'access-new';
  harness.refreshToken = 'refresh-new';
  useAuthStore.setState({
    user: newerUser,
    tenantId: 'tenant-1',
    isAuthenticated: true,
    sessionExpired: false,
  });
  releaseRead.resolve();

  await assert.rejects(request, SessionExpiredError);
  assert.equal(useAuthStore.getState().isAuthenticated, true);
  assert.equal(useAuthStore.getState().user?.id, newerUser.id);
  assert.equal(useAuthStore.getState().sessionExpired, false);
});

test('logout does not deadlock when a protected teardown request finds no envelope', async () => {
  resetHarness();
  useAuthStore.setState({
    user: testUser(),
    tenantId: 'tenant-1',
    isAuthenticated: true,
    isLoading: false,
    isInitialized: true,
    error: null,
    sessionExpired: false,
  });
  harness.accessToken = 'access-a';
  harness.refreshToken = 'refresh-a';
  beginSession('refresh-a');
  harness.appValues.set('device-id', 'device-1');
  harness.loadCredentialEnvelope = async () => null;
  harness.unregister = async () => {
    const client = createApiClient('tenant-1');
    client.defaults.adapter = async (config: AxiosRequestConfig) => ({
      data: null,
      status: 204,
      statusText: 'No Content',
      headers: {},
      config,
    } as AxiosResponse);
    await client.delete('/devices/device-1');
  };

  const outcome = await Promise.race([
    useAuthStore.getState().logout().then(() => 'completed'),
    new Promise<'timed-out'>((resolve) => setTimeout(() => resolve('timed-out'), 100)),
  ]);

  assert.equal(outcome, 'completed');
  assert.equal(useAuthStore.getState().isAuthenticated, false);
  assert.equal(useAuthStore.getState().isLoading, false);
});
