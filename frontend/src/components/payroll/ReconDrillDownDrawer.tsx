'use client';

import { X } from 'lucide-react';
import { useCallback, useEffect, useId, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { payrollReconApi, type ReconEmployeesPage } from '../../api/payrollRecon';
import { useFormat } from '../../hooks/useFormat';
import { useT } from '../../hooks/useT';
import { drillParams, signedAmount, type DrillTarget } from '../../lib/payrollRecon';
import { requestFailureReason } from '../../lib/requestFailure';
import { Button } from '../ui/Button';
import { SkeletonRows } from '../ui/Skeleton';
import { Table, Td, Th } from '../ui/Table';
import { categoryName } from './reconWords';

const PAGE_SIZE = 50;
const FOCUSABLE = 'a[href],button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled]),[tabindex]:not([tabindex="-1"])';

/**
 * The employees behind one reconciliation figure, read from the server for exactly that cell. A side
 * drawer on the inline-end edge: focus moves in on open, Tab stays inside, Escape or the backdrop close
 * it and focus returns to the figure that opened it. A failed read says so and offers Retry; it is
 * never shown as an empty list.
 */
export function ReconDrillDownDrawer({ runId, priorRunId, target, title, subtitle, currency, opener, onClose }: {
  runId: string;
  priorRunId: string | null;
  target: DrillTarget | null;
  title: string;
  subtitle?: string;
  currency: string | null;
  opener: HTMLElement | null;
  onClose: () => void;
}) {
  const t = useT();
  const fmt = useFormat();
  const titleId = useId();
  const dialogRef = useRef<HTMLDivElement>(null);
  const closeRef = useRef<HTMLButtonElement>(null);
  const [page, setPage] = useState(1);
  const [data, setData] = useState<ReconEmployeesPage | null>(null);
  const [error, setError] = useState('');
  const [attempt, setAttempt] = useState(0);
  const open = target !== null;
  const onCloseRef = useRef(onClose);
  useEffect(() => { onCloseRef.current = onClose; }, [onClose]);

  useEffect(() => { setPage(1); }, [target]);

  useEffect(() => {
    if (!target) return;
    let live = true;
    setData(null); setError('');
    payrollReconApi.employees(runId, { ...drillParams(target), page, pageSize: PAGE_SIZE, ...(priorRunId ? { priorRunId } : {}) })
      .then(r => { if (live) setData(r); })
      .catch(e => { if (live) setError(requestFailureReason(e)); });
    return () => { live = false; };
  }, [runId, priorRunId, target, page, attempt]);

  // Focus in on open, back to the opener on close; the page behind does not scroll.
  useEffect(() => {
    if (!open) return;
    const overflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    requestAnimationFrame(() => closeRef.current?.focus());
    return () => {
      document.body.style.overflow = overflow;
      if (opener && opener.isConnected) opener.focus();
    };
  }, [open, opener]);

  const onKeyDown = useCallback((e: React.KeyboardEvent) => {
    if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); onCloseRef.current(); return; }
    if (e.key !== 'Tab') return;
    const nodes = Array.from(dialogRef.current?.querySelectorAll<HTMLElement>(FOCUSABLE) ?? []);
    if (nodes.length === 0) return;
    const first = nodes[0]; const last = nodes[nodes.length - 1];
    if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
    else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
  }, []);

  if (!open || typeof document === 'undefined') return null;

  const rows = data?.items ?? [];
  const pages = data ? Math.max(1, Math.ceil(data.total / (data.pageSize || PAGE_SIZE))) : 1;
  const hasMoney = rows.some(r => r.prior != null || r.current != null || r.delta != null);
  const amount = (n: number | null | undefined) => (n == null ? '—' : fmt.money(n, currency));

  return createPortal(
    <div className="fixed inset-0 z-50" onKeyDown={onKeyDown}>
      <div className="absolute inset-0 bg-slate-900/40" onClick={onClose} aria-hidden="true" />
      <div
        ref={dialogRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        data-testid="recon-drilldown"
        className="absolute inset-y-0 end-0 flex w-full max-w-2xl flex-col border-s border-line bg-surface shadow-e3"
      >
        <div className="flex items-start justify-between gap-3 border-b border-line px-5 py-4">
          <div className="min-w-0">
            <h2 id={titleId} className="text-h4 font-semibold text-fg">{title}</h2>
            {subtitle && <p className="text-body-sm text-fg-muted">{subtitle}</p>}
          </div>
          <Button ref={closeRef} size="sm" variant="ghost" onClick={onClose}>
            <X className="h-4 w-4" aria-hidden="true" /> {t('Close')}
          </Button>
        </div>

        <div className="flex-1 space-y-3 overflow-y-auto px-5 py-4">
          {error && (
            <div role="alert" className="rounded-lg border border-danger/30 bg-danger-soft px-3 py-2 text-body-sm text-danger-fg">
              <p>{t('The employees could not be loaded. {reason}', { reason: error })}</p>
              <Button size="sm" className="mt-2" onClick={() => setAttempt(n => n + 1)}>{t('Retry')}</Button>
            </div>
          )}
          {!error && !data && <SkeletonRows rows={5} />}
          {data && rows.length === 0 && <p className="text-body-sm text-fg-muted">{t('No employees fall in this figure.')}</p>}
          {data && rows.length > 0 && (
            <>
              <p className="text-body-sm text-fg-muted" data-testid="recon-drilldown-count">
                {t('{count, plural, one {# employee} other {# employees}}', { count: data.total })}
              </p>
              <Table caption={t('Employees behind {figure}', { figure: title })} density="compact">
                <thead>
                  <tr>
                    <Th>{t('Employee')}</Th>
                    <Th>{t('Detail')}</Th>
                    {hasMoney && <Th numeric>{t('Prior month')}</Th>}
                    {hasMoney && <Th numeric>{t('This month')}</Th>}
                    {hasMoney && <Th numeric>{t('Change')}</Th>}
                  </tr>
                </thead>
                <tbody>
                  {rows.map(r => (
                    <tr key={r.employeeId} data-testid="recon-drilldown-row">
                      <Td>{r.employeeName ? `${r.employeeName}${r.employeeCode ? ` (${r.employeeCode})` : ''}` : t('Employee {id}', { id: r.employeeId })}</Td>
                      <Td>{[r.category ? categoryName(t, r.category) : null, r.note].filter(Boolean).join(' · ') || '—'}</Td>
                      {hasMoney && <Td numeric><bdi>{amount(r.prior)}</bdi></Td>}
                      {hasMoney && <Td numeric><bdi>{amount(r.current)}</bdi></Td>}
                      {hasMoney && <Td numeric><bdi>{r.delta == null ? '—' : signedAmount(r.delta, v => fmt.money(v, currency))}</bdi></Td>}
                    </tr>
                  ))}
                </tbody>
              </Table>
              {pages > 1 && (
                <nav aria-label={t('Pages of employees')} className="flex items-center justify-between gap-2">
                  <Button size="sm" disabled={page <= 1} onClick={() => setPage(p => Math.max(1, p - 1))}>{t('Previous')}</Button>
                  <span className="text-body-sm text-fg-muted"><bdi>{t('Page {page} of {pages}', { page: fmt.integer(page), pages: fmt.integer(pages) })}</bdi></span>
                  <Button size="sm" disabled={page >= pages} onClick={() => setPage(p => p + 1)}>{t('Next')}</Button>
                </nav>
              )}
            </>
          )}
        </div>
      </div>
    </div>,
    document.body,
  );
}
