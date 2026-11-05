'use client';

import { CheckCircle2 } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { payrollReconApi, type ReconDto, type ReconEmployeeRow } from '../../api/payrollRecon';
import { useFormat } from '../../hooks/useFormat';
import { useT } from '../../hooks/useT';
import {
  MIN_EXPLANATION_LENGTH, activeSignoff, buildSignoffRequest, employeeReasonCategories, explanationId, flaggedItems, parseReconError,
  requiredExplanations, signedAmount, signoffGate, type DrillTarget,
} from '../../lib/payrollRecon';
import { requestFailureReason } from '../../lib/requestFailure';
import { Badge } from '../ui/Badge';
import { Button } from '../ui/Button';
import { Textarea } from '../ui/Field';
import { blockerText, categoryName, scopeReasonText, supersededText } from './reconWords';

type T = ReturnType<typeof useT>;

const testKey = (scope: string, key: string) => `${scope}-${key.replace(':', '-')}`;
const nameOf = (t: T, r: ReconEmployeeRow) => (r.employeeName ? `${r.employeeName}${r.employeeCode ? ` (${r.employeeCode})` : ''}` : t('Employee {id}', { id: r.employeeId }));

/** Everything a person must do before the reconciliation can be signed off, and the sign-off itself. */
export function ReconSignoffForm({ runId, runStatus, recon, mayApprove, priorRunId, onChanged, onSignedOff, onAnnounce, onDrill }: {
  runId: string;
  runStatus: string;
  recon: ReconDto;
  mayApprove: boolean;
  priorRunId: string | null;
  /** Re-read the reconciliation (after a sign-off, a withdrawal, or a refusal that means the figures moved). */
  onChanged: () => void;
  /** A sign-off was recorded: the review stays open so the signer sees it. */
  onSignedOff?: () => void;
  onAnnounce: (text: string) => void;
  onDrill: (target: DrillTarget, title: string, opener: HTMLElement) => void;
}) {
  const t = useT();
  const fmt = useFormat();
  const signed = activeSignoff(recon.signoff);
  const stamp = `${recon.processVersion ?? ''}|${recon.snapshotHash ?? ''}`;

  const [drafts, setDrafts] = useState<Record<string, string>>({});
  const [notes, setNotes] = useState('');
  const [affirmed, setAffirmed] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [attempt, setAttempt] = useState(0);

  // Who needs a reason (exits nobody could explain) and how many employees' pay moved beyond the threshold.
  const reasonCategories = useMemo(() => employeeReasonCategories(recon.volume), [recon.volume]);
  const [people, setPeople] = useState<ReconEmployeeRow[] | null>(null);
  const [peopleFailed, setPeopleFailed] = useState(false);
  const [flagged, setFlagged] = useState<{ total: number | null; rows: ReconEmployeeRow[]; loading: boolean }>({ total: null, rows: [], loading: true });

  const needsInputs = !signed && recon.mode !== 'NotApplicable';
  useEffect(() => {
    if (!needsInputs) return;
    let live = true;
    setPeopleFailed(false);
    if (reasonCategories.length === 0) setPeople([]);
    else {
      setPeople(null);
      Promise.all(reasonCategories.map(async category => {
        const rows: ReconEmployeeRow[] = [];
        // Read every page (the API serves at most 100 rows a page); the cap only stops a server that never reports its total.
        for (let page = 1; page <= 500; page++) {
          const r = await payrollReconApi.employees(runId, { section: 'volume', category, page, pageSize: 100, ...(priorRunId ? { priorRunId } : {}) });
          rows.push(...r.items.map(i => ({ ...i, category: i.category ?? category })));
          if (rows.length >= r.total || r.items.length === 0) break;
        }
        return rows;
      })).then(all => { if (live) setPeople(all.flat()); }).catch(() => { if (live) { setPeople(null); setPeopleFailed(true); } });
    }
    if (recon.mode !== 'Full') setFlagged({ total: 0, rows: [], loading: false });
    else {
      setFlagged(f => ({ ...f, total: null, loading: true }));
      payrollReconApi.employees(runId, { section: 'value', bucket: 'flagged', page: 1, pageSize: 50, ...(priorRunId ? { priorRunId } : {}) })
        .then(r => { if (live) setFlagged({ total: r.total, rows: r.items, loading: false }); })
        .catch(() => { if (live) setFlagged({ total: null, rows: [], loading: false }); });
    }
    return () => { live = false; };
  }, [runId, priorRunId, stamp, reasonCategories, needsInputs, recon.mode, attempt]);

  const employeeIds = useMemo(() => Array.from(new Set((people ?? []).map(p => p.employeeId))), [people]);
  const required = useMemo(() => requiredExplanations(recon.value, employeeIds), [recon.value, employeeIds]);
  const gate = signoffGate({
    recon, runStatus, mayApprove, required, drafts,
    flaggedEmployeeCount: flagged.total, flaggedAffirmed: affirmed,
    employeesKnown: people !== null,
  });
  const blocker = gate.blocker && gate.blocker !== 'already_signed' ? gate.blocker : null;
  const items = flaggedItems(recon.value);
  const money = (n: number) => fmt.money(n, recon.currency);

  const setDraft = (scope: 'Item' | 'Employee', key: string, text: string) => setDrafts(d => ({ ...d, [explanationId(scope, key)]: text }));

  const submit = async () => {
    if (!gate.canSubmit || busy) return;
    setBusy(true); setError('');
    try {
      const body = buildSignoffRequest({ recon, required, drafts, acknowledgedFlaggedEmployeeCount: flagged.total ?? 0, notes, priorRunId });
      const result = await payrollReconApi.signoff(runId, body);
      onAnnounce(result?.signedOffByName ? t('The reconciliation was signed off by {name}.', { name: result.signedOffByName }) : t('The reconciliation was signed off.'));
      onSignedOff?.();
      onChanged();
    } catch (e) {
      const err = parseReconError(e);
      switch (err.code) {
        case 'signoff_stale':
          setError(t('This run changed since you opened it, so the figures were reloaded. Read them again, then sign off.')); setAffirmed(false); onChanged(); break;
        case 'variance_explanation_required':
          setError(t('The server still needs a written reason for: {keys}.', { keys: err.missingKeys.join(', ') || '—' })); break;
        case 'flagged_employees_not_acknowledged':
          setError(t('The number of employees whose pay changed is different now. Confirm the new count, then sign off again.')); setAffirmed(false); setAttempt(n => n + 1); break;
        case 'recon_integrity_failed':
          setError(t('The reconciliation does not tie, so it cannot be signed off.')); onChanged(); break;
        case 'run_processing':
          setError(t('The run is being processed. Wait for it to finish, then sign off.')); break;
        case 'run_not_signable':
          setError(t('A run can be signed off only while it is Processed or waiting for finance review.')); onChanged(); break;
        case 'maker_checker_violation':
          setError(t('You created or processed this run, so you cannot sign off its reconciliation. Another approver must.')); break;
        case 'signoff_requires_organization_scope':
          setError(t('Sign-off needs access to the whole company.')); break;
        case 'recon_not_applicable':
          setError(t('Off-cycle runs are not reconciled, so there is nothing to sign off.')); break;
        default:
          setError(t('The sign-off could not be recorded. {reason}', { reason: err.message ?? requestFailureReason(e) }));
      }
    } finally { setBusy(false); }
  };

  // ── Withdraw ────────────────────────────────────────────────────────────────
  const [withdrawing, setWithdrawing] = useState(false);
  const [withdrawReason, setWithdrawReason] = useState('');
  const [withdrawBusy, setWithdrawBusy] = useState(false);
  const [withdrawError, setWithdrawError] = useState('');
  const withdraw = async () => {
    if (withdrawReason.trim().length < MIN_EXPLANATION_LENGTH || withdrawBusy) return;
    setWithdrawBusy(true); setWithdrawError('');
    try {
      await payrollReconApi.withdraw(runId, withdrawReason.trim());
      setWithdrawing(false); setWithdrawReason('');
      onAnnounce(t('The sign-off was withdrawn. The run needs to be reviewed and signed off again before it can be approved.'));
      onChanged();
    } catch (e) {
      setWithdrawError(t('The sign-off could not be withdrawn. {reason}', { reason: parseReconError(e).message ?? requestFailureReason(e) }));
    } finally { setWithdrawBusy(false); }
  };

  if (signed) {
    const reasons = (recon.value.sections ?? []).flatMap(s => s.items.filter(i => i.explanation).map(i => ({ key: `${s.kind}-${i.code}`, label: i.label, text: i.explanation as string })));
    return (
      <div className="space-y-3">
        <p className="flex items-start gap-2 text-body text-success-fg" data-testid="recon-signoff-status">
          <CheckCircle2 className="mt-0.5 h-5 w-5 shrink-0" aria-hidden="true" />
          <span>{t('Signed off by {name}', { name: signed.signedOffByName })} · <bdi>{fmt.dateTime(signed.signedOffAtUtc)}</bdi></span>
        </p>
        <p className="text-body-sm text-fg-muted">{t('Approve is now available. If the run is processed again, reopened, sent back or voided, this sign-off stops counting and a new one is needed.')}</p>
        {reasons.length > 0 && (
          <div>
            <h5 className="text-body font-semibold text-fg">{t('Reasons recorded')}</h5>
            <ul className="mt-1 list-disc ps-5 text-body-sm text-fg-muted">
              {reasons.map(r => <li key={r.key}><span className="font-medium text-fg">{r.label}</span>: {r.text}</li>)}
            </ul>
          </div>
        )}
        {mayApprove && recon.scope.canSignOff && !withdrawing && (
          <Button size="sm" onClick={() => setWithdrawing(true)}>{t('Withdraw sign-off')}</Button>
        )}
        {withdrawing && (
          <div className="space-y-2 rounded-lg border border-line p-3">
            <label className="block text-body-sm font-medium text-fg" htmlFor="recon-withdraw-reason">{t('Reason for withdrawing')}</label>
            <Textarea id="recon-withdraw-reason" rows={2} className="w-full max-w-2xl" value={withdrawReason} onChange={e => setWithdrawReason(e.target.value)} aria-describedby="recon-withdraw-hint" />
            <p id="recon-withdraw-hint" className="text-meta text-fg-muted">{recon.signoffRequired !== false
              ? t('At least {min} characters. Approve is withheld until the reconciliation is signed off again.', { min: MIN_EXPLANATION_LENGTH })
              : t('At least {min} characters. The reason is kept with the withdrawal.', { min: MIN_EXPLANATION_LENGTH })}</p>
            {withdrawError && <p role="alert" className="text-body-sm text-danger-fg">{withdrawError}</p>}
            <div className="flex gap-2">
              <Button size="sm" variant="danger" busy={withdrawBusy} disabled={withdrawReason.trim().length < MIN_EXPLANATION_LENGTH} onClick={withdraw} data-testid="recon-withdraw-confirm">{t('Withdraw sign-off')}</Button>
              <Button size="sm" variant="ghost" onClick={() => { setWithdrawing(false); setWithdrawError(''); }}>{t('Cancel')}</Button>
            </div>
          </div>
        )}
      </div>
    );
  }

  const superseded = recon.signoff && recon.signoff.status === 'Superseded' ? recon.signoff : null;
  const flaggedCount = flagged.total;

  return (
    <div className="space-y-5">
      {superseded && <p className="rounded-lg border border-line bg-surface-sunken px-3 py-2 text-body-sm text-fg-muted" data-testid="recon-signoff-superseded">{supersededText(t, superseded.supersededReason, superseded.signedOffByName)}</p>}
      <p className="text-body-sm text-fg-muted">
        {recon.mode === 'NoPrior'
          ? t('There is no prior month to explain movement against. Sign off to confirm you have read this month’s headcount and pay composition.')
          : recon.signoffRequired !== false
            ? t('Signing off records that you read this reconciliation against the prior month and explained what moved. Approve is withheld until it is signed.')
            : t('Signing off records that you read this reconciliation against the prior month and explained what moved. Your company does not require it before Approve.')}
      </p>

      {items.length > 0 && (
        <fieldset className="space-y-3">
          <legend className="text-body font-semibold text-fg">{t('Why the larger movements happened')}</legend>
          <p className="text-meta text-fg-muted">{t('The change among continuing staff on these lines was more than {pct} of the prior amount or {amount}.', { pct: fmt.percent(recon.thresholds.pct, 1), amount: money(recon.thresholds.minAmount) })}</p>
          {items.map(i => {
            const id = `recon-reason-${testKey('Item', i.key)}`;
            return (
              <div key={i.key} className="space-y-1">
                <label htmlFor={id} className="block text-body-sm font-medium text-fg">
                  {t('Why did {item} move?', { item: i.label })}{' '}
                  <span className="font-normal text-fg-muted"><bdi>{money(i.prior)} → {money(i.current)} ({signedAmount(i.delta, v => money(v))})</bdi></span>
                </label>
                <Textarea id={id} rows={2} className="w-full max-w-2xl" data-testid={`recon-explanation-${testKey('Item', i.key)}`} value={drafts[explanationId('Item', i.key)] ?? ''} onChange={e => setDraft('Item', i.key, e.target.value)} aria-describedby="recon-reason-hint" />
              </div>
            );
          })}
        </fieldset>
      )}

      {(reasonCategories.length > 0 || peopleFailed) && (
        <fieldset className="space-y-3">
          <legend className="text-body font-semibold text-fg">{t('Employees paid last month and not this month, with no reason on record')}</legend>
          {peopleFailed && (
            <p role="alert" className="text-body-sm text-danger-fg">
              {t('The employees who need a reason could not be listed.')} <button type="button" className="underline" onClick={() => setAttempt(n => n + 1)}>{t('Retry')}</button>
            </p>
          )}
          {people === null && !peopleFailed && <p className="text-body-sm text-fg-muted">{t('Loading…')}</p>}
          {(people ?? []).map(p => {
            const id = `recon-reason-${testKey('Employee', String(p.employeeId))}`;
            return (
              <div key={p.employeeId} className="space-y-1" data-testid={`recon-employee-reason-${p.employeeId}`}>
                <label htmlFor={id} className="block text-body-sm font-medium text-fg">
                  {t('Why is {employee} not paid this month?', { employee: nameOf(t, p) })}{' '}
                  {p.category && <Badge tone="warning">{categoryName(t, p.category)}</Badge>}
                </label>
                <Textarea id={id} rows={2} className="w-full max-w-2xl" data-testid={`recon-explanation-${testKey('Employee', String(p.employeeId))}`} value={drafts[explanationId('Employee', String(p.employeeId))] ?? ''} onChange={e => setDraft('Employee', String(p.employeeId), e.target.value)} aria-describedby="recon-reason-hint" />
              </div>
            );
          })}
        </fieldset>
      )}

      {(items.length > 0 || reasonCategories.length > 0) && (
        <p id="recon-reason-hint" className="text-meta text-fg-muted">{t('Each reason needs at least {min} characters and is kept with the sign-off.', { min: MIN_EXPLANATION_LENGTH })}</p>
      )}

      {recon.mode === 'Full' && (
        <div className="space-y-2" data-testid="recon-flagged-employees">
          <h5 className="text-body font-semibold text-fg">{t('Employees whose pay changed')}</h5>
          {flagged.loading && <p className="text-body-sm text-fg-muted">{t('Loading…')}</p>}
          {!flagged.loading && flaggedCount === null && (
            <p role="alert" className="text-body-sm text-danger-fg">
              {t('The number of employees whose pay changed could not be read.')} <button type="button" className="underline" onClick={() => setAttempt(n => n + 1)}>{t('Retry')}</button>
            </p>
          )}
          {flaggedCount === 0 && <p className="text-body-sm text-fg-muted">{t('No employee’s pay changed by more than the threshold.')}</p>}
          {flaggedCount !== null && flaggedCount > 0 && (
            <>
              <p className="text-body-sm text-fg-muted">
                {t('Pay changed by more than {pct} or {amount} for {count, plural, one {# employee} other {# employees}}. Read the list before you confirm the count.', { pct: fmt.percent(recon.thresholds.pct, 1), amount: money(recon.thresholds.minAmount), count: flaggedCount })}
              </p>
              <Button size="sm" onClick={e => onDrill({ section: 'value', flagged: true }, t('Employees whose pay changed beyond the threshold'), e.currentTarget)} data-testid="recon-show-flagged">{t('Show the employees')}</Button>
              <label className="flex items-start gap-2 text-body-sm text-fg">
                <input type="checkbox" className="mt-1" checked={affirmed} onChange={e => setAffirmed(e.target.checked)} data-testid="recon-ack-flagged" />
                <span>{t('I have reviewed the list and confirm that {count, plural, one {# employee’s pay} other {# employees’ pay}} changed by more than the threshold.', { count: flaggedCount })}</span>
              </label>
            </>
          )}
        </div>
      )}

      <div className="space-y-1">
        <label htmlFor="recon-signoff-notes" className="block text-body-sm font-medium text-fg">{t('Notes (optional)')}</label>
        <Textarea id="recon-signoff-notes" rows={2} className="w-full max-w-2xl" value={notes} onChange={e => setNotes(e.target.value)} />
      </div>

      {recon.scope.reasons.length > 0 && !recon.scope.canSignOff && (
        <ul className="list-disc ps-5 text-meta text-fg-muted">{recon.scope.reasons.map(r => <li key={r}>{scopeReasonText(t, r)}</li>)}</ul>
      )}

      <div className="space-y-2 border-t border-line pt-3">
        {blocker && <p id="recon-signoff-blocker" className="text-body-sm text-fg-muted" data-testid="recon-signoff-blocker">{blockerText(t, blocker, { min: MIN_EXPLANATION_LENGTH, flagged: flaggedCount })}</p>}
        {error && <p role="alert" className="text-body-sm text-danger-fg" data-testid="recon-signoff-error">{error}</p>}
        <Button variant="primary" disabled={!gate.canSubmit} busy={busy} onClick={submit} aria-describedby={blocker ? 'recon-signoff-blocker' : undefined} data-testid="recon-signoff-button">
          {busy ? t('Signing off…') : t('Sign off the reconciliation')}
        </Button>
      </div>
    </div>
  );
}
