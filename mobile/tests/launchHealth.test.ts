import test from 'node:test';
import assert from 'node:assert/strict';
import { beginLaunch, markHealthy, parseLaunchRecord, LaunchFailureLatch, CRASH_LOOP_THRESHOLD } from '../src/updates/launchHealth.ts';

test('a first launch of an update is clean', () => {
  const { record, crashLoop } = beginLaunch(null, 'u1');
  assert.deepEqual(record, { updateId: 'u1', failedLaunches: 0, healthy: false });
  assert.equal(crashLoop, false);
});

test('relaunching an update that never became healthy counts up and trips the crash-loop flag', () => {
  let d = beginLaunch(null, 'u1');
  for (let i = 1; i <= CRASH_LOOP_THRESHOLD; i++) {
    d = beginLaunch(d.record, 'u1');
    assert.equal(d.record.failedLaunches, i);
  }
  assert.equal(d.crashLoop, true);
});

test('a healthy launch resets the count, and a new update id starts fresh', () => {
  let d = beginLaunch(null, 'u1');
  d = beginLaunch(d.record, 'u1');
  d = beginLaunch(markHealthy(d.record), 'u1');
  assert.equal(d.record.failedLaunches, 0);
  const next = beginLaunch({ updateId: 'u1', failedLaunches: 5, healthy: false }, 'u2');
  assert.equal(next.record.failedLaunches, 0);
  assert.equal(next.crashLoop, false);
});

test('the embedded bundle never reports a crash loop (nothing to roll back to)', () => {
  const d = beginLaunch({ updateId: 'embedded', failedLaunches: 9, healthy: false }, null);
  assert.equal(d.record.updateId, 'embedded');
  assert.equal(d.crashLoop, false);
});

test('junk in storage reads as no record', () => {
  assert.equal(parseLaunchRecord('x'), null);
  assert.equal(parseLaunchRecord({ updateId: 1 }), null);
  assert.deepEqual(parseLaunchRecord({ updateId: 'a', failedLaunches: -3, healthy: false }), { updateId: 'a', failedLaunches: 0, healthy: false });
});


test('a render failure before or during startup bookkeeping remains latched', async () => {
  const beforeStartup = new LaunchFailureLatch();
  beforeStartup.noteFailure();
  await Promise.resolve(); // startup's asynchronous storage read must not clear this flag
  assert.equal(beforeStartup.hasFailed, true);
  const duringStartup = new LaunchFailureLatch();
  await Promise.resolve();
  duringStartup.noteFailure();
  assert.equal(duringStartup.hasFailed, true);
  // Failed OTA launches must stay unhealthy and count against the next launch.
  const pending = beginLaunch(null, 'u1').record;
  const stillPending = duringStartup.hasFailed ? pending : markHealthy(pending);
  assert.equal(stillPending.healthy, false);
  assert.equal(beginLaunch(stillPending, 'u1').record.failedLaunches, 1);
});
