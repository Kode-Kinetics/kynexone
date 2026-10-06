'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import { AlertTriangle, CheckCircle2, Lock, RefreshCw } from 'lucide-react';
import { useLocale } from '../../contexts/LocaleContext';
import { useAuth } from '../../contexts/AuthContext';
import type { EmployeeDetail } from '../../api/employees';
import { packageApi, type EmployeePackageView, type PackageLine } from '../../api/package';
import { WhyPopover } from './WhyPopover';
import { date, fill, hrWhy, reasonText, valueText, coverageText, type FormatContext } from './packageFormat';

type Group = { key: string; title: string; lines: PackageLine[] };

/**
 * The employee's package on the HR profile (Release A slice R2): pay in the Qiwa contract, contract benefits fixed for
 * the term, and facilities by current policy — each line with "Why this value?" naming the grade cell, the frozen row
 * and the term. One next action at most: fix the package for this contract year, or confirm a migrated one.
 */
export function EmployeePackagePanel({ employee }: { employee: EmployeeDetail }) {
  const { t, locale } = useLocale();
  const { hasPermission } = useAuth();
  const canManage = hasPermission('entitlements.manage');
  const [view, setView] = useState<EmployeePackageView | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      setView(await packageApi.forEmployee(employee.id));
    } catch (e) {
      const res = (e as { response?: { status?: number } })?.response;
      setError(res?.status === 403 ? t('You do not have access to this employee’s package.') : t('The package could not be loaded.'));
    } finally {
      setLoading(false);
    }
  }, [employee.id, t]);

  useEffect(() => { void load(); }, [load]);

  const act = async (kind: 'freeze' | 'confirm') => {
    if (!view?.contract) return;
    setBusy(true);
    setNotice(null);
    try {
      if (kind === 'freeze') {
        const r = await packageApi.freeze(employee.id, view.contract.id);
        setNotice(r.alreadyFrozen ? t('This contract year was already fixed.') : r.frozen ? t('The package is fixed for this contract year.') : t('Nothing to fix: the grade has no contract benefits yet.'));
      } else {
        await packageApi.confirm(employee.id, view.contract.id);
        setNotice(t('The package is confirmed against the signed contract.'));
      }
      await load();
    } catch (e) {
      const data = (e as { response?: { data?: { message?: string } } })?.response?.data;
      setNotice(data?.message ?? t('The package could not be changed.'));
    } finally {
      setBusy(false);
    }
  };

  const ctx: FormatContext = useMemo(() => ({ t, locale, currency: view?.currency ?? 'SAR' }), [t, locale, view?.currency]);

  if (loading) return <div className="h-40 animate-pulse rounded-2xl bg-slate-100 dark:bg-white/[0.04]" aria-busy="true" aria-label={t('Loading the package')} />;
  if (error || !view) {
    return (
      <div role="alert" className="flex flex-col items-start gap-2 rounded-2xl border border-amber-200 bg-amber-50 p-4 text-sm text-amber-800 dark:border-amber-500/20 dark:bg-amber-500/[0.06] dark:text-amber-300">
        <span className="flex items-center gap-2"><AlertTriangle className="h-4 w-4" aria-hidden="true" />{error}</span>
        <button type="button" onClick={() => void load()} className="flex items-center gap-1.5 rounded-lg bg-amber-600 px-3 py-1.5 text-xs font-semibold text-white hover:bg-amber-700">
          <RefreshCw className="h-3.5 w-3.5" aria-hidden="true" />{t('Load the package again')}
        </button>
      </div>
    );
  }

  const pkg = view.package;
  const label = (code: string) => (locale === 'ar' ? view.labels[code]?.ar : view.labels[code]?.en) ?? code;
  const gradeName = view.grade ? `${view.grade.code} ${locale === 'ar' && view.grade.nameAr ? view.grade.nameAr : view.grade.name}` : '';
  const companyName = view.company ? (locale === 'ar' ? view.company.nameAr || view.company.nameEn : view.company.nameEn) : '';
  const anyFrozen = pkg.lines.some((l) => l.source === 'ContractFrozen');
  const groups: Group[] = [
    { key: 'pay', title: t('Pay (in the Qiwa contract)'), lines: pkg.lines.filter((l) => l.class === 'QiwaWage') },
    {
      key: 'contract',
      title: anyFrozen && pkg.termEndsOn
        ? fill(t('Contract benefits — fixed until {date}'), { date: date(pkg.termEndsOn, locale) })
        : anyFrozen ? t('Contract benefits — fixed for this contract') : t('Contract benefits — not yet fixed for this contract year'),
      lines: pkg.lines.filter((l) => l.class !== 'QiwaWage' && l.class !== 'Facility'),
    },
    { key: 'facility', title: t('Facilities — current policy'), lines: pkg.lines.filter((l) => l.class === 'Facility') },
  ].filter((g) => g.lines.length > 0);

  const why = (line: PackageLine) => {
    const cell = view.cells.find((c) => c.id === line.gradeEntitlementId);
    const row = view.frozenRows.find((r) => r.id === line.employeeEntitlementId);
    return hrWhy({
      source: line.source, gradeStandardDiffers: line.gradeStandardDiffers, isCompanyOverride: line.isCompanyOverride, gradeName, companyName,
      salarySince: view.salary?.effectiveDate,
      cell: cell ? { ...cell, valueText: valueText({ ...cell, monthlyCash: null, dependantsCovered: 0 }, ctx) } : null,
      frozen: row && view.contract ? { ...row, contractNumber: view.contract.number } : null,
    }, ctx);
  };

  return (
    <section className="space-y-4" aria-label={t('Package for this contract year')}>
      <header className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h3 className="text-sm font-bold text-slate-800 dark:text-slate-100">{t('Package for this contract year')}</h3>
          <p className="text-xs text-slate-500 dark:text-slate-400">
            {[gradeName && fill(t('Grade {grade}'), { grade: gradeName }), companyName,
              view.contract && fill(t('Contract {number}: {from} to {to}'), {
                number: view.contract.number, from: date(view.contract.startDate, locale),
                to: view.contract.endDate ? date(view.contract.endDate, locale) : t('no end date'),
              }),
              fill(t('As of {date}'), { date: date(pkg.asOf, locale) })].filter(Boolean).join(' · ')}
          </p>
        </div>
        {canManage && view.canFreeze && (
          <div className="flex flex-col items-end gap-1">
            <button type="button" disabled={busy} onClick={() => void act('freeze')}
              className="inline-flex items-center gap-1.5 rounded-lg bg-sapphire px-3 py-1.5 text-xs font-semibold text-white hover:bg-sapphire/90 disabled:opacity-60">
              <Lock className="h-3.5 w-3.5" aria-hidden="true" />{busy ? t('Fixing…') : t('Fix the package for this contract year')}
            </button>
            <span className="max-w-xs text-end text-[11px] text-slate-500 dark:text-slate-400">{t('Later changes to the grade table will not alter this year’s benefits.')}</span>
          </div>
        )}
        {canManage && view.unverifiedRows > 0 && (
          <button type="button" disabled={busy} onClick={() => void act('confirm')}
            className="inline-flex items-center gap-1.5 rounded-lg bg-emerald-600 px-3 py-1.5 text-xs font-semibold text-white hover:bg-emerald-700 disabled:opacity-60">
            <CheckCircle2 className="h-3.5 w-3.5" aria-hidden="true" />{t('Confirm it matches the signed contract')}
          </button>
        )}
      </header>

      {notice && <p role="status" className="rounded-lg bg-slate-50 px-3 py-2 text-xs text-slate-700 dark:bg-white/[0.04] dark:text-slate-200">{notice}</p>}
      {!view.contract && <p className="rounded-lg bg-slate-50 px-3 py-2 text-xs text-slate-600 dark:bg-white/[0.04] dark:text-slate-300">{t('No active contract term, so nothing is fixed yet. The grade standard is shown.')}</p>}
      {view.unverifiedRows > 0 && <p className="rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-800 dark:bg-amber-500/[0.06] dark:text-amber-300">{t('These benefits were loaded from the grade table. Check them against the signed contract, then confirm.')}</p>}
      {view.blockReasons.map((r) => (
        <div key={r.code} role="alert" className="rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-900 dark:border-amber-500/20 dark:bg-amber-500/[0.06] dark:text-amber-200">
          <p className="font-semibold">{locale === 'ar' ? r.titleAr : r.titleEn}</p>
          <p>{locale === 'ar' ? r.fixAr : r.fixEn}</p>
        </div>
      ))}

      {groups.map((group) => (
        <div key={group.key} className="rounded-2xl border border-slate-200 bg-white dark:border-white/10 dark:bg-white/[0.03]">
          <h4 className="border-b border-slate-100 px-4 py-2 text-xs font-semibold uppercase tracking-wide text-slate-500 dark:border-white/5 dark:text-slate-400">{group.title}</h4>
          <ul className="divide-y divide-slate-100 dark:divide-white/5">
            {group.lines.map((line) => (
              <li key={line.componentCode} className="flex flex-wrap items-center justify-between gap-x-4 gap-y-1 px-4 py-2.5">
                <div className="min-w-0">
                  <p className="text-sm font-medium text-slate-800 dark:text-slate-100">{label(line.componentCode)}</p>
                  {line.dependantScope !== 'None' && line.eligible && <p className="text-xs text-slate-500 dark:text-slate-400">{coverageText(line, t)}</p>}
                  {line.reasonCode && <p className="text-xs text-amber-700 dark:text-amber-300">{reasonText(line.reasonCode, t)}</p>}
                </div>
                <div className="flex flex-wrap items-center gap-2">
                  {line.gradeStandardDiffers && <span className="rounded-full bg-sky-50 px-2 py-0.5 text-[11px] font-medium text-sky-700 dark:bg-sky-500/10 dark:text-sky-300">{t('Reviewed at renewal')}</span>}
                  <span className={`text-sm tabular-nums ${line.eligible ? 'text-slate-900 dark:text-slate-50' : 'text-slate-400 line-through dark:text-slate-500'}`}>
                    {line.eligible || line.class === 'QiwaWage' ? valueText(line, ctx) : line.reasonCode ? '—' : t('Not included')}
                  </span>
                  <WhyPopover title={label(line.componentCode)} lines={why(line)} />
                </div>
              </li>
            ))}
          </ul>
        </div>
      ))}
    </section>
  );
}
