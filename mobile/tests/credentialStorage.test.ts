import test from 'node:test';
import assert from 'node:assert/strict';
import { registerHooks } from 'node:module';

function moduleUrl(source: string): string {
  return `data:text/javascript,${encodeURIComponent(source)}`;
}

const mockModules = new Map<string, string>([
  ['expo-secure-store', moduleUrl(`
    const harness = () => globalThis.__credentialStorageHarness;
    export const WHEN_UNLOCKED_THIS_DEVICE_ONLY = 'device-only';
    export async function setItemAsync(key, value, options) {
      return harness().setSecureItem(key, value, options);
    }
    export async function getItemAsync(key) {
      return harness().secureItems.get(key) ?? null;
    }
    export async function deleteItemAsync(key) {
      return harness().deleteSecureItem(key);
    }
  `)],
  ['@react-native-async-storage/async-storage', moduleUrl(`
    const harness = () => globalThis.__credentialStorageHarness;
    export default {
      async setItem(key, value) { harness().appItems.set(key, value); },
      async getItem(key) { return harness().appItems.get(key) ?? null; },
      async removeItem(key) {
        harness().appDeletes.push(key);
        harness().appItems.delete(key);
      },
    };
  `)],
  ['@/config', moduleUrl(`
    export const STORAGE_KEYS = {
      ACCESS_TOKEN: 'legacy-access',
      REFRESH_TOKEN: 'legacy-refresh',
      TOKEN_EXPIRY: 'legacy-expiry',
      USER: 'cached-user',
      OFFLINE_PUNCHES: 'offline-punches',
    };
  `)],
]);

registerHooks({
  resolve(specifier, context, nextResolve) {
    const mock = mockModules.get(specifier);
    if (mock) return { url: mock, shortCircuit: true };
    return nextResolve(specifier, context);
  },
});

const storage = await import('../src/storage/index.ts');
const {
  CREDENTIAL_ENVELOPE_KEY,
  USER_CACHE_TTL_MS,
  saveCredentialEnvelope,
  loadCredentialEnvelope,
  clearCredentialEnvelope,
  userStorage,
} = storage;

interface Harness {
  secureItems: Map<string, string>;
  appItems: Map<string, string>;
  secureDeletes: string[];
  appDeletes: string[];
  secureWrites: { key: string; value: string; options: unknown }[];
  setSecureItem: (key: string, value: string, options: unknown) => Promise<void>;
  deleteSecureItem: (key: string) => Promise<void>;
}

const harness: Harness = {
  secureItems: new Map(),
  appItems: new Map(),
  secureDeletes: [],
  appDeletes: [],
  secureWrites: [],
  setSecureItem: async (key, value, options) => {
    harness.secureWrites.push({ key, value, options });
    harness.secureItems.set(key, value);
  },
  deleteSecureItem: async (key) => {
    harness.secureDeletes.push(key);
    harness.secureItems.delete(key);
  },
};
(globalThis as typeof globalThis & { __credentialStorageHarness: Harness })
  .__credentialStorageHarness = harness;

function resetHarness(): void {
  harness.secureItems.clear();
  harness.appItems.clear();
  harness.secureDeletes.length = 0;
  harness.appDeletes.length = 0;
  harness.secureWrites.length = 0;
  harness.setSecureItem = async (key, value, options) => {
    harness.secureWrites.push({ key, value, options });
    harness.secureItems.set(key, value);
  };
  harness.deleteSecureItem = async (key) => {
    harness.secureDeletes.push(key);
    harness.secureItems.delete(key);
  };
}

const envelope = {
  version: 1 as const,
  accessToken: 'access-token',
  refreshToken: 'refresh-token',
  expiresAt: 1_800_000_000_000,
  tenantId: 'tenant-one',
  persistedAt: 1_700_000_000_000,
};

test('credential envelope round-trips through one atomic secure write', async () => {
  resetHarness();

  await saveCredentialEnvelope(envelope);

  assert.equal(harness.secureWrites.length, 1);
  assert.equal(harness.secureWrites[0]?.key, CREDENTIAL_ENVELOPE_KEY);
  assert.deepEqual(JSON.parse(harness.secureWrites[0]?.value ?? ''), envelope);
  assert.deepEqual(await loadCredentialEnvelope(), envelope);
});

test('malformed and old credential envelopes fail closed', async () => {
  for (const raw of [
    '{not-json',
    JSON.stringify({ ...envelope, version: 0 }),
    JSON.stringify({ ...envelope, refreshToken: '' }),
    JSON.stringify({ ...envelope, expiresAt: 'tomorrow' }),
  ]) {
    resetHarness();
    harness.secureItems.set(CREDENTIAL_ENVELOPE_KEY, raw);

    assert.equal(await loadCredentialEnvelope(), null);
    assert.equal(harness.secureItems.has(CREDENTIAL_ENVELOPE_KEY), false);
  }
});

test('credential write failure rejects and does not report success', async () => {
  resetHarness();
  harness.secureItems.set(CREDENTIAL_ENVELOPE_KEY, JSON.stringify({
    ...envelope,
    accessToken: 'previous-access-token',
  }));
  harness.setSecureItem = async () => {
    throw new Error('secure storage unavailable');
  };

  await assert.rejects(
    saveCredentialEnvelope(envelope),
    /secure storage unavailable/,
  );
  assert.equal(
    JSON.parse(harness.secureItems.get(CREDENTIAL_ENVELOPE_KEY) ?? '').accessToken,
    'previous-access-token',
  );
});

test('legacy cleanup failure prevents publishing a new credential envelope', async () => {
  resetHarness();
  harness.deleteSecureItem = async (key) => {
    if (key === 'legacy-refresh') throw new Error('secure delete unavailable');
    harness.secureDeletes.push(key);
    harness.secureItems.delete(key);
  };

  await assert.rejects(
    saveCredentialEnvelope(envelope),
    /secure delete unavailable/,
  );
  assert.equal(harness.secureWrites.length, 0);
  assert.equal(harness.secureItems.has(CREDENTIAL_ENVELOPE_KEY), false);
});

test('loading without an envelope removes unusable legacy token fragments', async () => {
  resetHarness();
  harness.secureItems.set('legacy-access', 'old-access');
  harness.secureItems.set('legacy-refresh', 'old-refresh');
  harness.secureItems.set('legacy-expiry', '1800000000000');

  assert.equal(await loadCredentialEnvelope(), null);
  assert.equal(harness.secureItems.has('legacy-access'), false);
  assert.equal(harness.secureItems.has('legacy-refresh'), false);
  assert.equal(harness.secureItems.has('legacy-expiry'), false);
});

test('clearing credentials removes the envelope and every legacy token key', async () => {
  resetHarness();
  harness.secureItems.set(CREDENTIAL_ENVELOPE_KEY, JSON.stringify(envelope));
  harness.secureItems.set('legacy-access', 'old-access');
  harness.secureItems.set('legacy-refresh', 'old-refresh');
  harness.secureItems.set('legacy-expiry', '1800000000000');

  await clearCredentialEnvelope();

  assert.deepEqual(
    [...harness.secureDeletes].sort(),
    [CREDENTIAL_ENVELOPE_KEY, 'legacy-access', 'legacy-expiry', 'legacy-refresh'].sort(),
  );
});

test('cached user data excludes authorization fields and expires by TTL', async () => {
  resetHarness();
  const realNow = Date.now;
  Date.now = () => 10_000;
  try {
    await userStorage.saveUser({
      id: 'user-1',
      tenantId: 'tenant-one',
      employeeId: 'employee-1',
      username: 'person@example.com',
      email: 'person@example.com',
      fullName: 'Person One',
      role: 'SUPER_ADMIN',
      accessMode: 'FullPortal',
      permissions: [{ module: 'payroll', actions: ['approve'] }],
      department: 'Finance',
      jobTitle: 'Approver',
      isFirstLogin: false,
      isActive: true,
      mustChangePassword: false,
    });

    const stored = JSON.parse(harness.appItems.get('cached-user') ?? '');
    assert.deepEqual(stored, {
      version: 1,
      cachedAt: 10_000,
      user: {
        id: 'user-1',
        employeeId: 'employee-1',
        email: 'person@example.com',
        fullName: 'Person One',
      },
    });
    assert.deepEqual(await userStorage.getUser(), stored.user);

    Date.now = () => 10_000 + USER_CACHE_TTL_MS + 1;
    assert.equal(await userStorage.getUser(), null);
    assert.equal(harness.appItems.has('cached-user'), false);
  } finally {
    Date.now = realNow;
  }
});
