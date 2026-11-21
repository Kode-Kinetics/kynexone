'use client';

import { useEffect, useRef, useState } from 'react';
import { Loader2 } from 'lucide-react';
import { payrollApi } from '../../api/payroll';
import { useT } from '../../hooks/useT';
import { extractApiError, isAbortError } from '../../api/saudiBankExports';
import { paymentActionRefusal } from '../../lib/payrollPaymentAccess';
import { Modal } from '../Modal';
import { Field, Input, Textarea } from '../ui/Field';

/**
 * "Record as submitted to {bank}?": the person who handed the file to the bank says so, with the reference the bank gave the upload.
 * It calls the existing WPS status transition (Submitted needs a reference on the server too). Busy and refusals stay in the dialog,
 * and a second click cannot send it twice.
 */
export function RecordSubmittedDialog({ open, batchId, bankName, summary, onClose, onRecorded }: {
  open: boolean;
  batchId: string;
  /** The bank's name, when the setup names one. */
  bankName: string | null;
  /** "{count} employees · {amount} · credit date {date} · file {ref}". */
  summary: string;
  onClose: () => void;
  onRecorded: (reference: string) => void;
}) {
  const t = useT();
  const [reference, setReference] = useState('');
  const [notes, setNotes] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [showRequired, setShowRequired] = useState(false);
  const sending = useRef(false);

  useEffect(() => { if (open) { setReference(''); setNotes(''); setError(''); setShowRequired(false); } }, [open]);

  const submit = async (e?: React.FormEvent) => {
    e?.preventDefault();
    if (sending.current) return;
    if (!reference.trim()) { setShowRequired(true); return; }
    sending.current = true; setBusy(true); setError('');
    try {
      await payrollApi.updateWpsStatus(batchId, { status: 'Submitted', reference: reference.trim(), notes: notes.trim() || undefined });
      onRecorded(reference.trim());
    } catch (err) {
      if (isAbortError(err)) return;
      const refusal = paymentActionRefusal(err, 'wps');
      const { message } = await extractApiError(err, t('The request failed. Please try again.'));
      setError(refusal ? t(refusal) : message);
    } finally {
      sending.current = false; setBusy(false);
    }
  };

  const formId = 'bank-submit-form';
  return (
    <Modal
      isOpen={open}
      title={bankName ? t('Record as submitted to {bank}?', { bank: bankName }) : t('Record as submitted to your bank?')}
      size="md"
      onClose={onClose}
      closeDisabled={busy}
      closeLabel={t('Close')}
      footer={(
        <div className="flex flex-wrap items-center justify-end gap-2">
          <button type="button" className="btn-secondary" onClick={onClose} disabled={busy}>{t('Cancel')}</button>
          <button type="submit" form={formId} className="btn-primary" disabled={busy} aria-busy={busy || undefined}>
            {busy && <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />}
            {t('Record as submitted')}
          </button>
        </div>
      )}
    >
      <form id={formId} onSubmit={submit} noValidate className="space-y-4" data-testid="bank-submit-dialog">
        <p className="text-body text-fg">{summary}</p>
        <Field label={t('Bank submission reference')} required noInfo="plain reference" hint={t('The reference your bank gave this upload.')}
          error={showRequired && !reference.trim() ? t('Enter the bank submission reference.') : undefined} size="md">
          <Input value={reference} onChange={e => setReference(e.target.value)} dir="ltr" autoComplete="off" spellCheck={false} disabled={busy} />
        </Field>
        <Field label={t('Notes')} noInfo="free text" size="md">
          <Textarea value={notes} onChange={e => setNotes(e.target.value)} rows={2} disabled={busy} />
        </Field>
        {error && <p role="alert" className="rounded-lg bg-danger-soft px-3 py-2 text-body-sm text-danger-fg">{error}</p>}
      </form>
    </Modal>
  );
}
