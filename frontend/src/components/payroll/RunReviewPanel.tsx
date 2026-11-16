'use client';

import { useEffect, useMemo, useState } from 'react';
import { CheckCircle2 } from 'lucide-react';
import { payrollApi, type PayrollRun, type PayrollSlip, type PayrollValidationResult } from '../../api/payroll';
import type { ReconDto } from '../../api/payrollRecon';
import { employeesWithIssues } from '../../lib/payrollPipeline';
import { findingText } from '../../lib/validationFindingText';
import type { ReconLoadState } from '../../hooks/useRunRecon';
import { useFormat } from '../../hooks/useFormat';
import { useT } from '../../hooks/useT';
import { Button } from '../ui/Button';
import { Table, Td, Th } from '../ui/Table';
import { ManpowerReconPanel, type ReconTab } from './ManpowerReconPanel';

const ISSUES_SHOWN = 10;

/**
 * The Review stop point. The reviewer reads how the run moved from the prior month (headcount and value
 * reconciliation) and which employees have open validation results, then signs the reconciliation off.
 * The sign-off is kept by the server; Approve is refused without it, so this stage is done only when the
 * server says so.
 */
export function RunReviewPanel({ run, results, recon, reconState, reconError, reloadRecon, candidates, priorRunId, onPriorRunChange, mayApprove, signoffRequired, reviewedAtUtc, reviewedByName, onOpenValidation, onSignedOff }: {
  run: PayrollRun;
  results: PayrollValidationResult[] | null;
  recon: ReconDto | null;
  reconState: ReconLoadState;
  reconError: string;
  reloadRecon: () => void;
  candidates: PayrollRun[];
  priorRunId: string | null;
  onPriorRunChange: (id: string | null) => void;
  mayApprove: boolean;
  /** False when the company's rule does not hold Approve for the sign-off: the review is shown, never waited for. */
  signoffRequired: boolean;
  /** When the Active server sign-off was given; null = not signed off. */
  reviewedAtUtc: string | null;
  reviewedByName: string | null;
  onOpenValidation: () => void;
  onSignedOff?: () => void;
}) {
  const t = useT();
  const fmt = useFormat();
  const [slips, setSlips] = useState<PayrollSlip[] | null>(null);
  const [showAllIssues, setShowAllIssues] = useState(false);
  const [tab, setTab] = useState<ReconTab>('volume');
  useEffect(() => { setTab('volume'); }, [run.id]);

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

  const visibleIssues = showAllIssues ? issues : issues.slice(0, ISSUES_SHOWN);

  // A locked or paid run has nothing left to review: it shows the sign-off summary, and the full reconciliation and the
  // open findings sit under Details for anyone who wants to look back.
  const settled = run.status === 'Locked' || run.status === 'Paid';

  const signoffSummary = reviewedAtUtc ? (
    <p className="flex items-center gap-2 text-body-sm text-success-fg" data-testid="review-complete">
      <CheckCircle2 className="h-4 w-4" aria-hidden="true" />
      {reviewedByName
        ? t('Reconciliation signed off by {name}, {when}.', { name: reviewedByName, when: fmt.dateTime(reviewedAtUtc) })
        : t('Reconciliation signed off, {when}.', { when: fmt.dateTime(reviewedAtUtc) })}
    </p>
  ) : null;

  const reconPanel = (
    <ManpowerReconPanel
      run={run}
      candidates={candidates}
      recon={recon}
      state={reconState}
      error={reconError}
      reload={reloadRecon}
      mayApprove={mayApprove}
      priorRunId={priorRunId}
      onPriorRunChange={onPriorRunChange}
      tab={tab}
      onTab={setTab}
      onSignedOff={onSignedOff}
    />
  );

  const findings = (
    <div className="space-y-2">
      <h4 className="text-body font-semibold text-fg">{t('Employees with open results')}</h4>
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
                <Td>{[...new Set(i.findings.map(f => findingText(t, f)))].join(' · ')}</Td>
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
      <p className="text-meta text-fg-muted">{t('Open results are cleared, or overridden with a reason, on the Validation tab.')}{' '}
        <button type="button" className="underline" onClick={onOpenValidation}>{t('Open Validation')}</button>
      </p>
    </div>
  );

  if (settled) {
    return (
      <section aria-labelledby={`review-${run.id}`} className="space-y-3 rounded-lg border border-line bg-surface-sunken p-4" data-testid="run-review">
        <h3 id={`review-${run.id}`} className="text-h4 font-semibold text-fg">{t('Review')}</h3>
        {signoffSummary ?? <p className="text-body-sm text-fg-muted" data-testid="review-not-required">{t('No reconciliation sign-off is needed for this run')}</p>}
        <details className="rounded-lg border border-line bg-surface">
          <summary className="cursor-pointer px-3 py-2 text-body-sm font-medium text-fg">{t('Details')}</summary>
          <div className="space-y-4 p-3">
            {reconPanel}
            {findings}
          </div>
        </details>
      </section>
    );
  }

  return (
    <section aria-labelledby={`review-${run.id}`} className="space-y-4 rounded-lg border border-line bg-surface-sunken p-4" data-testid="run-review">
      <div>
        <h3 id={`review-${run.id}`} className="text-h4 font-semibold text-fg">{t('Review before approval')}</h3>
        <p className="mt-0.5 text-body-sm text-fg-muted">{signoffRequired
          ? t('Read how this run moved from the prior month, explain what moved, then sign the reconciliation off. Approve is offered once it is signed off.')
          : t('Read how this run moved from the prior month and explain what moved. Your company does not require a sign-off before Approve, so Approve is not held for it. You can still sign the reconciliation off.')}</p>
      </div>

      {reconPanel}

      {findings}

      <div className="flex flex-wrap items-center gap-3 border-t border-line pt-3">
        {signoffSummary ?? (
          <>
            <Button variant={signoffRequired ? 'primary' : 'secondary'} onClick={() => setTab('signoff')} data-testid="review-go-to-signoff">{t('Go to sign-off')}</Button>
            <p className="text-meta text-fg-muted">{signoffRequired
              ? t('The sign-off is kept by the server. Approve is refused until the reconciliation is signed off for this version of the run.')
              : t('The sign-off is optional for this company. It is kept by the server if you give it.')}</p>
          </>
        )}
      </div>
    </section>
  );
}
