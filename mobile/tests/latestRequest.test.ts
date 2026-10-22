import assert from 'node:assert/strict';
import test from 'node:test';
import { createLatestRequestGate } from '../src/api/latestRequest.ts';

test('only the newest request remains eligible to update state', () => {
  const gate = createLatestRequestGate();
  const first = gate.begin();
  const second = gate.begin();
  assert.equal(gate.isLatest(first), false);
  assert.equal(gate.isLatest(second), true);
});

test('invalidate prevents an in-flight request from updating state', () => {
  const gate = createLatestRequestGate();
  const request = gate.begin();
  gate.invalidate();
  assert.equal(gate.isLatest(request), false);
});
