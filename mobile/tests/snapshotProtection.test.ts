import assert from 'node:assert/strict';
import test from 'node:test';
import * as protection from '../src/security/snapshotProtection.ts';

const settle = () => new Promise<void>((resolve) => setImmediate(resolve));

test('closing MFA does not remove the authenticated root snapshot protection', async () => {
  const calls: string[] = [];
  const coordinator = protection.createSnapshotProtection({
    enable: async () => { calls.push('enable'); },
    disable: async () => { calls.push('disable'); },
  });
  const releaseRoot = coordinator.acquire();
  const releaseMfa = coordinator.acquire();
  await settle();
  releaseMfa();
  releaseMfa();
  await settle();
  assert.deepEqual(calls, ['enable']);
  releaseRoot();
  await settle();
  assert.deepEqual(calls, ['enable', 'disable']);
});

test('a delayed enable followed by unmount is disabled only after enable completes', async () => {
  const calls: string[] = [];
  let complete!: () => void;
  const pending = new Promise<void>((resolve) => { complete = resolve; });
  const coordinator = protection.createSnapshotProtection({
    enable: async () => { calls.push('enable'); await pending; },
    disable: async () => { calls.push('disable'); },
  });
  const release = coordinator.acquire();
  await settle();
  release();
  await settle();
  assert.deepEqual(calls, ['enable']);
  complete();
  await settle();
  assert.deepEqual(calls, ['enable', 'disable']);
});

test('reacquiring during cleanup keeps the next sensitive screen protected', async () => {
  let enabled = false;
  const coordinator = protection.createSnapshotProtection({
    enable: async () => { enabled = true; },
    disable: async () => { enabled = false; },
  });
  const releasePayroll = coordinator.acquire();
  await settle();
  releasePayroll();
  const releaseDocuments = coordinator.acquire();
  await settle();
  assert.equal(enabled, true);
  releaseDocuments();
  await settle();
  assert.equal(enabled, false);
});

test('iOS payroll and document leases prevent foreground capture independently of the root blur', async () => {
  const calls: string[] = [];
  const api = {
    enableAppSwitcherProtectionAsync: async () => { calls.push('blur-on'); },
    disableAppSwitcherProtectionAsync: async () => { calls.push('blur-off'); },
    preventScreenCaptureAsync: async (key: string) => { calls.push(`capture-on:${key}`); },
    allowScreenCaptureAsync: async (key: string) => { calls.push(`capture-off:${key}`); },
  };
  const coordinator = protection.createScreenProtection(api, 'ios');
  const root = coordinator.acquireSnapshot();
  const payroll = coordinator.acquireSensitive();
  const mfa = coordinator.acquireSensitive();
  await settle();
  assert.deepEqual(calls.sort(), ['blur-on', 'capture-on:sensitive-content']);
  mfa();
  await settle();
  assert.equal(calls.length, 2, 'MFA cannot disable payroll capture prevention');
  const documents = coordinator.acquireSensitive();
  payroll();
  await settle();
  assert.equal(calls.length, 2);
  documents();
  await settle();
  assert.equal(calls.at(-1), 'capture-off:sensitive-content');
  assert.equal(calls.includes('blur-off'), false, 'authenticated root still owns the snapshot');
  root();
  await settle();
  assert.equal(calls.at(-1), 'blur-off');
});
