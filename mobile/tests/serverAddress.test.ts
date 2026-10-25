import test from 'node:test';
import assert from 'node:assert/strict';
import {
  apiBaseUrlFor,
  isInstallationInfo,
  normalizeServerAddress,
  parseServerQr,
  serverQrPayload,
} from '../src/config/serverAddress.ts';

test('a typed address becomes the server origin', () => {
  assert.deepEqual(normalizeServerAddress('hr.example.sa'), { ok: true, origin: 'https://hr.example.sa' });
  assert.deepEqual(normalizeServerAddress('  https://hr.example.sa/api/  '), { ok: true, origin: 'https://hr.example.sa' });
  assert.deepEqual(normalizeServerAddress('https://HR.Example.sa:8443/anything?x=1'), { ok: true, origin: 'https://hr.example.sa:8443' });
});

test('only HTTPS, never loopback, never credentials', () => {
  assert.deepEqual(normalizeServerAddress(''), { ok: false, error: 'empty' });
  assert.deepEqual(normalizeServerAddress('http://hr.example.sa'), { ok: false, error: 'httpsRequired' });
  assert.deepEqual(normalizeServerAddress('ftp://hr.example.sa'), { ok: false, error: 'httpsRequired' });
  for (const host of ['localhost', '127.0.0.1', '127.1.2.3', '0.0.0.0', '[::1]']) {
    assert.deepEqual(normalizeServerAddress(`https://${host}`), { ok: false, error: 'loopback' }, host);
  }
  assert.deepEqual(normalizeServerAddress('https://user:pass@hr.example.sa'), { ok: false, error: 'credentials' });
  assert.deepEqual(normalizeServerAddress('https://'), { ok: false, error: 'invalid' });
});

test('a development build may use plain http and a local server', () => {
  assert.deepEqual(normalizeServerAddress('http://192.168.1.20:5117', { allowInsecure: true }), { ok: true, origin: 'http://192.168.1.20:5117' });
  assert.deepEqual(normalizeServerAddress('http://localhost:5117', { allowInsecure: true }), { ok: true, origin: 'http://localhost:5117' });
});

test('the Tenant Admin QR code round-trips', () => {
  const payload = serverQrPayload('https://hr.example.sa');
  assert.equal(payload, 'kynexone://server?url=https%3A%2F%2Fhr.example.sa');
  assert.deepEqual(parseServerQr(payload), { ok: true, origin: 'https://hr.example.sa' });
  assert.deepEqual(parseServerQr('kynexone://server?url=https://hr.example.sa/api'), { ok: true, origin: 'https://hr.example.sa' });
});

test('a QR code that is not a KynexOne server code, or carries an unsafe address, is refused', () => {
  assert.deepEqual(parseServerQr('https://hr.example.sa'), { ok: false, error: 'notKynexOneQr' });
  assert.deepEqual(parseServerQr('kynexone://server'), { ok: false, error: 'notKynexOneQr' });
  assert.deepEqual(parseServerQr('WIFI:S:office;T:WPA;P:secret;;'), { ok: false, error: 'notKynexOneQr' });
  assert.deepEqual(parseServerQr('kynexone://server?url=http://hr.example.sa'), { ok: false, error: 'httpsRequired' });
  assert.deepEqual(parseServerQr('kynexone://server?url=https://localhost'), { ok: false, error: 'loopback' });
});

test('the API base is the origin plus /api', () => {
  assert.equal(apiBaseUrlFor('https://hr.example.sa'), 'https://hr.example.sa/api');
  assert.equal(apiBaseUrlFor('https://hr.example.sa/'), 'https://hr.example.sa/api');
});

test('only a KynexOne installation answer is accepted', () => {
  assert.equal(isInstallationInfo({ setupComplete: true, organisationName: 'Al Noor', pushEnabled: false, licence: { state: 'Valid' } }), true);
  assert.equal(isInstallationInfo({ setupComplete: false, organisationName: null, pushEnabled: true, licence: { state: 'Missing' } }), true);
  assert.equal(isInstallationInfo({ status: 'ok' }), false);
  assert.equal(isInstallationInfo('<html>'), false);
  assert.equal(isInstallationInfo(null), false);
});
