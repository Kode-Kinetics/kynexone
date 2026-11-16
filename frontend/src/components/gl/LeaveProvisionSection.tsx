'use client';

import { useCallback, useEffect, useState } from 'react';
import { apiErrorReason } from '../../api/client';
import { financeRatesApi } from '../../api/financeRates';
import {
  LEAVE_PROVISIONING_KEY,
  financeGlAdjustmentsApi,
  type LeaveProvisionSummary,
} from '../../api/financeGlAdjustments';
import { useFormat } from '../../hooks/useFormat';
import { useT } from '../../hooks/useT';
import { Figure } from './EosbProvisionPanel';
import { lastMeasuredLabel } from '../../lib/glLeaveProvision';

const firstOfNextMonth = () => {
  const d = new Date();
  const next = new Date(d.getFullYear(), d.getMonth() + 1, 1);
  return `${next.getFullYear()}-${String(next.getMonth() + 1).padStart(2, '0')}`;
};

/**
 * Setup → GL & rates → End-of-service provision → Annual leave provision, for ONE company.
 *
 *  1. Monthly provisioning on/off — the company rate payparameter.leave_provisioning (Off | BalanceMonthly), effective
 *     from the first day of a chosen month. With it on, every payroll Lock values each employee's unused annual leave
 *     (days x the day rate a final settlement uses) and books only the change: expense 5114 / provision 2330. The first
 *     month books everything owed to date, so it looks like a large one-off.
 *  2. What the books carry (2330) beside the employees' measured value, with a drill-down to the employees behind it.
 *
 * The server is authoritative: refusals are shown word for word.
 */
export function LeaveProvisionSection({
  companyId, companyName, currency, canManageRates,
}: { companyId: string; companyName: string; currency: string | null; canManageRates: boolean }) {
  const t = useT();
  const f = useFormat();
  const [data, setData] = useState<LeaveProvisionSummary | null>(null);
  const [loadError, setLoadError] = useState('');
  const [switchMonth, setSwitchMonth] = useState(firstOfNextMonth());
  const [busy, setBusy] = useState('');
  const [actionError, setActionError] = useState('');
  const [notice, setNotice] = useState('');
  const [showEmployees, setShowEmployees] = useState(false);

  const load = useCallback(() => {
    setData(null);
    setLoadError('');
    financeGlAdjustmentsApi.leaveProvision(companyId)
      .then(setData)
      .catch((err) => setLoadError(apiErrorReason(err, t('The annual leave provision could not be loaded.'))));
  }, [companyId, t]);
  useEffect(() => { load(); setShowEmployees(false); setNotice(''); setActionError(''); }, [load]);

  const cur = currency ?? undefined;
  const money = (n: number) => f.money(n, cur);

  const setProvisioning = async (on: boolean) => {
    if (!data) return;
    const body = { rateKey: LEAVE_PROVISIONING_KEY, rateValue: on ? 'BalanceMonthly' : 'Off', effectiveFrom: `${switchMonth}-01` };
    const rateId = data.provisioning.currentRateId;
    setBusy(on ? 'on' : 'off');
    setActionError('');
    setNotice('');
    try {
      await (rateId ? financeRatesApi.updateCompanyRate(rateId, body) : financeRatesApi.createCompanyRate(body, companyId));
      setNotice(on
        ? t('Annual leave provisioning will start from {month}.', { month: switchMonth })
        : t('Annual leave provisioning will stop from {month}.', { month: switchMonth }));
      load();
    } catch (err) {
      setActionError(apiErrorReason(err, t('That could not be saved.')));
    } finally {
      setBusy('');
    }
  };

  if (loadError) {
    return (
      <section className="surface p-4" aria-labelledby="leaveprov-heading">
        <h3 id="leaveprov-heading" className="text-sm font-semibold text-slate-900 dark:text-white">{t('Annual leave provision')}</h3>
        <div role="alert" className="mt-2 text-xs text-rose-600 dark:text-rose-400" data-testid="leaveprov-load-error">
          {loadError} <button type="button" className="underline" onClick={load}>{t('Try again')}</button>
        </div>
      </section>
    );
  }
  if (!data) {
    return (
      <section className="surface p-4" aria-labelledby="leaveprov-heading">
        <h3 id="leaveprov-heading" className="text-sm font-semibold text-slate-900 dark:text-white">{t('Annual leave provision')}</h3>
        <p className="mt-2 text-xs text-slate-400" data-testid="leaveprov-loading">{t('Loading the annual leave provision…')}</p>
      </section>
    );
  }

  const p = data.provisioning;
  const scheduled = p.latestEffectiveFrom && p.latestEffectiveFrom !== p.effectiveFrom ? p.latestEffectiveFrom : null;
  const startsOn = p.latestValue === 'BalanceMonthly';

  return (
    <section className="surface space-y-4 p-4" aria-labelledby="leaveprov-heading" data-testid="leaveprov-panel">
      <div>
        <h3 id="leaveprov-heading" className="text-sm font-semibold text-slate-900 dark:text-white">{t('Annual leave provision')}</h3>
        <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
          {t("Each month, when payroll is locked, the value of every employee's unused annual leave is measured, and only the change is booked: expense {expense}, provision {provision}.",
            { expense: data.expenseAccount, provision: data.provisionAccount })}
        </p>
      </div>

      <div className="rounded-lg border border-slate-200 p-3 text-xs dark:border-white/10" data-testid="leaveprov-switch">
        <p className="font-semibold text-slate-800 dark:text-slate-100">
          {p.enabled
            ? t('Annual leave provisioning is on for {company} since {date}.', { company: companyName, date: p.effectiveFrom ? f.date(p.effectiveFrom) : '—' })
            : t('Annual leave provisioning is off for {company}. Payroll journals do not include an annual leave provision.', { company: companyName })}
        </p>
        {scheduled && (
          <p className="mt-1 text-slate-600 dark:text-slate-300">
            {startsOn
              ? t('Scheduled: annual leave provisioning starts on {date}.', { date: f.date(scheduled) })
              : t('Scheduled: annual leave provisioning stops on {date}.', { date: f.date(scheduled) })}
          </p>
        )}
        {p.inheritedFromTenant && <p className="mt-1 text-slate-500">{t('This setting comes from the group default.')}</p>}
        <p className="mt-1 text-slate-500">
          {t('Unused days are valued at the day rate a final settlement uses, {basis}, so the provision and the leave payout on exit agree. Leave taken lowers the provision the following month.',
            { basis: data.basis.label })}
        </p>
        <p className="mt-1 text-slate-500" data-testid="leaveprov-catchup-note">
          {t('The first payroll locked after you switch this on books everything owed for unused leave to date in one entry (the opening catch-up), so that month will look like a large one-off.')}
        </p>
        {canManageRates ? (
          <div className="mt-2 flex flex-wrap items-end gap-2">
            <label className="font-medium text-slate-600 dark:text-slate-300">
              {t('From month')}
              <input type="month" className="input mt-1" value={switchMonth} onChange={(e) => setSwitchMonth(e.target.value)} data-testid="leaveprov-switch-month" />
            </label>
            {p.enabled || startsOn ? (
              <button type="button" className="btn-secondary h-8 px-3 text-xs" disabled={!switchMonth || !!busy} onClick={() => setProvisioning(false)}>
                {busy === 'off' ? t('Saving…') : t('Stop annual leave provisioning')}
              </button>
            ) : (
              <button type="button" className="btn-primary h-8 px-3 text-xs" disabled={!switchMonth || !!busy} onClick={() => setProvisioning(true)}>
                {busy === 'on' ? t('Saving…') : t('Start annual leave provisioning')}
              </button>
            )}
          </div>
        ) : (
          <p className="mt-2 text-slate-500">{t('Only someone who manages company rates can switch provisioning on or off.')}</p>
        )}
      </div>

      {actionError && <p role="alert" className="rounded-lg bg-rose-50 px-3 py-2 text-xs text-rose-700 dark:bg-rose-500/10 dark:text-rose-300" data-testid="leaveprov-error">{actionError}</p>}
      {notice && <p role="status" className="rounded-lg bg-emerald-50 px-3 py-2 text-xs text-emerald-800 dark:bg-emerald-500/10 dark:text-emerald-300" data-testid="leaveprov-notice">{notice}</p>}

      <dl className="grid gap-3 sm:grid-cols-3" data-testid="leaveprov-figures">
        <Figure label={t('Provision in the books')} value={money(data.glBalance)}
          definition={t('Credits minus debits on {account} for this company.', { account: data.provisionAccount })} />
        <Figure label={t('Value of unused leave')} value={money(data.employeeObligation)}
          definition={t('The latest month-end measurement for each employee still provided for.')} />
        <Figure label={t('Unused leave days')} value={f.number(data.totalDays, { maximumFractionDigits: 2 })}
          definition={t('Days the employees measured at the latest month end could still take or be paid for.')} />
      </dl>

      {data.employees.length === 0 ? (
        <p className="text-xs text-slate-500" data-testid="leaveprov-no-employees">
          {p.enabled
            ? t('No payroll has been locked since provisioning started, so no employee has been measured yet.')
            : t('Nobody has been measured yet.')}
        </p>
      ) : (
        <div>
          <button type="button" className="btn-secondary h-8 px-3 text-xs" aria-expanded={showEmployees} aria-controls="leaveprov-employees"
            onClick={() => setShowEmployees((v) => !v)}>
            {showEmployees
              ? t('Hide employees')
              : t('{count, plural, one {Show the # employee behind these figures} other {Show the # employees behind these figures}}', { count: data.employees.length })}
          </button>
          {showEmployees && (
            <div className="mt-2 overflow-x-auto outline-none focus-visible:ring-2 focus-visible:ring-sapphire dark:focus-visible:ring-cyanAccent" tabIndex={0} role="group" aria-labelledby="leaveprov-heading" id="leaveprov-employees">
              <table className="w-full text-xs" data-testid="leaveprov-employees">
                <thead>
                  <tr className="text-start text-slate-500">
                    <th scope="col" className="py-1 text-start font-medium">{t('Employee')}</th>
                    <th scope="col" className="py-1 text-start font-medium">{t('Last measured')}</th>
                    <th scope="col" className="py-1 text-end font-medium">{t('Unused days')}</th>
                    <th scope="col" className="py-1 text-end font-medium">{t('Day rate')}</th>
                    <th scope="col" className="py-1 text-end font-medium">{t('Value')}</th>
                    <th scope="col" className="py-1 text-end font-medium">{t('Provided in the books')}</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100 dark:divide-white/5">
                  {data.employees.map((e) => (
                    <tr key={e.employeeId}>
                      <td className="py-1.5"><span className="font-mono">{e.employeeCode}</span> · {e.employeeName}</td>
                      <td className="py-1.5 font-mono">{lastMeasuredLabel(e, t)}</td>
                      <td className="py-1.5 text-end">{f.number(e.days, { maximumFractionDigits: 2 })}</td>
                      <td className="py-1.5 text-end">{money(e.dailyRate)}</td>
                      <td className="py-1.5 text-end">{money(e.obligation)}</td>
                      <td className="py-1.5 text-end">{money(e.provisionCarried)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}
    </section>
  );
}
