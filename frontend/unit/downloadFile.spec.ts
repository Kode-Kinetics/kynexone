import { expect, test } from '@playwright/test';
import { downloadAuthenticated, saveBlob, unwrapBlobError, type DownloadEnv } from '../src/lib/downloadFile';
import { documentDownloadState } from '../src/lib/documentDownload';

/** A DOM-free environment: the helper takes it as a parameter so the unit lane (plain Node) can observe every side effect. */
function fakeEnv() {
  const log: string[] = [];
  const timers: Array<() => void> = [];
  const env: DownloadEnv = {
    createObjectURL: () => { log.push('create'); return 'blob:test/1'; },
    revokeObjectURL: url => { log.push(`revoke ${url}`); },
    triggerDownload: (url, fileName) => { log.push(`click ${url} ${fileName}`); },
    defer: fn => { timers.push(fn); },
  };
  return { env, log, flush: () => timers.splice(0).forEach(fn => fn()) };
}

test('saveBlob saves through an object URL with the file name and revokes it afterwards', () => {
  const { env, log, flush } = fakeEnv();
  saveBlob(new Blob(['x']), 'passport.pdf', env);
  expect(log).toEqual(['create', 'click blob:test/1 passport.pdf']);
  flush();
  expect(log.at(-1)).toBe('revoke blob:test/1');
});

test('downloadAuthenticated fetches the bytes first (through the caller-supplied authenticated request) and then saves them', async () => {
  const { env, log } = fakeEnv();
  const order: string[] = [];
  await downloadAuthenticated(async () => { order.push('fetch'); return new Blob(['pdf']); }, 'contract.pdf', env);
  expect(order).toEqual(['fetch']);
  expect(log[0]).toBe('create');
  expect(log[1]).toBe('click blob:test/1 contract.pdf');
});

test('downloadAuthenticated saves nothing when the request fails, and rethrows so the screen can show the error', async () => {
  const { env, log } = fakeEnv();
  const failure = Object.assign(new Error('boom'), { response: { status: 403, data: {} } });
  await expect(downloadAuthenticated(async () => { throw failure; }, 'a.pdf', env)).rejects.toBe(failure);
  expect(log).toEqual([]);
});

test('a blob-typed error body is parsed so the JSON code and message reach the error mapper', async () => {
  const blob = new Blob([JSON.stringify({ code: 'document_quarantined', message: 'blocked' })], { type: 'application/json' });
  const err = { isAxiosError: true, response: { status: 409, data: blob } };
  const out = await unwrapBlobError(err) as { response: { status: number; data: { code: string } } };
  expect(out.response.status).toBe(409);
  expect(out.response.data.code).toBe('document_quarantined');
});

test('a blob error body that is not JSON is left as an empty problem rather than throwing', async () => {
  const err = { isAxiosError: true, response: { status: 502, data: new Blob(['<html>bad gateway</html>']) } };
  const out = await unwrapBlobError(err) as { response: { data: unknown } };
  expect(out.response.data).toEqual({});
});

test('unwrapBlobError leaves errors without a blob body untouched', async () => {
  const plain = new Error('network');
  expect(await unwrapBlobError(plain)).toBe(plain);
  const json = { response: { status: 404, data: { code: 'not_found' } } };
  expect(await unwrapBlobError(json)).toBe(json);
});

test('downloadAuthenticated rethrows a blob error already parsed', async () => {
  const { env } = fakeEnv();
  const blob = new Blob([JSON.stringify({ code: 'document_quarantined' })]);
  const caught = await downloadAuthenticated(async () => { throw { isAxiosError: true, response: { status: 409, data: blob } }; }, 'a.pdf', env).catch(e => e);
  expect(caught.response.data.code).toBe('document_quarantined');
});

// ---- who may download an employee document from the workspace --------------------------------

const doc = { fileName: 'passport.pdf', scanStatus: 'clean' };
const all = { documents: true, sensitive: true };

test('documentDownloadState: a scanned, visible file is available to a holder of employees.documents', () => {
  expect(documentDownloadState(doc, all)).toBe('available');
  expect(documentDownloadState({ ...doc, scanStatus: 'not_scanned_trusted_source' }, { documents: true, sensitive: false })).toBe('available');
});

test('documentDownloadState: without employees.documents the server refuses (403), so the action explains instead of failing', () => {
  expect(documentDownloadState(doc, { documents: false, sensitive: true })).toBe('needs_documents_permission');
});

test('documentDownloadState: a masked sensitive file (no file name sent) needs employees.sensitive', () => {
  expect(documentDownloadState({ fileName: '', scanStatus: 'clean' }, { documents: true, sensitive: false })).toBe('needs_sensitive_permission');
  expect(documentDownloadState({ fileName: '', scanStatus: 'clean' }, { documents: true, sensitive: true })).toBe('available');
});

test('documentDownloadState: a file the scan has not passed is never offered (the scan label already says why)', () => {
  for (const scanStatus of ['quarantined', 'quarantined_pending', 'PendingScan', undefined, 'weird']) {
    expect(documentDownloadState({ fileName: 'a.pdf', scanStatus }, all), String(scanStatus)).toBe('scan_blocked');
  }
});
