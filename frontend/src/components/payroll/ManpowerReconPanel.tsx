'use client';

import { AlertTriangle, Download } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { payrollReconApi, type ReconDto } from '../../api/payrollRecon';
import { saveBlob } from '../../api/saudiBankExports';
import type { PayrollRun } from '../../api/payroll';
import type { ReconLoadState } from '../../hooks/useRunRecon';
import { useT } from '../../hooks/useT';
import { activeSignoff, type DrillTarget } from '../../lib/payrollRecon';
import { requestFailureReason } from '../../lib/requestFailure';
import { Badge } from '../ui/Badge';
import { Button } from '../ui/Button';
import { Select } from '../ui/Field';
import { RovingTabList, TabPanel } from '../ui/RovingTabs';
import { SkeletonRows } from '../ui/Skeleton';
import { FinalSettlementBlock } from './FinalSettlementBlock';
import { ReconDrillDownDrawer } from './ReconDrillDownDrawer';
import { ReconSignoffForm } from './ReconSignoffForm';
import { ValueBridgeTable } from './ValueBridgeTable';
import { VolumeBridgeTable } from './VolumeBridgeTable';
import { usePeriodLabel } from './LockSummary';
import { issueText } from './reconWords';
import { useReconPeriod } from './useReconPeriod';

export type ReconTab = 'volume' | 'value' | 'finalSettlement' | 'signoff';

/**
 * The manpower and value reconciliation of a payroll run against the prior month: headcount (who joined,
 * who left), value (each pay item, by what moved it), final settlements, and the sign-off a person gives
 * before Approve is offered. Loading, failing and "not applicable" are each said, never drawn as an empty bridge.
 */
export function ManpowerReconPanel({ run, candidates, recon, state, error, reload, mayApprove, priorRunId, onPriorRunChange, tab, onTab, onSignedOff }: {
  run: PayrollRun;
  /** The earlier runs that can stand in as the comparison (see priorRunCandidates). */
  candidates: PayrollRun[];
  recon: ReconDto | null;
  state: ReconLoadState;
  error: string;
  reload: () => void;
  mayApprove: boolean;
  priorRunId: string | null;
  onPriorRunChange: (id: string | null) => void;
  tab: ReconTab;
  onTab: (tab: ReconTab) => void;
  onSignedOff?: () => void;
}) {
  const t = useT();
  const period = useReconPeriod();
  const [live, setLive] = useState('');
  const onAnnounce = setLive;
  const [drill, setDrill] = useState<{ target: DrillTarget; title: string } | null>(null);
  const opener = useRef<HTMLElement | null>(null);
  const [exporting, setExporting] = useState<'xlsx' | 'csv' | null>(null);
  const [exportError, setExportError] = useState('');

  // One polite sentence when the reconciliation arrives or fails; never per tab or per keystroke.
  const announcedFor = useRef('');
  useEffect(() => {
    const key = `${run.id}|${state}|${recon?.snapshotHash ?? ''}`;
    if (announcedFor.current === key) return;
    announcedFor.current = key;
    if (state === 'ready' && recon) onAnnounce(t('Reconciliation loaded for {period}.', { period: period(recon.period) }));
    else if (state === 'failed') onAnnounce(t('The reconciliation could not be loaded.'));
  }, [state, recon, run.id, onAnnounce, t]); // eslint-disable-line react-hooks/exhaustive-deps

  const openDrill = (target: DrillTarget, title: string, el: HTMLElement) => { opener.current = el; setDrill({ target, title }); };

  const doExport = async (format: 'xlsx' | 'csv') => {
    setExporting(format); setExportError('');
    try {
      saveBlob(await payrollReconApi.export(run.id, format, priorRunId));
    } catch (e) {
      setExportError(t('The export could not be created. {reason}', { reason: requestFailureReason(e) }));
    } finally { setExporting(null); }
  };

  const partial = recon?.scope.partial === true;
  const signed = activeSignoff(recon?.signoff);
  const tabs: Array<{ id: ReconTab; label: string }> = [
    { id: 'volume', label: t('Volume') },
    { id: 'value', label: t('Value') },
    { id: 'finalSettlement', label: t('Final settlement') },
    { id: 'signoff', label: t('Sign-off') },
  ];

  return (
    <div className="space-y-3" data-testid="manpower-recon">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h4 className="flex flex-wrap items-center gap-2 text-body font-semibold text-fg">
            {t('Manpower and value reconciliation')}
            {recon && (signed ? <Badge tone="success">{t('Signed off')}</Badge> : <Badge tone="warning">{t('Not signed off')}</Badge>)}
          </h4>
          {recon && (
            <p className="text-body-sm text-fg-muted">
              {recon.mode === 'Full' ? t('{period} against {prior}', { period: period(recon.period), prior: period(recon.priorPeriod) }) : t('{period}, with no prior month to compare', { period: period(recon.period) })}
            </p>
          )}
        </div>
        {recon && (
          <div className="flex flex-wrap items-center gap-2">
            <Button size="sm" busy={exporting === 'xlsx'} onClick={() => doExport('xlsx')} data-testid="recon-export-xlsx">
              <Download className="h-4 w-4" aria-hidden="true" /> {partial ? t('Export XLSX (partial)') : t('Export XLSX')}
            </Button>
            <Button size="sm" busy={exporting === 'csv'} onClick={() => doExport('csv')} data-testid="recon-export-csv">
              <Download className="h-4 w-4" aria-hidden="true" /> {partial ? t('Export CSV (partial)') : t('Export CSV')}
            </Button>
          </div>
        )}
      </div>
      {exportError && <p role="alert" className="text-body-sm text-danger-fg">{exportError}</p>}

      {state === 'loading' && !recon && (
        <div data-testid="recon-loading"><p className="sr-only">{t('Loading the reconciliation…')}</p><SkeletonRows rows={4} /></div>
      )}
      {state === 'failed' && (
        <p role="alert" className="text-body-sm text-danger-fg" data-testid="recon-load-failed">
          {t('The reconciliation could not be loaded. {reason}', { reason: error })}{' '}
          <button type="button" className="underline" onClick={reload}>{t('Retry')}</button>
        </p>
      )}
      {state === 'not_applicable' && (
        <p role="status" className="text-body-sm text-fg-muted" data-testid="recon-not-applicable">
          {t('This is an off-cycle run. Only monthly runs are reconciled against the prior month, so there is nothing to sign off before approving it.')}
        </p>
      )}

      {recon && (
        <>
          {partial && (
            <p role="status" className="flex items-start gap-2 rounded-lg border border-warning/30 bg-warning-soft px-3 py-2 text-body-sm text-warning-fg" data-testid="recon-partial-banner">
              <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
              {t('You can see only part of the company, so every figure here is a partial view. It is labelled partial when exported and cannot be signed off.')}
            </p>
          )}
          {recon.mode === 'NoPrior' && (
            <p role="status" className="rounded-lg border border-line bg-surface px-3 py-2 text-body-sm text-fg-muted" data-testid="recon-noprior">
              {recon.lastRunPeriod
                ? t('There is no run for the month before {period}; the last run was {last}. This shows the closing headcount and how pay is made up. A sign-off is still required.', { period: period(recon.period), last: period(recon.lastRunPeriod) })
                : t('There is no earlier run to compare with. This shows the closing headcount and how pay is made up. A sign-off is still required.')}
              {recon.priorVoided && ` ${t('The run for the month before was voided and has not been replaced.')}`}
            </p>
          )}
          {recon.priorNotFinal && (
            <p role="status" className="text-body-sm text-warning-fg" data-testid="recon-prior-not-final">{t('The prior month’s run is not approved yet, so its figures may still change.')}</p>
          )}
          {!recon.integrity.ok && (
            <div role="alert" className="rounded-lg border border-danger/30 bg-danger-soft px-3 py-2 text-body-sm text-danger-fg" data-testid="recon-integrity">
              <p className="font-semibold">{t('Integrity checks failed, so this reconciliation cannot be signed off.')}</p>
              <ul className="list-disc ps-5">
                {recon.integrity.issues.map((i, n) => {
                  const o = typeof i === 'string' ? { code: i, message: undefined } : i;
                  return <li key={n}>{issueText(t, o.code, o.message)}</li>;
                })}
              </ul>
            </div>
          )}

          <ComparisonPicker candidates={candidates} priorRunId={priorRunId} onChange={onPriorRunChange} automaticPeriod={recon.mode === 'Full' && !priorRunId && recon.priorPeriod ? period(recon.priorPeriod) : null} />

          <RovingTabList items={tabs} activeId={tab} onChange={onTab} idPrefix="recon" label={t('Reconciliation sections')} compact />
          <TabPanel idPrefix="recon" tabId={tab}>
            <div className="pt-2">
              {tab === 'volume' && <VolumeBridgeTable recon={recon} onDrill={openDrill} />}
              {tab === 'value' && <ValueBridgeTable recon={recon} onDrill={openDrill} />}
              {tab === 'finalSettlement' && <FinalSettlementBlock recon={recon} onDrill={openDrill} />}
              {tab === 'signoff' && (
                <ReconSignoffForm
                  runId={run.id}
                  runStatus={run.status}
                  recon={recon}
                  mayApprove={mayApprove}
                  priorRunId={priorRunId}
                  onChanged={reload}
                  onSignedOff={onSignedOff}
                  onAnnounce={onAnnounce}
                  onDrill={openDrill}
                />
              )}
            </div>
          </TabPanel>
        </>
      )}

      <ReconDrillDownDrawer
        runId={run.id}
        priorRunId={priorRunId}
        target={drill?.target ?? null}
        title={drill?.title ?? ''}
        subtitle={recon ? t('{period} against {prior}', { period: period(recon.period), prior: period(recon.priorPeriod) }) : undefined}
        currency={recon?.currency ?? null}
        opener={opener.current}
        onClose={() => setDrill(null)}
      />
      <div role="status" aria-live="polite" className="sr-only" data-testid="recon-live">{live}</div>
    </div>
  );
}

/** Compare with the previous month (automatic), or pick an earlier run. Shown only when there is a choice to make. */
function ComparisonPicker({ candidates, priorRunId, onChange, automaticPeriod }: {
  candidates: PayrollRun[];
  priorRunId: string | null;
  onChange: (id: string | null) => void;
  automaticPeriod: string | null;
}) {
  const t = useT();
  const period = usePeriodLabel();
  if (candidates.length === 0) return null;
  return (
    <div className="flex flex-wrap items-center gap-2">
      <label htmlFor="recon-compare-with" className="text-body-sm text-fg-muted">{t('Compare with')}</label>
      <Select id="recon-compare-with" size="sm" className="min-w-[12rem]" value={priorRunId ?? ''} onChange={e => onChange(e.target.value || null)}>
        <option value="">{automaticPeriod ? t('The previous month ({period})', { period: automaticPeriod }) : t('The previous month')}</option>
        {candidates.map(c => <option key={c.id} value={c.id}>{period(c)}</option>)}
      </Select>
    </div>
  );
}
