'use client';

import { useCallback, useEffect, useState } from 'react';
import { AlertTriangle, Download, FileText, RefreshCw } from 'lucide-react';
import { essApi, type EssPayslipDetail, type EssPayslipLine, type EssPayslipSummary } from '@/src/api/ess';
import { useLocale } from '@/src/contexts/LocaleContext';
import { payslipMonthLabel, payslipSections } from '@/src/lib/essPayslip';

const money = (n: number) => n.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

function errorMessage(e: unknown, t: (k: string) => string, fallback: string) {
  const res = (e as { response?: { status?: number; data?: { message?: string } } })?.response;
  if (res?.status === 403) return t('Self-service access is not enabled for your account.');
  return res?.data?.message ?? t(fallback);
}

/**
 * Employee self-service: the caller's own payslips (GET /api/ess/payslips), one month's lines
 * (GET /api/ess/payslips/{id}) and its PDF. Employer contributions are shown in their own section and
 * are never part of the deductions total.
 */
export function MyPayslipsPage() {
  const { t, locale } = useLocale();
  const [slips, setSlips] = useState<EssPayslipSummary[] | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [detail, setDetail] = useState<EssPayslipDetail | null>(null);
  const [detailLoading, setDetailLoading] = useState(false);
  const [detailError, setDetailError] = useState<string | null>(null);
  const [downloading, setDownloading] = useState(false);
  const [downloadError, setDownloadError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const rows = await essApi.payslips();
      setSlips(rows);
      setSelectedId((current) => current ?? rows[0]?.id ?? null);
    } catch (e) {
      setError(errorMessage(e, t, 'Could not load your payslips.'));
      setSlips(null);
    } finally {
      setLoading(false);
    }
  }, [t]);

  useEffect(() => { void load(); }, [load]);

  useEffect(() => {
    if (!selectedId) { setDetail(null); return; }
    let cancelled = false;
    setDetailLoading(true);
    setDetailError(null);
    setDownloadError(null);
    essApi.payslipDetail(selectedId)
      .then((d) => { if (!cancelled) setDetail(d); })
      .catch((e) => { if (!cancelled) { setDetail(null); setDetailError(errorMessage(e, t, 'Could not load this payslip.')); } })
      .finally(() => { if (!cancelled) setDetailLoading(false); });
    return () => { cancelled = true; };
  }, [selectedId, t]);

  const download = async () => {
    if (!detail) return;
    setDownloading(true);
    setDownloadError(null);
    try {
      await essApi.downloadPayslip(detail.id, `payslip-${detail.year}-${String(detail.month).padStart(2, '0')}.pdf`);
    } catch {
      setDownloadError(t('Could not download the payslip PDF. Please try again.'));
    } finally {
      setDownloading(false);
    }
  };

  return (
    <div className="space-y-4">
      <div>
        <h1 className="text-lg font-bold text-slate-800 dark:text-slate-100">{t('My Payslips')}</h1>
        <p className="text-xs text-slate-500 dark:text-slate-400">{t('Your finalised payslips. Choose a month to see every line, or download the PDF.')}</p>
      </div>

      {loading ? (
        <div className="grid gap-3 lg:grid-cols-[16rem_1fr]" aria-busy="true" aria-label={t('Loading your payslips')}>
          {[0, 1].map((i) => <div key={i} className="h-48 animate-pulse rounded-2xl bg-slate-100 dark:bg-white/[0.04]" />)}
        </div>
      ) : error ? (
        <div role="alert" className="flex flex-col items-center gap-3 rounded-2xl border border-amber-200 bg-amber-50 p-8 text-center dark:border-amber-500/20 dark:bg-amber-500/[0.06]">
          <AlertTriangle className="h-8 w-8 text-amber-500" />
          <p className="max-w-lg text-sm font-medium text-amber-800 dark:text-amber-300">{error}</p>
          <button type="button" onClick={() => void load()} className="flex items-center gap-1.5 rounded-lg bg-amber-600 px-3 py-1.5 text-xs font-semibold text-white hover:bg-amber-700">
            <RefreshCw className="h-3.5 w-3.5" /> {t('Retry')}
          </button>
        </div>
      ) : !slips || slips.length === 0 ? (
        <div data-testid="my-payslips-empty" className="flex flex-col items-center gap-3 rounded-2xl border border-slate-200 bg-white p-10 text-center dark:border-white/[0.06] dark:bg-white/[0.03]">
          <FileText className="h-10 w-10 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-semibold text-slate-700 dark:text-slate-200">{t('You have no finalised payslips yet.')}</p>
          <p className="max-w-md text-xs text-slate-500 dark:text-slate-400">{t('Your payslip appears here once payroll for the month is locked.')}</p>
        </div>
      ) : (
        <div className="grid gap-3 lg:grid-cols-[16rem_1fr]">
          <nav aria-label={t('Payslips by month')} className="rounded-2xl border border-slate-200 bg-white p-2 dark:border-white/[0.06] dark:bg-white/[0.03]">
            <ul className="space-y-1" data-testid="my-payslips-list">
              {slips.map((s) => {
                const active = s.id === selectedId;
                return (
                  <li key={s.id}>
                    <button
                      type="button"
                      onClick={() => setSelectedId(s.id)}
                      aria-current={active ? 'true' : undefined}
                      className={`flex w-full items-center justify-between gap-2 rounded-xl px-3 py-2 text-start text-sm transition ${active
                        ? 'bg-sapphire/10 font-semibold text-sapphire dark:bg-cyanAccent/10 dark:text-cyanAccent'
                        : 'text-slate-700 hover:bg-slate-50 dark:text-slate-200 dark:hover:bg-white/[0.04]'}`}
                    >
                      <span>{payslipMonthLabel(s.year, s.month, locale, s.periodLabel)}</span>
                      <span className="font-mono text-xs tabular-nums">{s.currency} {money(s.netSalary)}</span>
                    </button>
                  </li>
                );
              })}
            </ul>
          </nav>

          <section className="rounded-2xl border border-slate-200 bg-white p-4 dark:border-white/[0.06] dark:bg-white/[0.03]" aria-live="polite">
            {detailLoading ? (
              <div className="h-48 animate-pulse rounded-xl bg-slate-100 dark:bg-white/[0.04]" aria-busy="true" />
            ) : detailError ? (
              <p role="alert" className="text-sm text-amber-700 dark:text-amber-300">{detailError}</p>
            ) : detail ? (
              <PayslipDetailView detail={detail} downloading={downloading} downloadError={downloadError} onDownload={() => void download()} />
            ) : null}
          </section>
        </div>
      )}
    </div>
  );
}

function PayslipDetailView({ detail, downloading, downloadError, onDownload }: {
  detail: EssPayslipDetail;
  downloading: boolean;
  downloadError: string | null;
  onDownload: () => void;
}) {
  const { t, locale } = useLocale();
  const s = payslipSections(detail.lines);
  const cur = detail.currency;

  return (
    <div className="space-y-4" data-testid="my-payslip-detail">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <h2 className="text-base font-bold text-slate-800 dark:text-slate-100">{payslipMonthLabel(detail.year, detail.month, locale, detail.periodLabel)}</h2>
        <button
          type="button"
          onClick={onDownload}
          disabled={downloading}
          className="inline-flex items-center gap-1.5 rounded-xl bg-sapphire px-4 py-2 text-sm font-semibold text-white hover:bg-sapphire/90 disabled:opacity-60 dark:bg-cyanAccent dark:text-slate-900"
        >
          <Download className="h-4 w-4" /> {downloading ? t('Downloading…') : t('Download PDF')}
        </button>
      </div>
      {downloadError && <p role="alert" className="text-xs text-rose-600 dark:text-rose-400">{downloadError}</p>}
      {!(detail.reconciled && s.reconciles) && (
        <p role="alert" className="rounded-xl bg-amber-50 p-3 text-xs font-medium text-amber-800 dark:bg-amber-500/[0.08] dark:text-amber-300">
          {t('These figures do not add up. Please raise a request with HR before relying on this payslip.')}
        </p>
      )}

      <div className="grid gap-3 sm:grid-cols-2">
        <LineBlock title={t('Earnings')} lines={s.earnings} totalLabel={t('Gross pay')} total={s.gross} currency={cur} />
        <LineBlock title={t('Deductions')} lines={s.deductions} totalLabel={t('Total deductions')} total={s.totalDeductions} currency={cur}
          empty={t('No deductions this month.')} />
      </div>

      <div className="flex items-center justify-between rounded-xl bg-slate-900 px-4 py-3 text-white dark:bg-white/[0.08]">
        <span className="text-sm font-semibold">{t('Net pay')}</span>
        <span className="font-mono text-lg font-bold tabular-nums" data-testid="my-payslip-net">{cur} {money(s.net)}</span>
      </div>
      <p className="text-[11px] text-slate-500 dark:text-slate-400">{t('Gross pay minus total deductions equals net pay.')}</p>

      {s.employerContributions.length > 0 && (
        <div data-testid="my-payslip-employer">
          <LineBlock title={t('Employer contributions (not deducted from your pay)')} lines={s.employerContributions}
            totalLabel={t('Total employer contributions')} total={s.employerTotal} currency={cur} muted />
          <p className="mt-1 text-[11px] text-slate-500 dark:text-slate-400">
            {t('Your employer pays these on top of your salary, for example its share of GOSI. They are not taken from your pay.')}
          </p>
        </div>
      )}
    </div>
  );
}

function LineBlock({ title, lines, totalLabel, total, currency, empty, muted }: {
  title: string;
  lines: EssPayslipLine[];
  totalLabel: string;
  total: number;
  currency: string;
  empty?: string;
  muted?: boolean;
}) {
  return (
    <div className={`rounded-xl border p-3 ${muted ? 'border-dashed border-slate-300 dark:border-white/[0.12]' : 'border-slate-200 dark:border-white/[0.06]'}`}>
      <h3 className="mb-2 text-xs font-bold uppercase tracking-wide text-slate-500 dark:text-slate-400">{title}</h3>
      {lines.length === 0 && empty ? (
        <p className="text-xs text-slate-500 dark:text-slate-400">{empty}</p>
      ) : (
        <dl className="space-y-1 text-sm">
          {lines.map((l, i) => (
            <div key={`${l.name}-${i}`} className="flex justify-between gap-2">
              <dt className="text-slate-700 dark:text-slate-200">{l.name}</dt>
              <dd className="font-mono tabular-nums text-slate-800 dark:text-slate-100">{money(l.amount)}</dd>
            </div>
          ))}
        </dl>
      )}
      <div className="mt-2 flex justify-between gap-2 border-t border-slate-200 pt-2 text-sm font-semibold dark:border-white/[0.06]">
        <span className="text-slate-700 dark:text-slate-200">{totalLabel}</span>
        <span className="font-mono tabular-nums text-slate-900 dark:text-white">{currency} {money(total)}</span>
      </div>
    </div>
  );
}
