import { expect, test } from '@playwright/test';
import { LOCALE_DICTS } from '../src/i18n/translations';
import { canRescan, rescanStatusKey } from '../src/lib/scanState';

const both = { write: true, documents: true };

test('Scan again is offered only for a file waiting for its scan, and only with both document permissions', () => {
  expect(canRescan('quarantined_pending', both)).toBe(true);
  expect(canRescan('QuarantinedPending', both)).toBe(true);
  for (const s of ['clean', 'not_scanned_trusted_source', 'quarantined', '', null, undefined]) expect(canRescan(s, both), String(s)).toBe(false);
  expect(canRescan('quarantined_pending', { write: false, documents: true })).toBe(false);
  expect(canRescan('quarantined_pending', { write: true, documents: false })).toBe(false);
});

test('the answer from the server is shown in the existing scan words', () => {
  expect(rescanStatusKey('clean')).toBe('ess.completion.doc.scan.Clean');
  expect(rescanStatusKey('quarantined')).toBe('ess.completion.doc.scan.Quarantined');
  expect(rescanStatusKey('quarantined_pending')).toBe('ess.completion.doc.scan.PendingScan');
});

test('every Scan again string has Arabic', () => {
  for (const k of ['Scan again', 'Scanning…', 'ess.completion.rescan.done', 'ess.completion.rescan.notPending', 'ess.completion.rescan.failed'])
    expect(LOCALE_DICTS.ar[k], k).toBeTruthy();
});
