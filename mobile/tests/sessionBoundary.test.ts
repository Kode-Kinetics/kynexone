import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { runInNewContext } from 'node:vm';
import { setImmediate as tick } from 'node:timers/promises';
import ts from 'typescript';

// Execute the shipped store/client/navigator logic with only native I/O, networking,
// React rendering and Zustand's container substituted. This is not an on-device test.
const sourceRoot = resolve(dirname(fileURLToPath(import.meta.url)), '../src');
function sourceLoader(stubs: Record<string, unknown>) {
  const cache = new Map<string, { exports: any }>();
  const load = (file: string): any => {
    let path = resolve(sourceRoot, file);
    if (!existsSync(path)) path += existsSync(path + '.ts') ? '.ts' : '.tsx';
    if (cache.has(path)) return cache.get(path)!.exports;
    const module = { exports: {} as any };
    cache.set(path, module);
    const code = ts.transpileModule(readFileSync(path, 'utf8'), {
      fileName: path,
      compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.React, esModuleInterop: true },
    }).outputText;
    const require = (id: string) => {
      if (Object.hasOwn(stubs, id)) return stubs[id];
      if (id.startsWith('@/')) return load(id.slice(2));
      if (id.startsWith('.')) return load(resolve(dirname(path), id));
      throw new Error(`Unexpected dependency: ${id}`);
    };
    runInNewContext(code, { module, exports: module.exports, require, console, Promise, Error, Date, setTimeout, clearTimeout, __DEV__: false }, { filename: path });
    return module.exports;
  };
  return load;
}
function deferred<T = any>() {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
function createStore(init: any) {
  let state: any;
  const listeners = new Set<(state: any) => void>();
  const set = (patch: any) => { state = { ...state, ...(typeof patch === 'function' ? patch(state) : patch) }; listeners.forEach(fn => fn(state)); };
  const get = () => state;
  const store: any = (selector?: any) => selector ? selector(state) : state;
  store.getState = get;
  store.setState = set;
  store.subscribe = (fn: any) => { listeners.add(fn); return () => listeners.delete(fn); };
  state = init(set, get);
  return store;
}
class ShapeError extends Error { route = '/auth/me'; issues = ['shape']; }
const oldUser = { id: 'old', email: 'old@example.test', tenantSlug: 'old-company' };
const newUser = { id: 'new', email: 'new@example.test', tenantSlug: 'new-company' };
function authWorld(options: { realClient?: boolean; refresh?: boolean; malformed?: boolean; slowSave?: boolean; slowLogout?: boolean; loginResult?: 'failure' | 'mfa' } = {}) {
  const me = deferred();
  const requested = deferred<void>();
  const saveStarted = deferred<void>();
  const saveRelease = deferred<void>();
  const logoutStarted = deferred<void>();
  const logoutRelease = deferred<void>();
  let meRequests = 0;
  let savedUser: any = oldUser;
  let savedTokens: any = { accessToken: 'old-access', refreshToken: 'old-refresh' };
  let tenant: any = 'old-company';
  let blocking = false;
  let userSaves = 0;
  let tokenSaves = 0;
  const storage = {
    userStorage: { getUser: async () => savedUser, saveUser: async (user: any) => { userSaves++; if (options.slowSave) { saveStarted.resolve(); await saveRelease.promise; } savedUser = user; } },
    tokenStorage: { isTokenExpired: async () => false, getRefreshToken: async () => savedTokens?.refreshToken, getAccessToken: async () => savedTokens?.accessToken,
      saveTokens: async (tokens: any) => { tokenSaves++; savedTokens = tokens; } },
    appStorage: { get: async (key: string) => key === 'zayra_tenant_id' ? tenant : null, set: async (key: string, value: any) => { if (key === 'zayra_tenant_id') tenant = value; } },
    clearAllAuthData: async () => { savedUser = null; savedTokens = null; tenant = null; },
  };
  const compatibility = { getState: () => ({ reset: () => { blocking = false; }, flag: () => { blocking = true; } }) };
  const makeClient = (publicClient = false) => {
    const requests: any[] = [], responses: any[] = [];
    const client: any = (config: any) => {
      let chain: Promise<any> = Promise.resolve({ ...config, headers: { ...config.headers } });
      for (const [yes, no] of requests) chain = chain.then(yes, no);
      chain = chain.then(async cfg => {
        if (!publicClient && options.refresh && !cfg._retry) throw { response: { status: 401 }, config: cfg };
        requested.resolve();
        const data = await me.promise;
        return { data, status: 200, config: cfg };
      });
      for (const [yes, no] of responses) chain = chain.then(yes, no);
      return chain;
    };
    client.interceptors = { request: { use: (yes: any, no: any) => requests.push([yes, no]) }, response: { use: (yes: any, no: any) => responses.push([yes, no]) } };
    for (const method of ['get', 'post', 'put', 'patch', 'delete']) client[method] = (url: string, data?: any, config?: any) => client({ ...(config ?? (method === 'get' ? data : {})), url, method });
    return client;
  };
  let api: any;
  const services = { authApi: {
    login: async () => { if (options.loginResult === 'mfa') return { kind: 'mfa-required' }; throw new Error('bad credentials'); },
    getMe: async () => { meRequests++; if (options.realClient) return api.apiGet('/auth/me'); requested.resolve(); return me.promise; },
    logout: async () => { logoutStarted.resolve(); if (options.slowLogout) await logoutRelease.promise; },
    getMfaStatus: async () => null,
  }, deviceApi: { register: async () => undefined, unregister: async () => undefined } };
  const stubs: Record<string, unknown> = {
    zustand: { create: createStore }, 'expo-device': {}, 'expo-constants': { expoConfig: {} }, 'react-native': { Platform: { OS: 'ios' } },
    '@/observability/reportError': { setErrorTrackingTenant: () => {}, reportError: () => {} },
    '@/safety/compatibilityStore': { useCompatibilityStore: compatibility }, '@/storage': storage,
    '@/config': { STORAGE_KEYS: { DEVICE_ID: 'device' }, APP_CONFIG: { API_BASE_URL: 'https://api.example.test', TENANT_HEADER: 'X-Tenant' }, APP_VERSION: '1', APP_VERSION_HEADER: 'X-Version', API_TIMEOUT_MS: 100 },
    '@/api/services': services,
    '@/api/sessionErrors': { isDefinitiveAuthRefusal: (e: any) => e?.response?.status === 401 },
    '@/api/schemas': { ResponseShapeError: ShapeError },
    '@/utils/device': { generateDeviceId: async () => 'device' }, '@/features/notifications/pushNotifications': { registerPushToken: async () => {} },
    './accessPolicy': { deriveMobileAccess: () => ({ mode: 'Employee' }), hasEffectivePermission: () => true },
    './publicAuthInput': { normalizeEmail: (s: string) => s, normalizeWorkspace: (s: string) => s ?? '', requireWorkspace: (s: string) => s },
    './welcomeCode': { isWelcomeCode: () => false },
    axios: { create: () => makeClient(), isAxiosError: () => true },
    '@/config/i18n': { t: (key: string) => key },
    './publicAuthClient': { createIsolatedPublicAuthClient: () => makeClient(true) },
    './schemas': { ResponseShapeError: ShapeError, validateRefreshResponse: (value: any) => { if (options.malformed) throw new ShapeError(); return value; }, validateResponse: (_m: any, _u: any, value: any) => { if (options.malformed) throw new ShapeError(); return value; } },
    './sessionErrors': { NoRefreshTokenError: class extends Error {}, isDefinitiveAuthRefusal: (e: any) => e?.response?.status === 401,
      localizedErrorKey: () => null, retryTransient: (fn: any) => fn(), shouldRetryGet: () => false, backoffDelayMs: () => 0 },
  };
  if (!options.realClient) stubs['@/api/client'] = { initApiClient: () => {}, setSessionExpiredHandler: () => {} };
  const load = sourceLoader(stubs);
  if (options.realClient) api = load('api/client.ts');
  const store = load('auth/authStore.ts').useAuthStore;
  store.setState({ isInitialized: true, connectionIssue: true });
  return { store, me, requested, saveStarted, saveRelease, logoutStarted, logoutRelease, storage, inspect: () => ({ savedUser, savedTokens, tenant, blocking, userSaves, tokenSaves, meRequests }) };
}

for (const result of ['success', 'transient-error'] as const) {
  test(`sign-out ignores a delayed recovery ${result}`, async () => {
    const w = authWorld();
    const pending = w.store.getState().initialize();
    await w.requested.promise;
    await w.store.getState().logout();
    if (result === 'success') w.me.resolve(oldUser); else w.me.reject(new Error('offline'));
    await pending;
    assert.equal(w.store.getState().isAuthenticated, false);
    assert.equal(w.store.getState().connectionIssue, false);
    assert.equal(w.inspect().savedUser, null);
    assert.equal(w.inspect().savedTokens, null);
  });
}

test('sign-out finishes storage cleanup after an already-started recovery save', async () => {
  const w = authWorld({ slowSave: true });
  const pending = w.store.getState().initialize();
  await w.requested.promise;
  w.me.resolve(oldUser);
  await w.saveStarted.promise;
  const logout = w.store.getState().logout();
  await tick();
  w.saveRelease.resolve();
  await Promise.all([pending, logout]);
  assert.equal(w.store.getState().isAuthenticated, false);
  assert.equal(w.inspect().savedUser, null);
});

test('an account switch invalidates the preceding recovery response', async () => {
  const w = authWorld();
  const pending = w.store.getState().initialize();
  await w.requested.promise;
  await w.store.getState().finishLogin(newUser, { accessToken: 'new', refreshToken: 'new' }, 'new-company');
  w.me.resolve(oldUser);
  await pending;
  assert.equal(w.store.getState().user.id, 'new');
  assert.equal(w.inspect().savedUser.id, 'new');
});

test('session expiry invalidates a pending recovery', async () => {
  const w = authWorld();
  const pending = w.store.getState().initialize();
  await w.requested.promise;
  w.store.getState().handleSessionExpired();
  await tick();
  w.me.resolve(oldUser);
  await pending;
  assert.equal(w.store.getState().isAuthenticated, false);
  assert.equal(w.store.getState().sessionExpired, true);
  assert.equal(w.inspect().savedUser, null);
});

for (const malformed of [false, true]) {
  test(`sign-out rejects a delayed ${malformed ? 'malformed' : 'valid'} token refresh without persisting or blocking`, async () => {
    const w = authWorld({ realClient: true, refresh: true, malformed });
    const pending = w.store.getState().initialize();
    await w.requested.promise;
    await w.store.getState().logout();
    w.me.resolve({ accessToken: 'late', refreshToken: 'late', expiresAtUtc: '2030-01-01T00:00:00Z' });
    await pending;
    assert.equal(w.inspect().tokenSaves, 0);
    assert.equal(w.inspect().savedTokens, null);
    assert.equal(w.inspect().blocking, false);
    assert.equal(w.store.getState().connectionIssue, false);
    assert.equal(w.store.getState().isAuthenticated, false);
  });
}

test('a malformed delayed /auth/me cannot restore the compatibility block after sign-out', async () => {
  const w = authWorld({ realClient: true, malformed: true });
  const pending = w.store.getState().initialize();
  await w.requested.promise;
  await w.store.getState().logout();
  w.me.resolve({});
  await pending;
  assert.equal(w.inspect().blocking, false);
  assert.equal(w.store.getState().connectionIssue, false);
});

test('ordinary recovery still persists the fresh authoritative user', async () => {
  const w = authWorld();
  const pending = w.store.getState().initialize();
  await w.requested.promise;
  w.me.resolve(oldUser);
  await pending;
  assert.equal(w.store.getState().isAuthenticated, true);
  assert.equal(w.inspect().savedUser.id, 'old');
});

test('an ordinary transient recovery failure retains credentials and the recovery screen', async () => {
  const w = authWorld();
  const pending = w.store.getState().initialize();
  await w.requested.promise;
  w.me.reject(new Error('offline'));
  await pending;
  assert.equal(w.store.getState().connectionIssue, true);
  assert.equal(w.inspect().savedTokens.refreshToken, 'old-refresh');
});

function navigatorWorld() {
  let locked = false;
  let initialLockEvaluated = true;
  let signedIn = true;
  const auth = () => ({ isAuthenticated: signedIn, isInitialized: true, initialize: () => {}, connectionIssue: false });
  const React = { createElement: (type: any, props: any, ...children: any[]) => ({ type, props, children }), useEffect: () => {}, useMemo: (fn: any) => fn() };
  const stubs: Record<string, unknown> = { react: React,
    'react-native': { View: 'View', Text: 'Text', ActivityIndicator: 'ActivityIndicator', StyleSheet: { create: (value: any) => value } },
    '@react-navigation/native': { NavigationContainer: 'NavigationContainer' }, 'react-i18next': { useTranslation: () => ({ t: (s: string) => s }) },
    '@/auth/authStore': { useAuthStore: auth }, '@/safety/appLockStore': { useAppLockStore: (select: any) => select({ locked, initialLockEvaluated }) },
    '@/safety/compatibilityStore': { useCompatibilityStore: (select: any) => select({ blocking: false }) },
    '@/theme/ThemeProvider': { useTheme: () => ({ theme: { isDark: false, colors: {}, radius: {}, typography: {} } }) },
    './AuthStack': { AuthStack: 'AuthStack' }, './MainTabs': { MainTabs: 'MainTabs' }, './routes': { navigationRef: {} },
    '@/components/ui': { LiquidBackdrop: 'LiquidBackdrop', Surface: 'Surface' },
  };
  for (const [path, names] of [['auth/MfaSetupBanner', ['MfaSetupBanner']], ['auth/ResetCodeBanner', ['ResetCodeBanner']], ['safety/ConnectionIssueScreen', ['ConnectionIssueScreen']], ['safety/UpdateRequired', ['UpdateRequiredBanner', 'UpdateRequiredScreen']]] as const)
    stubs['@/features/' + path] = Object.fromEntries(names.map(name => [name, name]));
  const root = sourceLoader(stubs)('navigation/RootNavigator.tsx').RootNavigator;
  return { render: root, lock: (value: boolean) => { locked = value; }, evaluated: (value: boolean) => { initialLockEvaluated = value; }, signOut: () => { signedIn = false; } };
}
function contains(node: any, type: string): boolean {
  return !!node && (node.type === type || (node.children ?? []).some((child: any) => contains(child, type)));
}
test('locking removes the authenticated navigation tree, including its native modal windows', () => {
  const w = navigatorWorld();
  assert.equal(contains(w.render(), 'MainTabs'), true);
  w.lock(true);
  const hidden = w.render();
  assert.equal(contains(hidden, 'NavigationContainer'), false);
  assert.equal(contains(hidden, 'MainTabs'), false);
  assert.equal(contains(hidden, 'MfaSetupBanner'), false);
  // Cancellation leaves locked=true: protected screens stay unmounted.
  assert.equal(contains(w.render(), 'MainTabs'), false);
  w.lock(false);
  assert.equal(contains(w.render(), 'MainTabs'), true);
});
test('restored private content stays hidden until initial lock hydration and decision finish', () => {
  const w = navigatorWorld();
  w.evaluated(false); // auth restored before slow device biometrics/preferences hydration
  assert.equal(contains(w.render(), 'MainTabs'), false);
  assert.equal(contains(w.render(), 'NavigationContainer'), false);
  // Once hydration completes, AppLockGate applies the lock BEFORE publishing a decision.
  w.lock(true);
  w.evaluated(true);
  assert.equal(contains(w.render(), 'MainTabs'), false);
  // Canceling biometric prompt must never open the private tree.
  assert.equal(contains(w.render(), 'MainTabs'), false);
  w.lock(false);
  assert.equal(contains(w.render(), 'MainTabs'), true);
});

test('a lock from an earlier session does not suppress the sign-in navigator', () => {
  const w = navigatorWorld();
  w.lock(true); w.evaluated(false); w.signOut();
  assert.equal(contains(w.render(), 'AuthStack'), true);
});


test('a retry queued during sign-out cannot start another session restoration', async () => {
  const w = authWorld({ slowLogout: true });
  const logout = w.store.getState().logout();
  await w.logoutStarted.promise;
  const retry = w.store.getState().initialize();
  await tick();
  const requestsDuringLogout = w.inspect().meRequests;
  w.me.resolve(oldUser);
  w.logoutRelease.resolve();
  await Promise.all([retry, logout]);
  assert.equal(requestsDuringLogout, 0);
  assert.equal(w.store.getState().isAuthenticated, false);
});

for (const loginResult of ['failure', 'mfa'] as const) {
  test(`pending sign-out still clears old credentials after a ${loginResult} login attempt`, async () => {
    const w = authWorld({ slowLogout: true, loginResult });
    const logout = w.store.getState().logout();
    await w.logoutStarted.promise;
    const attempted = w.store.getState().login('new@example.test', 'wrong', 'new-company');
    if (loginResult === 'failure') await assert.rejects(attempted, /bad credentials/); else await attempted;
    w.logoutRelease.resolve();
    await logout;
    assert.equal(w.inspect().savedUser, null);
    assert.equal(w.inspect().savedTokens, null);
    assert.equal(w.inspect().tenant, null);
    assert.equal(w.store.getState().isAuthenticated, false);
  });
}

test('old sign-out does not clear a successfully persisted replacement session', async () => {
  const w = authWorld({ slowLogout: true });
  const logout = w.store.getState().logout();
  await w.logoutStarted.promise;
  await w.store.getState().finishLogin(newUser, { accessToken: 'new', refreshToken: 'new' }, 'new-company');
  w.logoutRelease.resolve();
  await logout;
  assert.equal(w.inspect().savedUser.id, 'new');
  assert.equal(w.inspect().savedTokens.accessToken, 'new');
  assert.equal(w.store.getState().user.id, 'new');
  assert.equal(w.store.getState().isAuthenticated, true);
});

test('sign-out clears a replacement write cancelled while its storage operation was in flight', async () => {
  const w = authWorld({ slowSave: true, slowLogout: true });
  const login = w.store.getState().finishLogin(newUser, { accessToken: 'new', refreshToken: 'new' }, 'new-company');
  await w.saveStarted.promise;
  const logout = w.store.getState().logout();
  await w.logoutStarted.promise;
  w.saveRelease.resolve();
  await login;
  w.logoutRelease.resolve();
  await logout;
  assert.equal(w.inspect().savedUser, null);
  assert.equal(w.inspect().savedTokens, null);
  assert.equal(w.store.getState().isAuthenticated, false);
});

test('an unsuccessful login cannot cancel session-expiry cleanup queued just before it', async () => {
  const w = authWorld();
  w.store.getState().handleSessionExpired();
  await assert.rejects(w.store.getState().login('new@example.test', 'wrong', 'new-company'), /bad credentials/);
  await tick();
  assert.equal(w.inspect().savedUser, null);
  assert.equal(w.inspect().savedTokens, null);
});

test('overlapping sign-out requests both settle without leaving the app busy', async () => {
  const w = authWorld({ slowLogout: true });
  const first = w.store.getState().logout();
  await w.logoutStarted.promise;
  const second = w.store.getState().logout();
  await tick();
  w.logoutRelease.resolve();
  await Promise.all([first, second]);
  assert.equal(w.inspect().savedTokens, null);
  assert.equal(w.store.getState().isAuthenticated, false);
  assert.equal(w.store.getState().isLoading, false);
});

test('sensitive share presenter fences lock, background, sign-out, account switch and screen exit', async () => {
  let generation = 3;
  let locked = false;
  let signedIn = true;
  let foreground = 'active';
  let onScreen = true;
  let launched = 0;
  const gate = sourceLoader({
    'react-native': { AppState: { get currentState() { return foreground; } } },
    'expo-sharing': { shareAsync: async () => { launched++; } },
    '@/auth/authStore': { useAuthStore: { getState: () => ({ isAuthenticated: signedIn }) } },
    '@/safety/appLockStore': { useAppLockStore: { getState: () => ({ locked }) } },
    '@/auth/sessionGeneration': {
      currentSessionGeneration: () => generation,
      isCurrentSession: (expected: number) => expected === generation,
    },
  })('safety/documentShareGate.ts');
  const captured = gate.currentShareGeneration();
  const share = () => gate.shareProtectedDocument('file:///salary.pdf', { mimeType: 'application/pdf' }, captured, () => onScreen);
  assert.equal(await share(), true);
  assert.equal(launched, 1);
  locked = true;
  assert.equal(await share(), false);
  locked = false;
  foreground = 'background';
  assert.equal(await share(), false);
  foreground = 'active';
  onScreen = false;
  assert.equal(await share(), false);
  onScreen = true;
  generation++;
  assert.equal(await share(), false);
  generation--;
  signedIn = false;
  assert.equal(await share(), false);
  assert.equal(launched, 1, 'no new native sheet may open after the state changes');
});
