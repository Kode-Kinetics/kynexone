'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import { AlertTriangle, CalendarClock, RefreshCw } from 'lucide-react';
import { ReleaseAGate } from '../components/releaseA/ReleaseAGate';
import { WhyPopover } from '../components/entitlements/WhyPopover';
import { coverageText, date, essWhy, fill, reasonText, valueText, type FormatContext } from '../components/entitlements/packageFormat';
import { useLocale } from '../contexts/LocaleContext';
import { packageApi, type EssGroup, type EssPackage } from '../api/package';

/**
 * My package / باقتي (Release A slice R2): the employee's own pay, contract benefits fixed for this contract year, and the
 * facilities their grade offers today — Arabic-first and built for a phone: one column, logical (start/end) spacing so it
 * mirrors in RTL, values that wrap instead of overflowing, and a "Why this value?" on every line.
 */
export function MyPackagePage() {
  return (
    <ReleaseAGate>
      <MyPackage />
    </ReleaseAGate>
  );
}

function MyPackage() {
  const { t, locale } = useLocale();
  const [data, setData] = useState<EssPackage | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      setData(await packageApi.mine());
    } catch (e) {
      const res = (e as { response?: { status?: number; data?: { error?: string } } })?.response;
      setError(res?.data?.error === 'no_employee_record'
        ? t('Your login is not linked to an employee record yet. Ask HR to link it.')
        : t('Your package could not be loaded.'));
      setData(null);
    } finally {
      setLoading(false);
    }
  }, [t]);

  useEffect(() => { void load(); }, [load]);

  const ctx: FormatContext = useMemo(() => ({ t, locale, currency: data?.currency ?? 'SAR' }), [t, locale, data?.currency]);

  const header = (
    <div>
      <h1 className="text-lg font-bold text-slate-800 dark:text-slate-100">{t('My package')}</h1>
      <p className="text-xs text-slate-500 dark:text-slate-400">{t('Your pay, your contract benefits for this contract year, and the facilities your grade offers.')}</p>
    </div>
  );

  if (loading) {
    return (
      <div className="mx-auto max-w-2xl space-y-4">
        {header}
        {[0, 1, 2].map((i) => <div key={i} className="h-28 animate-pulse rounded-2xl bg-slate-100 dark:bg-white/[0.04]" aria-busy="true" />)}
      </div>
    );
  }
  if (error || !data) {
    return (
      <div className="mx-auto max-w-2xl space-y-4">
        {header}
        <div role="alert" className="flex flex-col items-center gap-3 rounded-2xl border border-amber-200 bg-amber-50 p-6 text-center dark:border-amber-500/20 dark:bg-amber-500/[0.06]">
          <AlertTriangle className="h-7 w-7 text-amber-500" aria-hidden="true" />
          <p className="text-sm font-medium text-amber-800 dark:text-amber-300">{error}</p>
          <button type="button" onClick={() => void load()} className="flex items-center gap-1.5 rounded-lg bg-amber-600 px-3 py-1.5 text-xs font-semibold text-white hover:bg-amber-700">
            <RefreshCw className="h-3.5 w-3.5" aria-hidden="true" />{t('Load my package again')}
          </button>
        </div>
      </div>
    );
  }

  const grade = (locale === 'ar' ? data.gradeNameAr : null) || data.gradeName;
  const company = (locale === 'ar' ? data.companyNameAr : null) || data.companyName;
  const anyFixed = data.lines.some((l) => l.fixed);
  const titles: Record<EssGroup, string> = {
    pay: t('Pay (in your Qiwa contract)'),
    contract: anyFixed && data.fixedUntil
      ? fill(t('Contract benefits — fixed until {date}'), { date: date(data.fixedUntil, locale) })
      : t('Contract benefits — not yet fixed for this contract year'),
    facility: t('Facilities — current policy'),
  };
  const groups = (['pay', 'contract', 'facility'] as EssGroup[])
    .map((g) => ({ key: g, lines: data.lines.filter((l) => l.group === g) }))
    .filter((g) => g.lines.length > 0);

  return (
    <div className="mx-auto max-w-2xl space-y-4">
      {header}
      <p className="text-xs text-slate-600 dark:text-slate-300">
        {[grade && fill(t('Grade {grade}'), { grade }), company, data.dependantsOnFile > 0 && fill(t('Dependants on file: {n}'), { n: data.dependantsOnFile })]
          .filter(Boolean).join(' · ')}
      </p>
      {data.renewalReviewOpensOn && (
        <p className="flex items-start gap-2 rounded-xl bg-sky-50 px-3 py-2 text-xs text-sky-800 dark:bg-sky-500/10 dark:text-sky-200">
          <CalendarClock className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
          {fill(t('Your contract renewal review opens on {date}.'), { date: date(data.renewalReviewOpensOn, locale) })}
        </p>
      )}
      {groups.map((group) => (
        <section key={group.key} aria-label={titles[group.key]} className="rounded-2xl border border-slate-200 bg-white dark:border-white/10 dark:bg-white/[0.03]">
          <h2 className="border-b border-slate-100 px-4 py-2 text-xs font-semibold text-slate-500 dark:border-white/5 dark:text-slate-400">{titles[group.key]}</h2>
          <ul className="divide-y divide-slate-100 dark:divide-white/5">
            {group.lines.map((line) => {
              const name = locale === 'ar' ? line.labelAr : line.labelEn;
              const shown = line.eligible || line.group === 'pay';
              return (
                <li key={line.componentCode} className="space-y-1 px-4 py-3">
                  <div className="flex items-start justify-between gap-3">
                    <p className="min-w-0 text-sm font-medium text-slate-800 dark:text-slate-100">{name}</p>
                    <WhyPopover title={name} lines={essWhy(line.why, ctx)} />
                  </div>
                  {shown
                    ? <p className="break-words text-sm tabular-nums text-slate-900 dark:text-slate-50">{valueText(line, ctx)}</p>
                    : !line.reasonCode && <p className="text-sm text-slate-400 dark:text-slate-500">{t('Not included')}</p>}
                  {shown && line.dependantScope !== 'None' && <p className="text-xs text-slate-500 dark:text-slate-400">{coverageText(line, t, data.dependantsOnFile)}</p>}
                  {line.reasonCode && <p className={`text-xs ${shown ? 'text-amber-700 dark:text-amber-300' : 'text-slate-500 dark:text-slate-400'}`}>{reasonText(line.reasonCode, t, line.reasonCriterion, line.eligibleFrom, locale)}</p>}
                </li>
              );
            })}
          </ul>
        </section>
      ))}
    </div>
  );
}
