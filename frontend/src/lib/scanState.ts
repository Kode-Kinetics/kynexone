/**
 * Where an uploaded file stands with the security scan (CD66). The API's statuses are `clean`, `not_scanned_trusted_source` (demo or
 * default mode: upload allowed, never shown as clean), `quarantined` and `quarantined_pending` (scan pending or failed). The older
 * PascalCase names (`Clean`, `PendingScan`, `Quarantined`) read the same. Anything unrecognised fails closed: no download.
 */
export type ScanState = 'clean' | 'trusted' | 'pending' | 'quarantined' | 'none';

export function scanState(raw?: string | null): ScanState {
  if (raw == null || raw === '') return 'none';
  const k = raw.toLowerCase().replace(/[_\s-]/g, '');
  if (k === 'clean') return 'clean';
  if (k === 'notscannedtrustedsource') return 'trusted';
  if (k === 'quarantined') return 'quarantined';
  return 'pending';
}

export const canDownloadScanned = (state: ScanState) => state === 'clean' || state === 'trusted';

const LABEL: Record<ScanState, string | null> = {
  clean: 'ess.completion.doc.scan.Clean', trusted: 'ess.completion.doc.scan.trusted',
  pending: 'ess.completion.doc.scan.PendingScan', quarantined: 'ess.completion.doc.scan.Quarantined', none: null,
};
export const scanLabelKey = (state: ScanState) => LABEL[state];

export const scanRefusalKey = (state: ScanState): string | null =>
  state === 'quarantined' ? 'ess.completion.doc.scan.quarantinedNote' : state === 'pending' ? 'ess.completion.doc.scan.pendingNote' : null;

/** HR may run the scan again only on a file the scanner could not judge (`quarantined_pending`), and only with the document permissions. A file found infected is never offered. */
export const canRescan = (raw: string | null | undefined, can: { write: boolean; documents: boolean }) =>
  can.write && can.documents && !!raw && raw.toLowerCase().replace(/[_\s-]/g, '') === 'quarantinedpending';

/** The status the server answered with, in the words of the existing labels. */
export const rescanStatusKey = (raw?: string | null) => scanLabelKey(scanState(raw)) ?? 'ess.completion.doc.scan.PendingScan';
