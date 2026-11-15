'use client';
import { lazy, Suspense, useEffect, useState } from 'react';
import { AlertTriangle, Landmark } from 'lucide-react';
import { useAuth } from '../../contexts/AuthContext';
import { useT } from '../../hooks/useT';
import { saudiBankExportsApi } from '../../api/saudiBankExports';
import { bankFileProductionAllowed } from '../../lib/payrollPaymentAccess';
import { requestFailureReason } from '../../lib/requestFailure';
import { Skeleton } from '../ui/Skeleton';
import type { BankFlowProgress } from '../../lib/bankFileSteps';
import type { GuideSlots } from './BankFileGuide';

// The server allow-list, not a browser flag, owns activation. Old APIs, denied requests and failed probes keep this additive feature
// out of the way of existing payroll, but never silently: a Saudi batch says why its bank-file panel is not there and what to do next.
// The panel is split from the page; while it arrives the guided flow is already drawn, with this skeleton in step 2.
const Panel = lazy(() => import('./SaudiBankExportPanel').then(m => ({ default: m.SaudiBankExportPanel })));

type Probe = { batchId: string; state: 'enabled' | 'off' } | { batchId: string; state: 'failed'; error: unknown };

function GateSkeleton() {
  const t = useT();
  return (
    <section className="surface space-y-3 p-4" aria-busy="true" data-testid="bank-file-checking">
      <p role="status" className="sr-only">{t('Checking whether the bank file can be prepared…')}</p>
      <Skeleton className="h-5 w-2/3" />
      <Skeleton className="h-4 w-full" />
      <Skeleton className="h-24 w-full" />
    </section>
  );
}

/** Why the bank-file panel is not here: a named cause and the next step, never an empty gap. */
function GateReason({ testId, title, children, action }: { testId: string; title: string; children: React.ReactNode; action?: React.ReactNode }) {
  return (
    <section role="note" data-testid={testId} className="surface flex items-start gap-3 p-4">
      <Landmark className="mt-0.5 h-5 w-5 shrink-0 text-fg-muted" aria-hidden="true" />
      <div className="min-w-0 flex-1">
        <h3 className="text-body font-semibold text-fg">{title}</h3>
        <p className="mt-1 text-body-sm text-fg-muted">{children}</p>
        {action && <div className="mt-2">{action}</div>}
      </div>
    </section>
  );
}

export function SaudiBankExportGate({ batchId, employeeIds, employeeLabels, isSaudi = false, onProgress, suggestedReference, renderGuide }: {
  /** Draws the guided flow; the gate says why the bank file is not there in step 2 of it, and the panel fills the steps when it is. */
  renderGuide: (slots: GuideSlots) => React.ReactNode;
  batchId: string;
  employeeIds: number[];
  employeeLabels?: Record<number, string>;
  /** A Saudi batch always says why the panel is missing; any other batch keeps the old behaviour (nothing). */
  isSaudi?: boolean;
  /** The step flow above the panel follows what the gate and the panel report. */
  onProgress?: (p: BankFlowProgress) => void;
  /** Starting value for the bank's batch number (see suggestedBatchReference). */
  suggestedReference?: string;
}) {
  const t = useT();
  const { hasPermission } = useAuth();
  // The Payroll Manager prepares the file (payroll.payment_batch.manage); payroll.export keeps working.
  const permitted = bankFileProductionAllowed(hasPermission);
  const [probe, setProbe] = useState<Probe | null>(null);
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    setProbe(null);
    if (!permitted) return;
    const ac = new AbortController();
    saudiBankExportsApi.availability(batchId, { signal: ac.signal })
      .then(value => { if (!ac.signal.aborted) setProbe({ batchId, state: value.enabled === true ? 'enabled' : 'off' }); })
      .catch(error => { if (!ac.signal.aborted) setProbe({ batchId, state: 'failed', error }); });
    return () => ac.abort();
  }, [batchId, permitted, attempt]);

  const current = probe && probe.batchId === batchId ? probe : null;
  const gate: BankFlowProgress['gate'] = !permitted ? 'no_permission' : !current ? 'checking' : current.state === 'enabled' ? 'ready' : current.state === 'off' ? 'off' : 'failed';
  useEffect(() => { if (gate !== 'ready') onProgress?.({ gate, validation: 'none', generated: false }); }, [gate, onProgress]);

  if (gate === 'ready') {
    return (
      <Suspense fallback={<>{renderGuide({ steps: { check: { body: <GateSkeleton /> } } })}</>}>
        <Panel key={batchId} batchId={batchId} employeeIds={employeeIds} employeeLabels={employeeLabels} onProgress={onProgress} suggestedReference={suggestedReference} renderGuide={renderGuide} />
      </Suspense>
    );
  }
  if (!isSaudi) return null;
  // Step 2 is where the bank file is prepared, so it is where its absence is explained.
  const inStep = (body: React.ReactNode) => <>{renderGuide({ steps: { check: { body } } })}</>;
  if (gate === 'checking') return inStep(<GateSkeleton />);
  if (gate === 'no_permission') {
    return inStep(
      <GateReason testId="bank-file-no-permission" title={t('You cannot prepare the bank file')}>
        {t('Preparing the Saudi bank file needs the Payroll export permission. Ask an administrator to give it to your role, then come back to this batch.')}
      </GateReason>,
    );
  }
  if (gate === 'off') {
    return inStep(
      <GateReason testId="bank-file-off" title={t('Bank file export is switched off for this company')}>
        {t('The platform team switches bank file export on for each company. Ask them to turn it on, then reload this batch.')}
      </GateReason>,
    );
  }
  return inStep(
    <GateReason
      testId="bank-file-check-failed"
      title={t('Could not check whether the bank file can be prepared')}
      action={<button type="button" className="btn-secondary btn-sm" onClick={() => setAttempt(n => n + 1)}>{t('Retry')}</button>}
    >
      <AlertTriangle className="me-1 inline h-4 w-4 align-text-bottom text-warning-fg" aria-hidden="true" />
      {current && current.state === 'failed' ? t(requestFailureReason(current.error)) : ''}
    </GateReason>,
  );
}
