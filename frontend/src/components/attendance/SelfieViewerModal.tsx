import { useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { attendanceApi } from '../../api/attendance';
import { useLocale } from '../../contexts/LocaleContext';

type ViewState =
  | { kind: 'loading' }
  | { kind: 'ready'; url: string }
  | { kind: 'error'; status?: number };

/**
 * HR's view of the selfie behind one punch. The image is fetched as a blob (the server answers
 * Cache-Control: no-store and audits every view), shown from an object URL, and that URL is revoked
 * the moment the dialog closes, so the face image does not outlive the review in the browser.
 */
export function SelfieViewerModal({ rawEventId, employee, time, onClose }: {
  rawEventId: string;
  employee: string;
  time: string;
  onClose: () => void;
}) {
  const { t } = useLocale();
  const [state, setState] = useState<ViewState>({ kind: 'loading' });
  const closeRef = useRef<HTMLButtonElement>(null);
  const titleId = `selfie-title-${rawEventId}`;

  useEffect(() => {
    let cancelled = false;
    let url: string | null = null;
    attendanceApi.evidence.selfie(rawEventId)
      .then((blob) => {
        if (cancelled) return;
        url = URL.createObjectURL(blob);
        setState({ kind: 'ready', url });
      })
      .catch((err: { response?: { status?: number } }) => {
        if (cancelled) return;
        setState({ kind: 'error', status: err?.response?.status });
      });
    return () => {
      cancelled = true;
      if (url) URL.revokeObjectURL(url);
    };
    // Only the punch decides a fetch: every fetch is an audited view, so a language switch must not refetch.
  }, [rawEventId]);

  useEffect(() => {
    closeRef.current?.focus();
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [onClose]);

  if (typeof document === 'undefined') return null;
  return createPortal(
    <div className="fixed inset-0 z-[9999] overflow-y-auto bg-black/50 backdrop-blur-sm">
      <div className="flex min-h-screen items-start justify-center p-4" onClick={(e) => { if (e.target === e.currentTarget) onClose(); }}>
        <div role="dialog" aria-modal="true" aria-labelledby={titleId}
          className="relative my-8 w-full max-w-md rounded-2xl border border-slate-200 bg-white shadow-2xl dark:border-white/10 dark:bg-[#0e1729]">
          <div className="border-b border-slate-100 px-6 py-4 dark:border-white/[0.07]">
            <h2 id={titleId} className="text-base font-bold text-slate-900 dark:text-white">
              {t('Selfie on the punch by {employee} at {time}', { employee, time })}
            </h2>
            <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
              {t('The selfie the employee took with this punch. Opening it is recorded in the audit log.')}
            </p>
          </div>
          <div className="grid min-h-[12rem] place-items-center p-6">
            {state.kind === 'loading' && <p role="status" className="text-sm text-slate-500">{t('Loading the selfie…')}</p>}
            {state.kind === 'ready' && (
              // eslint-disable-next-line @next/next/no-img-element -- a blob: URL, never optimised or cached by Next
              <img src={state.url} alt={t('Selfie of the employee')} className="max-h-[60vh] max-w-full rounded-xl object-contain" />
            )}
            {state.kind === 'error' && (
              <p role="alert" className="text-sm text-rose-600 dark:text-rose-400">
                {state.status === 404
                  ? t('There is no stored selfie for this punch. It may have been deleted under the retention rule.')
                  : state.status === 403
                    ? t('You cannot open this selfie. HR cannot review the selfie on their own punch, and opening one needs the selfie-review permission.')
                    : t('The selfie could not be loaded. Close this window and try again.')}
              </p>
            )}
          </div>
          <div className="flex justify-end border-t border-slate-100 px-6 py-4 dark:border-white/[0.07]">
            <button ref={closeRef} type="button" onClick={onClose} className="btn-secondary">{t('Close')}</button>
          </div>
        </div>
      </div>
    </div>,
    document.body,
  );
}
