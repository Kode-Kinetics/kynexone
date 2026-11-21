'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import { AlertTriangle, CheckCircle2, RefreshCw } from 'lucide-react';
import { Badge } from '../ui/Badge';
import { Field, Textarea } from '../ui/Field';
import { RequestUsageDialog } from './RequestUsageDialog';
import { UsageFigures } from '../../views/BenefitUsagePage';
import { useAuth } from '../../contexts/AuthContext';
import { useLocale } from '../../contexts/LocaleContext';
import { benefitUsageApi, type EssUsage, type EssUsageRequest } from '../../api/benefitUsage';
import { STATUS_TONES, calendarDate, fill, measureText, refusalKey, statusKey } from '../../lib/benefitUsage';

/**
 * "My benefits — used and left" inside My package, like a loan balance: for each fixed contract benefit that is used up (education,
 * tickets), what this period gives, what is used and what is left, for the employee and each family member it covers; a Request
 * button (Finance approves it next) and the status of the employee's own requests, with Withdraw while Pending. Shows nothing when
 * the package has no such benefit.
 */
export function MyBenefitUsageCard() {
  const { t, locale } = useLocale();
  const { hasPermission } = useAuth();
  const [data, setData] = useState<EssUsage | null>(null);
  const [failed, setFailed] = useState(false);
  const [asking, setAsking] = useState(false);
  const [notice, setNotice] = useState('');
  const today = useMemo(() => new Date().toISOString().slice(0, 10), []);

  const load = useCallback(async () => {
    setFailed(false);
    try { setData(await benefitUsageApi.mine()); } catch { setFailed(true); }
  }, []);
  useEffect(() => { void load(); }, [load]);

  if (failed) {
    return (
      <section role="alert" className="flex flex-wrap items-center gap-2 rounded-2xl border border-line bg-surface px-4 py-3 text-sm text-fg">
        <AlertTriangle className="h-4 w-4 text-warning-fg" aria-hidden="true" />{t('Your benefit usage could not be loaded.')}
        <button type="button" className="btn-ghost" onClick={() => void load()}><RefreshCw className="h-4 w-4" aria-hidden="true" />{t('Try again')}</button>
      </section>
    );
  }
  if (!data || data.benefits.length === 0) return null;
  const label = (x: { labelEn: string; labelAr: string }) => (locale === 'ar' ? x.labelAr : x.labelEn);
  const resetOn = (end: string) => {
    const next = new Date(`${end}T00:00:00Z`);
    next.setUTCDate(next.getUTCDate() + 1);
    return calendarDate(next.toISOString().slice(0, 10), locale);
  };
  const mayRequest = hasPermission('ess.write') && data.benefits.some((b) => b.canRequest);

  return (
    <section aria-labelledby="my-benefit-usage" className="rounded-2xl border border-line bg-surface" data-testid="my-benefit-usage">
      <div className="flex flex-wrap items-center justify-between gap-2 border-b border-line px-4 py-2">
        <h2 id="my-benefit-usage" className="text-xs font-semibold text-fg-muted">{t('My benefits — used and left')}</h2>
        {mayRequest && <button type="button" className="btn-secondary" onClick={() => setAsking(true)}>{t('Request a benefit')}</button>}
      </div>
      {notice && <p role="status" className="flex items-center gap-2 px-4 pt-3 text-sm text-success-fg"><CheckCircle2 className="h-4 w-4" aria-hidden="true" />{notice}</p>}
      <ul className="divide-y divide-line">
        {data.benefits.map((b) => (
          <li key={b.benefitCode} className="space-y-2 px-4 py-3">
            <div className="flex flex-wrap items-baseline justify-between gap-2">
              <p className="text-sm font-medium text-fg">{label(b)}</p>
              <p className="text-xs text-fg-muted">{fill(t('Resets on {date}'), { date: resetOn(b.periodEnd) })}</p>
            </div>
            <UsageFigures row={{ ...b, beneficiaries: b.beneficiaries.map((p) => (p.dependantId ? p : { ...p, name: t('You'), dependantId: 'self' })) }} />
          </li>
        ))}
      </ul>
      {data.requests.length > 0 && (
        <div className="border-t border-line px-4 py-3">
          <h3 className="mb-2 text-xs font-semibold text-fg-muted">{t('My requests')}</h3>
          <ul className="space-y-2">{data.requests.slice(0, 10).map((r) => <MyRequest key={r.id} request={r} onDone={(m) => { setNotice(m); void load(); }} />)}</ul>
        </div>
      )}
      <RequestUsageDialog open={asking} rows={data.benefits} today={today} forSelf onClose={() => setAsking(false)}
        onSubmit={async (input) => {
          await benefitUsageApi.requestMine(input);
          setAsking(false);
          setNotice(t('Your request was sent. Finance decides it next.'));
          void load();
        }} />
    </section>
  );
}

function MyRequest({ request: r, onDone }: { request: EssUsageRequest; onDone: (message: string) => void }) {
  const { t, locale } = useLocale();
  const [asking, setAsking] = useState(false);
  const [text, setText] = useState('');
  const [error, setError] = useState('');
  const withdraw = async () => {
    if (!text.trim()) { setError(t('Say why.')); return; }
    try { await benefitUsageApi.cancelMine(r.id, text.trim()); setAsking(false); onDone(t('Withdrawn. The entitlement is available again.')); }
    catch (e) { setError(t(refusalKey(e))); }
  };
  return (
    <li className="space-y-1 text-sm">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <span className="min-w-0 text-fg">
          {locale === 'ar' ? r.labelAr : r.labelEn} · {r.units != null ? measureText(r.units, 'Units', null, locale) : measureText(r.amount ?? 0, 'Amount', r.currency, locale)}
          {r.forName && <span className="text-fg-muted"> · {fill(t('For {name}'), { name: r.forName })}</span>}
          <span className="text-fg-muted"> · {calendarDate(r.usageDate, locale)}</span>
        </span>
        <span className="flex items-center gap-2">
          <Badge tone={STATUS_TONES[r.status]}>{t(statusKey(r.status))}</Badge>
          {r.canCancel && !asking && <button type="button" className="btn-ghost" onClick={() => setAsking(true)}>{t('Withdraw')}</button>}
        </span>
      </div>
      {r.decisionReason && <p className="text-xs text-fg-muted">{fill(t('Reason: {text}'), { text: r.decisionReason })}</p>}
      {asking && <div className="space-y-2">
        <Field label={t('Reason (required)')} noInfo="a short free-text reason">
          <Textarea value={text} maxLength={1000} rows={2} onChange={(e) => setText(e.target.value)} />
        </Field>
        <div className="flex gap-2">
          <button type="button" className="btn-danger" onClick={() => void withdraw()}>{t('Withdraw')}</button>
          <button type="button" className="btn-secondary" onClick={() => { setAsking(false); setError(''); }}>{t('Cancel')}</button>
        </div>
      </div>}
      {error && <p role="alert" className="text-sm text-danger-fg">{error}</p>}
    </li>
  );
}
