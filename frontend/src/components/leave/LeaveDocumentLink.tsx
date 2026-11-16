'use client';

import { useEffect, useState } from 'react';
import { FileText } from 'lucide-react';
import { leaveRequestsApi } from '../../api/leave';
import { openBlob } from '../../api/esign';
import { useLocale } from '../../contexts/LocaleContext';

const BUTTON = 'inline-flex items-center gap-1 rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-medium text-slate-600 hover:bg-slate-50 disabled:opacity-50 dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5';

/** Types a browser shows without running anything in this app's origin. Any other file is saved, never opened here. */
const VIEWABLE = new Set(['application/pdf', 'image/png', 'image/jpeg']);

/**
 * "View document" for a leave request's supporting document. The server tells only the leave's own employee and
 * Admin / HR Manager / HR Officer that a document exists (`attachmentPath` / `hasDocument`), and the download endpoint
 * applies the same rule, so this needs no role check of its own. A caller that holds only the leave id (the generic
 * approvals inbox) leaves `available` undefined and the leave detail is read once to find out.
 */
export function LeaveDocumentLink({ leaveId, available }: { leaveId: string; available?: boolean }) {
  const { t } = useLocale();
  const [known, setKnown] = useState<boolean | undefined>(available);
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    if (available !== undefined) { setKnown(available); return; }
    let live = true;
    setKnown(undefined);
    leaveRequestsApi.detail(leaveId)
      .then(d => { if (live) setKnown(!!d.request.attachmentPath); })
      .catch(() => { if (live) setKnown(false); });
    return () => { live = false; };
  }, [leaveId, available]);

  if (!known) return null;

  const open = async () => {
    setBusy(true); setFailed(false);
    // Open the tab inside the click (a popup blocker allows that), then point it at the fetched file.
    const tab = window.open('', '_blank');
    try {
      const { blob, fileName } = await leaveRequestsApi.attachment(leaveId);
      const type = (blob.type || '').split(';')[0].trim().toLowerCase();
      if (tab && VIEWABLE.has(type)) {
        const url = URL.createObjectURL(new Blob([blob], { type }));
        tab.opener = null;
        tab.location.href = url;
        setTimeout(() => URL.revokeObjectURL(url), 60_000);
      } else {
        tab?.close();
        openBlob(new Blob([blob], { type: 'application/octet-stream' }), fileName);
      }
    } catch {
      tab?.close();
      setFailed(true);
    } finally {
      setBusy(false);
    }
  };

  return (
    <span className="inline-flex flex-wrap items-center gap-2">
      <button type="button" className={BUTTON} onClick={() => void open()} disabled={busy} aria-busy={busy || undefined} data-testid="leave-view-document">
        <FileText className="h-3.5 w-3.5" aria-hidden="true" />
        {busy ? t('Opening…') : t('View document')}
      </button>
      {failed && <span role="alert" className="text-xs text-rose-600 dark:text-rose-400">{t('The supporting document could not be opened. It may have been removed.')}</span>}
    </span>
  );
}
