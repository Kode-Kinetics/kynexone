'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import { AlertTriangle, CheckCircle2, Clock3, RefreshCw } from 'lucide-react';
import { ReleaseAGate } from '../components/releaseA/ReleaseAGate';
import { RequestUsageDialog } from '../components/entitlements/RequestUsageDialog';
import { PageHeader } from '../components/ui/Page';
import { Badge } from '../components/ui/Badge';
import { EmptyState } from '../components/ui/EmptyState';
import { Field, Input, Textarea } from '../components/ui/Field';
import { useAuth } from '../contexts/AuthContext';
import { useLocale } from '../contexts/LocaleContext';
import { benefitUsageApi, type UsageRequest, type UtilisationRow } from '../api/benefitUsage';
import { STATUS_TONES, calendarDate, fill, measureText, refusalKey, statusKey } from '../lib/benefitUsage';

/**
 * Benefit usage, the way loans work: for each employee's FIXED contract benefits, what this period allows, what is waiting, what is
 * approved and what is left (education per child as money, tickets as counts). HR requests a use for an employee; Finance approves
 * or rejects it (never their own request, never their own benefit); a Pending request can be withdrawn. Nothing here pays anyone.
 * Behind release_a and entitlements.read or benefits.usage.approve.
 */
export function BenefitUsagePage() {
  return (
    <ReleaseAGate>
      <BenefitUsage />
    </ReleaseAGate>
  );
}

const todayIso = () => new Date().toISOString().slice(0, 10);

function BenefitUsage() {
  const { t, locale } = useLocale();
  const { hasPermission } = useAuth();
  const canRequest = hasPermission('entitlements.manage');
  const [rows, setRows] = useState<UtilisationRow[]>([]);
  const [requests, setRequests] = useState<UsageRequest[]>([]);
  const [loading, setLoading] = useState(true);
  const [failed, setFailed] = useState(false);
  const [search, setSearch] = useState('');
  const [requestFor, setRequestFor] = useState<number | null>(null);
  const [notice, setNotice] = useState('');

  const load = useCallback(async () => {
    setLoading(true);
    setFailed(false);
    try {
      const [u, r] = await Promise.all([benefitUsageApi.utilisation(), benefitUsageApi.requests()]);
      setRows(u);
      setRequests(r);
    } catch {
      setFailed(true);
    } finally {
      setLoading(false);
    }
  }, []);
  useEffect(() => { void load(); }, [load]);

  const employeeRows = useMemo(() => rows.filter((r) => r.employeeId === requestFor), [rows, requestFor]);
  const today = useMemo(todayIso, []);
  const needle = search.trim().toLowerCase();
  const shown = useMemo(() => rows.filter((r) => !needle || r.employeeName.toLowerCase().includes(needle) || (r.employeeCode ?? '').toLowerCase().includes(needle)
    || r.labelEn.toLowerCase().includes(needle) || r.labelAr.includes(search.trim())), [rows, needle, search]);
  const waiting = requests.filter((i) => i.status === 'Pending');
  const recent = requests.filter((i) => i.status !== 'Pending').slice(0, 50);
  const label = (x: { labelEn: string; labelAr: string }) => (locale === 'ar' ? x.labelAr : x.labelEn);
  const done = (message: string) => { setNotice(message); void load(); };

  return (
    <div className="space-y-6">
      <PageHeader title={t('Benefit usage')}
        purpose={t('See what each employee’s fixed benefits allow this period, what is used and what is left. HR requests a use; Finance approves it.')} />
      {notice && <p role="status" className="flex items-center gap-2 rounded-lg bg-success-soft px-3 py-2 text-sm text-success-fg">
        <CheckCircle2 className="h-4 w-4 shrink-0" aria-hidden="true" />{notice}</p>}
      {loading
        ? <div className="space-y-3" aria-busy="true">{[0, 1, 2].map((i) => <div key={i} className="h-16 rounded-xl bg-surface-sunken" />)}</div>
        : failed
          ? <div role="alert" className="flex flex-col items-start gap-2 rounded-xl border border-line bg-surface p-4">
            <p className="flex items-center gap-2 text-sm text-fg"><AlertTriangle className="h-4 w-4 text-warning-fg" aria-hidden="true" />{t('Benefit usage could not be loaded.')}</p>
            <button type="button" className="btn-secondary" onClick={() => void load()}><RefreshCw className="h-4 w-4" aria-hidden="true" />{t('Try again')}</button>
          </div>
          : <>
            {waiting.length > 0 && (
              <section aria-labelledby="usage-waiting" className="space-y-3" data-testid="usage-waiting">
                <h2 id="usage-waiting" className="text-h3 text-fg">{fill(t('Waiting for Finance ({n})'), { n: waiting.length })}</h2>
                <ul className="space-y-3">{waiting.map((r) => <RequestCard key={r.id} request={r} onDone={done} />)}</ul>
              </section>
            )}
            <section aria-labelledby="usage-utilisation" className="space-y-3">
              <div className="flex flex-col gap-3 sm:flex-row sm:items-end sm:justify-between">
                <h2 id="usage-utilisation" className="text-h3 text-fg">{t('Entitlement, used and left')}</h2>
                <Field label={t('Find an employee or benefit')} noInfo="a search box" className="sm:w-72">
                  <Input type="search" value={search} onChange={(e) => setSearch(e.target.value)} />
                </Field>
              </div>
              {rows.length === 0
                ? <EmptyState kind="first-use" title={t('No fixed benefits to show')}
                  description={t('Tickets, education and other counted benefits appear here once an employee’s package is fixed for the contract year.')}
                  action={{ href: '/benefits/by-grade', label: t('Open Benefits by grade') }} />
                : shown.length === 0
                  ? <EmptyState kind="filtered" title={t('Nothing matches the search')} description={fill(t('Search: {text}'), { text: search })} action={{ onClick: () => setSearch('') }} />
                  : <ul className="divide-y divide-line overflow-hidden rounded-xl border border-line bg-surface" data-testid="usage-rows">
                    {shown.map((r) => (
                      <li key={`${r.employeeId}-${r.benefitCode}`} className="grid gap-3 px-4 py-3 md:grid-cols-[minmax(0,2fr)_minmax(0,3fr)_auto] md:items-center" data-testid="usage-row">
                        <div className="min-w-0">
                          <p className="truncate text-sm font-medium text-fg">{r.employeeName}{r.employeeCode && <span className="text-fg-muted"> · {r.employeeCode}</span>}</p>
                          <p className="text-sm text-fg">{label(r)}</p>
                          <p className="text-xs text-fg-muted">{fill(t('{from} to {to}'), { from: calendarDate(r.periodStart, locale), to: calendarDate(r.periodEnd, locale) })}</p>
                        </div>
                        <UsageFigures row={r} />
                        <div className="flex md:justify-end">
                          {canRequest && r.canRequest && (
                            <button type="button" className="btn-secondary" onClick={() => setRequestFor(r.employeeId)}
                              aria-label={fill(t('Request a benefit for {name}'), { name: r.employeeName })}>{t('Request use')}</button>
                          )}
                        </div>
                      </li>
                    ))}
                  </ul>}
            </section>
            {recent.length > 0 && (
              <section aria-labelledby="usage-recent" className="space-y-3">
                <h2 id="usage-recent" className="text-h3 text-fg">{t('Decided requests')}</h2>
                <ul className="space-y-3" data-testid="usage-recent">{recent.map((r) => <RequestCard key={r.id} request={r} onDone={done} />)}</ul>
              </section>
            )}
          </>}
      <RequestUsageDialog open={requestFor !== null} rows={employeeRows} today={today} onClose={() => setRequestFor(null)}
        onSubmit={async (input) => {
          const created = await benefitUsageApi.request(input);
          setRequestFor(null);
          done(fill(t('Requested for {name}. It now waits for Finance.'), { name: created.employeeName }));
        }} />
    </div>
  );
}

/** Entitled, used (waiting + approved) and left, with each child or traveller under it. Shared with My benefits. */
export function UsageFigures({ row }: { row: UtilisationRow }) {
  const { t, locale } = useLocale();
  const m = (v: number) => measureText(v, row.measure, row.currency, locale);
  return (
    <div className="min-w-0 space-y-1">
      <dl className="grid grid-cols-3 gap-2 text-sm">
        <div><dt className="text-xs text-fg-muted">{t('Entitled')}</dt><dd className="tabular-nums text-fg">{m(row.entitlement)}</dd></div>
        <div><dt className="text-xs text-fg-muted">{t('Used')}</dt>
          <dd className="tabular-nums text-fg">{m(row.pending + row.approved)}</dd>
          {row.pending > 0 && <dd className="text-xs text-fg-muted">{fill(t('{amount} waiting for Finance'), { amount: m(row.pending) })}</dd>}</div>
        <div><dt className="text-xs text-fg-muted">{t('Left to use')}</dt><dd className="font-semibold tabular-nums text-fg">{m(row.remaining)}</dd></div>
      </dl>
      {row.beneficiaries.length > 0 && (
        <p className="text-xs text-fg-muted">{row.beneficiaries.map((b) => fill(t('{name}: {left} left of {cap}'), {
          name: b.dependantId ? b.name : fill(t('{name} (employee)'), { name: b.name }), left: m(b.remaining), cap: m(b.cap) })).join(' · ')}</p>
      )}
      {row.verificationState !== 'Verified' && <Badge tone="warning"><AlertTriangle className="me-1 inline h-3 w-3" aria-hidden="true" />{t('Not confirmed against the contract')}</Badge>}
    </div>
  );
}

/** One request: what, for whom, the evidence, who asked, and the step the viewer may take (Approve / Reject, Withdraw). */
function RequestCard({ request: r, onDone }: { request: UsageRequest; onDone: (message: string) => void }) {
  const { t, locale } = useLocale();
  const { hasPermission } = useAuth();
  // The server says whether this request may be decided by this person; the controls also need the Finance key of their own
  // (benefits.usage.approve), which HR Manager does not hold, so a stale or wrong answer never offers HR an approve button.
  const canDecide = r.canDecide && hasPermission('benefits.usage.approve');
  const [asking, setAsking] = useState<'reject' | 'cancel' | null>(null);
  const [text, setText] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const value = r.units != null ? measureText(r.units, 'Units', null, locale) : measureText(r.amount ?? 0, 'Amount', r.currency, locale);
  const run = async (action: () => Promise<unknown>, message: string) => {
    setBusy(true);
    setError('');
    try { await action(); setAsking(null); setText(''); onDone(message); } catch (e) { setError(t(refusalKey(e))); } finally { setBusy(false); }
  };
  const confirmAsk = () => {
    if (!text.trim()) { setError(t('Say why.')); return; }
    if (asking === 'reject') void run(() => benefitUsageApi.decide(r.id, 'Rejected', text.trim()), t('Rejected.'));
    else void run(() => benefitUsageApi.cancel(r.id, text.trim()), t('Withdrawn. The entitlement is available again.'));
  };
  const details = [
    r.dependantName && fill(t('For {name}'), { name: r.dependantName }),
    r.academicYear && fill(t('Academic year {year}'), { year: r.academicYear }),
    r.providerName,
  ].filter(Boolean).join(' · ');
  return (
    <li className="space-y-2 rounded-xl border border-line bg-surface p-4" data-testid="usage-request">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0">
          <p className="text-sm font-medium text-fg">{r.employeeName} · {locale === 'ar' ? r.labelAr : r.labelEn}</p>
          <p className="text-sm tabular-nums text-fg">{value} · {calendarDate(r.usageDate, locale)}</p>
          {details && <p className="text-xs text-fg-muted">{details}</p>}
          <p className="text-xs text-fg-muted">{fill(t('Evidence: {ref}'), { ref: r.evidenceReference ?? '—' })} · {r.requestedByName
            ? fill(t('Requested by {name}'), { name: r.requestedByName }) : t('Requested by a former user')}</p>
          {r.decisionReason && <p className="text-xs text-fg-muted">{fill(t('Reason: {text}'), { text: r.decisionReason })}</p>}
        </div>
        <Badge tone={STATUS_TONES[r.status]}>{r.status === 'Pending' && <Clock3 className="me-1 inline h-3 w-3" aria-hidden="true" />}{t(statusKey(r.status))}</Badge>
      </div>
      {asking
        ? <div className="space-y-2">
          <Field label={t('Reason (required)')} noInfo="a short free-text reason">
            <Textarea value={text} maxLength={1000} rows={2} onChange={(e) => setText(e.target.value)} />
          </Field>
          <div className="flex flex-wrap gap-2">
            <button type="button" className="btn-danger" disabled={busy} onClick={confirmAsk}>{asking === 'reject' ? t('Reject') : t('Withdraw')}</button>
            <button type="button" className="btn-secondary" disabled={busy} onClick={() => { setAsking(null); setText(''); setError(''); }}>{t('Cancel')}</button>
          </div>
        </div>
        : r.status === 'Pending' && <div className="flex flex-wrap items-center gap-2">
          {canDecide && <>
            <button type="button" className="btn-primary" disabled={busy} onClick={() => void run(() => benefitUsageApi.decide(r.id, 'Approved'), t('Approved.'))}>{t('Approve')}</button>
            <button type="button" className="btn-secondary" disabled={busy} onClick={() => setAsking('reject')}>{t('Reject')}</button>
          </>}
          {r.canCancel && <button type="button" className="btn-ghost" disabled={busy} onClick={() => setAsking('cancel')}>{t('Withdraw')}</button>}
          {!canDecide && <p className="text-xs text-fg-muted">{t('Finance decides this. Whoever requested it, and the employee it is for, cannot.')}</p>}
        </div>}
      {error && <p role="alert" className="text-sm text-danger-fg">{error}</p>}
    </li>
  );
}
