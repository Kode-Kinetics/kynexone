import test from 'node:test';
import assert from 'node:assert/strict';
import {
  beginSession,
  captureSession,
  invalidateSession,
  mayCommit,
} from '../src/auth/sessionGeneration.ts';

test('same-generation refresh may commit while its refresh token is current', () => {
  beginSession('refresh-a');
  const snapshot = captureSession();

  assert.equal(mayCommit(snapshot, 'refresh-a'), true);
});

test('invalidating a session prevents its in-flight refresh from committing', () => {
  beginSession('refresh-a');
  const snapshot = captureSession();

  invalidateSession();

  assert.equal(mayCommit(snapshot, null), false);
  assert.equal(mayCommit(snapshot, 'refresh-a'), false);
});

test('a newer login prevents the prior session refresh from committing', () => {
  beginSession('refresh-a');
  const priorSession = captureSession();

  const currentSession = beginSession('refresh-b');

  assert.equal(mayCommit(priorSession, 'refresh-b'), false);
  assert.equal(mayCommit(currentSession, 'refresh-b'), true);
});

test('a rotated or missing stored refresh token prevents a commit', () => {
  const snapshot = beginSession('refresh-a');

  assert.equal(mayCommit(snapshot, 'refresh-b'), false);
  assert.equal(mayCommit(snapshot, null), false);
});
