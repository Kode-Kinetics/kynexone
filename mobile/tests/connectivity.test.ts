import test from 'node:test';
import assert from 'node:assert/strict';
import { isOfflineState, useConnectivityStore } from '../src/net/connectivity.ts';

test('offline means no connection, or a connection with no internet', () => {
  assert.equal(isOfflineState({ isConnected: false, isInternetReachable: false }), true);
  assert.equal(isOfflineState({ isConnected: true, isInternetReachable: false }), true);
  assert.equal(isOfflineState({ isConnected: true, isInternetReachable: true }), false);
  assert.equal(isOfflineState({ isConnected: true, isInternetReachable: null }), false); // still probing: do not flash a banner
  assert.equal(isOfflineState(null), false);
});

test('the store holds the flag', () => {
  useConnectivityStore.getState().setOffline(true);
  assert.equal(useConnectivityStore.getState().offline, true);
  useConnectivityStore.getState().setOffline(false);
});
