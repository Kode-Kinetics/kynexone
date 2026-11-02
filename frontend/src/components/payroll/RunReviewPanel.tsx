'use client';

import { useEffect, useMemo, useState } from 'react';
import { CheckCircle2 } from 'lucide-react';
import { payrollApi, type PayrollReconciliation, type PayrollRun, type PayrollSlip, type PayrollValidationResult } from '../../api/payroll';
import { changeBetween, employeesWithIssues } from '../../lib/payrollPipeline';
import { requestFailureReason } from '../../lib/requestFailure';
import { useFormat } from '../../hooks/useFormat';
import { useT } from '../../hooks/useT';
import { Button } from '../ui/Button';
import { Table, Td, Th } from '../ui/Table';

const ISSUES_SHOWN = 10;

/**
 * The Review stop point: the reviewer reads how this run moved from the prior period and which
 * employees have open validation results before Approve is offered. Prior-period figures come from the
 * reconciliation report (the server's own comparison); issues come from the saved validation results.
 */
export function RunReviewPanel({ run, results, currency, reviewedAtUtc, onComplete, onOpenValidation }: {
  run: PayrollRun;
  results: PayrollValidationResult[] | null;
  /** The confirmed company currency, or null when it could not be confirmed (figures then carry none). */
  currency: string | null;
  reviewedAtUtc: string | null;
  onComplete: () => void;
  onOpenValidation: () => void;
}) {
  const t = useT();
  const fmt = useFormat();
  const [recon, setRecon] = useState<PayrollReconciliation | null>(null);
  const [reconError, setReconError] = useState('');
  const [slips, setSlips] = useState<PayrollSlip[] | null>(null);
  const [onlyIssues, setOnlyIssues] = useState(true);
  const [showAllIssues, setShowAllIssues] = useState(false);
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let live = true;
    setRecon(null); setReconError('');
    payrollApi.reconciliation(run.id)
      .then(r => { if (live) setRecon(r); })
      .catch(e => { if (live) setReconError(requestFailureReason(e)); });
    return () => { live = false; };
  }, [run.id, run.processedAtUtc, attempt]);

  const issues = useMemo(() => employeesWithIssues(results ?? []), [results]);
  const hasIssues = issues.length > 0;

  // Names for the employees with issues, read only when there are some; ids stand in if the register cannot be read.
  useEffect(() => {
    let live = true;
    setSlips(null);
    if (!hasIssues) return;
    payrollApi.allSlips(run.id).then(s => { if (live) setSlips(s); }).catch(() => { if (live) setSlips(null); });
    return () => { live = false; };
  }, [run.id, run.processedAtUtc, hasIssues]);

  const nameOf = useMemo(() => {
    const m = new Map<number, string>();
    (slips ?? []).forEach(s => m.set(s.employeeId, `${s.employeeName} (${s.employeeCode})`));
    return (id: number) => m.get(id) ?? t('Employee {id}', { id });
  }, [slips, t]);

  const money = (n: number) => fmt.money(n, currency);
  const signed = (n: number, f: (v: number) => string) => `${n > 0 ? '+' : n < 0 ? '−' : ''}${f(Math.abs(n))}`;
  const pct = (cur: number, prev: number) => {
    const c = changeBetween(cur, prev);
    return c.percent === null ? '—' : signed(c.percent, v => fmt.percent(v, 1));
  };

  const rows = recon ? [
    { id: 'headcount', label: t('Employees paid'), cur: recon.currentHeadcount, prev: recon.priorHeadcount, f: (v: number) => fmt.integer(v) },
    { id: 'gross', label: t('Gross pay'), cur: recon.currentTotalGross, prev: recon.priorTotalGross, f: money },
    { id: 'net', label: t('Net pay'), cur: recon.currentTotalNet, prev: recon.priorTotalNet, f: money },
  ] : [];
  const flagged = (recon?.variances ?? []).filter(v => v.isVarianceFlag);
  const visibleIssues = showAllIssues ? issues : issues.slice(0, ISSUES_SHOWN);

  return (
    <section aria-labelledby={`review-${run.id}`} className="space-y-4 rounded-lg border border-line bg-surface-sunken p-4" data-testid="run-review">
      <div>
        <h3 id={`review-${run.id}`} className="text-h4 font-semibold text-fg">{t('Review before approval')}</h3>
        <p className="mt-0.5 text-body-sm text-fg-muted">{t('Read how this run moved from the prior period and who has open results. Approve is offered once you mark the review complete.')}</p>
      </div>

      <div className="space-y-2">
        <h4 className="text-body font-semibold text-fg">{t('Compared with the prior period')}</h4>
        {reconError && (
          <p role="alert" className="text-body-sm text-danger-fg" data-testid="review-comparison-failed">
            {t('The comparison could not be loaded. {reason}', { reason: reconError })}{' '}
            <button type="button" className="underline" onClick={() => setAttempt(n => n + 1)}>{t('Retry')}</button>
          </p>
        )}
        {!reconError && !recon && <p className="text-body-sm text-fg-muted">{t('Loading…')}</p>}
        {recon && !recon.priorPeriod && <p className="text-body-sm text-fg-muted">{t('There is no prior period to compare with.')}</p>}
        {recon && recon.priorPeriod && (
          <>
            <Table caption={t('Comparison with the prior period')} density="compact">
              <thead>
                <tr>
                  <Th>{t('Figure')}</Th>
                  <Th numeric>{t('This run')}</Th>
                  <Th numeric>{t('Prior period')}</Th>
                  <Th numeric>{t('Change')}</Th>
                </tr>
              </thead>
              <tbody>
                {rows.map(r => (
                  <tr key={r.id} data-testid={`review-row-${r.id}`}>
                    <Td>{r.label}</Td>
                    <Td numeric><bdi>{r.f(r.cur)}</bdi></Td>
                    <Td numeric><bdi>{r.f(r.prev)}</bdi></Td>
                    <Td numeric><bdi>{signed(r.cur - r.prev, r.f)} ({pct(r.cur, r.prev)})</bdi></Td>
                  </tr>
                ))}
              </tbody>
            </Table>
            <p className="text-body-sm text-fg-muted">
              {t('{joiners, plural, one {# joiner} other {# joiners}} and {leavers, plural, one {# leaver} other {# leavers}} against {period}.', { joiners: recon.joinerCount, leavers: recon.leaverCount, period: recon.priorPeriod })}
              {flagged.length > 0 && ` ${t('{count, plural, one {# employee changed by more than the variance threshold.} other {# employees changed by more than the variance threshold.}}', { count: flagged.length })}`}
            </p>
          </>
        )}
      </div>

      <div className="space-y-2">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h4 className="text-body font-semibold text-fg">{t('Employees with open results')}</h4>
          <label className="flex items-center gap-2 text-body-sm text-fg-muted">
            <input type="checkbox" checked={onlyIssues} onChange={e => setOnlyIssues(e.target.checked)} />
            {t('Only employees with issues')}
          </label>
        </div>
        {results === null && <p role="alert" className="text-body-sm text-danger-fg">{t('The validation results could not be read, so the employees with issues are not listed.')}</p>}
        {results !== null && issues.length === 0 && <p className="text-body-sm text-fg-muted" data-testid="review-no-issues">{t('No employee has an open validation result.')}</p>}
        {issues.length > 0 && (
          <Table caption={t('Employees with open validation results')} density="compact">
            <thead>
              <tr>
                <Th>{t('Employee')}</Th>
                <Th numeric>{t('Errors')}</Th>
                <Th numeric>{t('Warnings')}</Th>
                <Th>{t('What was found')}</Th>
              </tr>
            </thead>
            <tbody>
              {visibleIssues.map(i => (
                <tr key={i.employeeId} data-testid="review-issue-row">
                  <Td>{nameOf(i.employeeId)}</Td>
                  <Td numeric>{fmt.integer(i.errors)}</Td>
                  <Td numeric>{fmt.integer(i.warnings)}</Td>
                  <Td>{i.messages.join(' · ')}</Td>
                </tr>
              ))}
            </tbody>
          </Table>
        )}
        {issues.length > ISSUES_SHOWN && (
          <Button size="sm" onClick={() => setShowAllIssues(v => !v)} aria-expanded={showAllIssues}>
            {showAllIssues ? t('Show fewer') : t('Show all {count} employees', { count: fmt.integer(issues.length) })}
          </Button>
        )}
        {!onlyIssues && flagged.length > 0 && (
          <Table caption={t('Employees whose pay changed most')} density="compact">
            <thead>
              <tr>
                <Th>{t('Employee')}</Th>
                <Th numeric>{t('Prior net')}</Th>
                <Th numeric>{t('Net now')}</Th>
                <Th numeric>{t('Change')}</Th>
              </tr>
            </thead>
            <tbody>
              {flagged.slice(0, ISSUES_SHOWN).map(v => (
                <tr key={v.employeeId}>
                  <Td>{v.employeeName} ({v.employeeCode})</Td>
                  <Td numeric><bdi>{money(v.priorNet)}</bdi></Td>
                  <Td numeric><bdi>{money(v.currentNet)}</bdi></Td>
                  <Td numeric><bdi>{signed(v.netDelta, money)}</bdi></Td>
                </tr>
              ))}
            </tbody>
          </Table>
        )}
        <p className="text-meta text-fg-muted">{t('Open results are cleared, or overridden with a reason, on the Validation tab.')}{' '}
          <button type="button" className="underline" onClick={onOpenValidation}>{t('Open Validation')}</button>
        </p>
      </div>

      <div className="flex flex-wrap items-center gap-3 border-t border-line pt-3">
        {reviewedAtUtc ? (
          <p className="flex items-center gap-2 text-body-sm text-success-fg" data-testid="review-complete">
            <CheckCircle2 className="h-4 w-4" aria-hidden="true" />
            {t('Review marked complete on this device, {when}.', { when: fmt.dateTime(reviewedAtUtc) })}
          </p>
        ) : (
          <>
            <Button variant="primary" onClick={onComplete} data-testid="review-complete-button">{t('Mark review complete')}</Button>
            <p className="text-meta text-fg-muted">{t('This step is recorded on this device only. The server still checks validation and the second approver when you approve.')}</p>
          </>
        )}
      </div>
    </section>
  );
}
