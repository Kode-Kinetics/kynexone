'use client';

import { useState } from 'react';
import { employeesApi, type EmployeeDocument } from '../../api/employees';
import { openBlob } from '../../api/esign';
import { describeApiError } from '../../lib/apiError';
import { rejectReasonOrNull, MAX_REJECT_REASON } from '../../lib/documentVerification';
import { useT } from '../../hooks/useT';
import { Modal } from '../Modal';
import { useConfirm } from '../ui/useConfirm';

/** The document a decision is about. */
export interface DocumentTarget {
  employeeId: number;
  documentId: string;
  documentType: string;
  fileName: string;
  employeeName?: string;
}

/**
 * G-004: Verify and Reject for one employee document, shared by the "Documents to verify" queue and the employee's own
 * Compliance & documents section. Verify asks to confirm (the app's confirm dialog); Reject needs a reason the employee will
 * read. The server decides who may; a refusal is shown in words, never swallowed.
 */
export function useDocumentDecision(onDecided: (updated: EmployeeDocument) => void) {
  const t = useT();
  const { confirm, dialog: confirmDialog } = useConfirm();
  const [rejecting, setRejecting] = useState<DocumentTarget | null>(null);
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const validReason = rejectReasonOrNull(reason);

  const who = (target: DocumentTarget) => target.employeeName ? `${target.documentType} · ${target.employeeName}` : target.documentType;

  const verify = async (target: DocumentTarget) => {
    setError('');
    const ok = await confirm({
      title: t('Verify this document?'),
      message: t('{document} will count as verified. The employee is told, and any readiness rule that needs a verified document is met.', { document: who(target) }),
      confirmLabel: t('Verify document'),
      tone: 'primary',
    });
    if (!ok) return;
    setBusy(true);
    try { onDecided(await employeesApi.verifyDocument(target.employeeId, target.documentId)); }
    catch (e) { setError(describeApiError(e, t)); }
    finally { setBusy(false); }
  };

  const askReject = (target: DocumentTarget) => { setError(''); setReason(''); setRejecting(target); };

  const reject = async () => {
    if (!rejecting || !validReason) return;
    setBusy(true); setError('');
    try {
      onDecided(await employeesApi.rejectDocument(rejecting.employeeId, rejecting.documentId, validReason));
      setRejecting(null);
    } catch (e) { setError(describeApiError(e, t)); }
    finally { setBusy(false); }
  };

  const download = async (target: DocumentTarget) => {
    setError('');
    try { openBlob(await employeesApi.downloadDocument(target.documentId), target.fileName || 'document'); }
    catch (e) { setError(describeApiError(e, t)); }
  };

  const dialogs = (
    <>
      {confirmDialog}
      <Modal
        isOpen={rejecting !== null}
        title={t('Reject this document')}
        onClose={() => { if (!busy) setRejecting(null); }}
        closeDisabled={busy}
        size="sm"
        footer={(
          <>
            <button type="button" className="btn-secondary" disabled={busy} onClick={() => setRejecting(null)}>{t('Cancel')}</button>
            <button type="button" className="btn-danger" disabled={busy || !validReason} onClick={() => void reject()}>{busy ? t('Rejecting…') : t('Reject document')}</button>
          </>
        )}
      >
        {rejecting && <p className="text-sm text-slate-600 dark:text-slate-300">{who(rejecting)}</p>}
        <label className="mt-3 block text-sm font-semibold">
          {t('Reason the employee will read')}
          <textarea
            className="input mt-1.5 w-full"
            rows={3}
            maxLength={MAX_REJECT_REASON}
            value={reason}
            disabled={busy}
            onChange={event => setReason(event.target.value)}
            aria-describedby="reject-reason-help"
          />
        </label>
        <p id="reject-reason-help" className="mt-1 text-xs text-slate-500">{t('Say what is wrong and what to upload instead. At least 3 characters.')}</p>
        {error && <p role="alert" className="mt-3 rounded-lg bg-rose-50 px-3 py-2 text-sm font-semibold text-rose-700 dark:bg-rose-500/10 dark:text-rose-200">{error}</p>}
      </Modal>
    </>
  );

  return { verify, askReject, download, dialogs, busy, error: rejecting ? '' : error };
}
