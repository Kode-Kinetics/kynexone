'use client';

import { useCallback, useEffect, useState } from 'react';
import { apiErrorReason } from '../../api/client';
import { financeRatesApi } from '../../api/financeRates';
import {
  EOSB_PROVISIONING_KEY,
  financeGlAdjustmentsApi,
  type EosbProvisionSummary,
  type GlAdjustment,
  type LegacyAbsenceSummary,
} from '../../api/financeGlAdjustments';
import { useFormat } from '../../hooks/useFormat';
import { useT } from '../../hooks/useT';

/**
 * Setup → GL & rates → End-of-service provision, for ONE company.
 *
 *  1. Monthly provisioning on/off — the company rate payparameter.eosb_provisioning (Off | StatutoryMonthly),
 *     effective from the first day of a chosen month. With it on, every payroll Lock books the change in each
 *     employee's end-of-service obligation (DR 5110 / CR 2310); the first month books everything to date.
 *  2. What the books carry (2310) beside the employees' measured obligation and any approved actuarial
 *     adjustments, each with its definition, and a drill-down to the employees behind the figure.
 *  3. Actuarial true-up: requested here, posted only when a SECOND person approves.
 *  4. Old absence-deduction accounts (2104/2105): move what is still there to the contra-expense (or an equity
 *     account for a closed year), again only on a second person's approval.
 *
 * The server is authoritative: refusals are shown word for word, and Approve/Reject appear only on requests
 * the server marks `canApprove` for this viewer.
 */
export function EosbProvisionPanel({
  companyId,
  companyName,
  currency,
  canManage,
  canManageRates,
}: {
  companyId: string | null;
  companyName: string;
  currency: string | null;
  canManage: boolean;
  canManageRates: boolean;
}) {
  const t = useT();
  if (!companyId) {
    return (
      <section className="surface p-6 text-center text-sm text-slate-500" data-testid="eosb-choose-company">
        {t('Choose a company above to see its end-of-service provision.')}
      </section>
    );
  }
  return (
    <div className="space-y-4">
      <ProvisionSection companyId={companyId} companyName={companyName} currency={currency} canManage={canManage} canManageRates={canManageRates} />
      <LegacyAbsenceSection companyId={companyId} currency={currency} canManage={canManage} />
    </div>
  );
}

const firstOfNextMonth = () => {
  const d = new Date();
  const next = new Date(d.getFullYear(), d.getMonth() + 1, 1);
  return `${next.getFullYear()}-${String(next.getMonth() + 1).padStart(2, '0')}`;
};
const thisMonth = () => {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`;
};
const today = () => {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
};

function ProvisionSection({
  companyId, companyName, currency, canManage, canManageRates,
}: { companyId: string; companyName: string; currency: string | null; canManage: boolean; canManageRates: boolean }) {
  const t = useT();
  const f = useFormat();
  const [data, setData] = useState<EosbProvisionSummary | null>(null);
  const [loadError, setLoadError] = useState('');
  const [switchMonth, setSwitchMonth] = useState(firstOfNextMonth());
  const [busy, setBusy] = useState('');
  const [actionError, setActionError] = useState('');
  const [notice, setNotice] = useState('');
  const [showEmployees, setShowEmployees] = useState(false);
  const [form, setForm] = useState({ period: thisMonth(), amount: '', reportReference: '', reason: '' });

  const load = useCallback(() => {
    setData(null);
    setLoadError('');
    financeGlAdjustmentsApi.provision(companyId)
      .then(setData)
      .catch((err) => setLoadError(apiErrorReason(err, t('The end-of-service provision could not be loaded.'))));
  }, [companyId, t]);
  useEffect(() => { load(); setShowEmployees(false); setNotice(''); setActionError(''); }, [load]);

  const cur = currency ?? undefined;
  const money = (n: number) => f.money(n, cur);

  const run = async (key: string, call: () => Promise<unknown>, done: string) => {
    setBusy(key);
    setActionError('');
    setNotice('');
    try { await call(); setNotice(done); load(); }
    catch (err) { setActionError(apiErrorReason(err, t('That could not be saved.'))); }
    finally { setBusy(''); }
  };

  const setProvisioning = (on: boolean) => {
    if (!data) return;
    const body = { rateKey: EOSB_PROVISIONING_KEY, rateValue: on ? 'StatutoryMonthly' : 'Off', effectiveFrom: `${switchMonth}-01` };
    const rateId = data.provisioning.currentRateId;
    return run(on ? 'on' : 'off',
      () => rateId ? financeRatesApi.updateCompanyRate(rateId, body) : financeRatesApi.createCompanyRate(body, companyId),
      on ? t('Monthly provisioning will start from {month}.', { month: switchMonth })
         : t('Monthly provisioning will stop from {month}.', { month: switchMonth }));
  };

  const amount = Number(form.amount);
  const trueUpValid = /^\d{4}-\d{2}$/.test(form.period) && form.amount.trim() !== '' && Number.isFinite(amount) && amount !== 0
    && form.reportReference.trim() !== '' && form.reason.trim() !== '';

  if (loadError) {
    return (
      <section className="surface p-4" aria-labelledby="eosb-heading">
        <h3 id="eosb-heading" className="text-sm font-semibold text-slate-900 dark:text-white">{t('End-of-service provision')}</h3>
        <div role="alert" className="mt-2 text-xs text-rose-600 dark:text-rose-400" data-testid="eosb-load-error">
          {loadError} <button type="button" className="underline" onClick={load}>{t('Try again')}</button>
        </div>
      </section>
    );
  }
  if (!data) {
    return (
      <section className="surface p-4" aria-labelledby="eosb-heading">
        <h3 id="eosb-heading" className="text-sm font-semibold text-slate-900 dark:text-white">{t('End-of-service provision')}</h3>
        <p className="mt-2 text-xs text-slate-400" data-testid="eosb-loading">{t('Loading the end-of-service provision…')}</p>
      </section>
    );
  }

  const p = data.provisioning;
  const scheduled = p.latestEffectiveFrom && p.latestEffectiveFrom !== p.effectiveFrom ? p.latestEffectiveFrom : null;

  return (
    <section className="surface space-y-4 p-4" aria-labelledby="eosb-heading" data-testid="eosb-panel">
      <div>
        <h3 id="eosb-heading" className="text-sm font-semibold text-slate-900 dark:text-white">{t('End-of-service provision')}</h3>
        <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
          {t('Each month, when payroll is locked, the end-of-service amount every employee has earned so far is measured, and only the change is booked: expense {expense}, provision {provision}.',
            { expense: data.expenseAccount, provision: data.provisionAccount })}
        </p>
      </div>

      {/* 1. On / off */}
      <div className="rounded-lg border border-slate-200 p-3 text-xs dark:border-white/10" data-testid="eosb-switch">
        <p className="font-semibold text-slate-800 dark:text-slate-100">
          {p.enabled
            ? t('Monthly provisioning is on for {company} since {date}.', { company: companyName, date: p.effectiveFrom ? f.date(p.effectiveFrom) : '—' })
            : t('Monthly provisioning is off for {company}. Payroll journals do not include an end-of-service provision.', { company: companyName })}
        </p>
        {scheduled && (
          <p className="mt-1 text-slate-600 dark:text-slate-300">
            {p.latestValue === 'StatutoryMonthly'
              ? t('Scheduled: provisioning starts on {date}.', { date: f.date(scheduled) })
              : t('Scheduled: provisioning stops on {date}.', { date: f.date(scheduled) })}
          </p>
        )}
        {p.inheritedFromTenant && <p className="mt-1 text-slate-500">{t('This setting comes from the group default.')}</p>}
        <p className="mt-1 text-slate-500">
          {t('Measured as if the employer ended the contract at each month end (the full Article 84 award). If someone resigns and receives less, the unused provision is released when their settlement is approved.')}
        </p>
        {canManageRates ? (
          <div className="mt-2 flex flex-wrap items-end gap-2">
            <label className="font-medium text-slate-600 dark:text-slate-300">
              {t('From month')}
              <input type="month" className="input mt-1" value={switchMonth} onChange={(e) => setSwitchMonth(e.target.value)} data-testid="eosb-switch-month" />
            </label>
            {p.enabled || p.latestValue === 'StatutoryMonthly' ? (
              <button type="button" className="btn-secondary h-8 px-3 text-xs" disabled={!switchMonth || !!busy} onClick={() => setProvisioning(false)}>
                {busy === 'off' ? t('Saving…') : t('Stop monthly provisioning')}
              </button>
            ) : (
              <button type="button" className="btn-primary h-8 px-3 text-xs" disabled={!switchMonth || !!busy} onClick={() => setProvisioning(true)}>
                {busy === 'on' ? t('Saving…') : t('Start monthly provisioning')}
              </button>
            )}
          </div>
        ) : (
          <p className="mt-2 text-slate-500">{t('Only someone who manages company rates can switch provisioning on or off.')}</p>
        )}
      </div>

      {actionError && <p role="alert" className="rounded-lg bg-rose-50 px-3 py-2 text-xs text-rose-700 dark:bg-rose-500/10 dark:text-rose-300" data-testid="eosb-error">{actionError}</p>}
      {notice && <p role="status" className="rounded-lg bg-emerald-50 px-3 py-2 text-xs text-emerald-800 dark:bg-emerald-500/10 dark:text-emerald-300" data-testid="eosb-notice">{notice}</p>}

      {/* 2. Figures, each with its definition */}
      <dl className="grid gap-3 sm:grid-cols-3" data-testid="eosb-figures">
        <Figure label={t('Provision in the books')} value={money(data.glBalance)}
          definition={t('Credits minus debits on {account} for this company, including approved actuarial adjustments.', { account: data.provisionAccount })} />
        <Figure label={t("Employees' measured obligation")} value={money(data.employeeObligation)}
          definition={t('The latest month-end measurement for each employee still provided for.')} />
        <Figure label={t('Actuarial adjustments')} value={money(data.trueUpLayer)}
          definition={t('Approved true-ups from an actuarial report, on top of the statutory measurement.')} />
      </dl>

      {data.employees.length === 0 ? (
        <p className="text-xs text-slate-500" data-testid="eosb-no-employees">
          {p.enabled
            ? t('No payroll has been locked since provisioning started, so no employee has been measured yet.')
            : t('No employee has been measured yet.')}
        </p>
      ) : (
        <div>
          <button type="button" className="btn-secondary h-8 px-3 text-xs" aria-expanded={showEmployees} aria-controls="eosb-employees"
            onClick={() => setShowEmployees((v) => !v)}>
            {showEmployees
              ? t('Hide employees')
              : t('{count, plural, one {Show the # employee behind these figures} other {Show the # employees behind these figures}}', { count: data.employees.length })}
          </button>
          {showEmployees && (
            <div className="mt-2 overflow-x-auto" id="eosb-employees">
              <table className="w-full text-xs" data-testid="eosb-employees">
                <thead>
                  <tr className="text-start text-slate-500">
                    <th scope="col" className="py-1 text-start font-medium">{t('Employee')}</th>
                    <th scope="col" className="py-1 text-start font-medium">{t('Last measured')}</th>
                    <th scope="col" className="py-1 text-end font-medium">{t('Years of service')}</th>
                    <th scope="col" className="py-1 text-end font-medium">{t('Wage it is measured on')}</th>
                    <th scope="col" className="py-1 text-end font-medium">{t('Obligation')}</th>
                    <th scope="col" className="py-1 text-end font-medium">{t('Provided in the books')}</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100 dark:divide-white/5">
                  {data.employees.map((e) => (
                    <tr key={e.employeeId}>
                      <td className="py-1.5"><span className="font-mono">{e.employeeCode}</span> · {e.employeeName}</td>
                      <td className="py-1.5 font-mono">{e.lastMeasuredPeriod}</td>
                      <td className="py-1.5 text-end">{f.number(e.serviceYears, { maximumFractionDigits: 2 })}</td>
                      <td className="py-1.5 text-end">{money(e.wageBase)}</td>
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

      {/* 3. Actuarial true-up */}
      <div className="space-y-2 border-t border-slate-100 pt-3 dark:border-white/10">
        <h4 className="text-xs font-semibold text-slate-800 dark:text-slate-100">{t('Adjust to an actuarial valuation')}</h4>
        <p className="text-xs text-slate-500">
          {t('Enter the difference the actuary reported: a positive amount raises the provision, a negative amount lowers it. Nothing is posted until a second person approves.')}
        </p>
        {canManage && (
          <div className="flex flex-wrap items-end gap-2 text-xs" data-testid="eosb-trueup-form">
            <label className="font-medium text-slate-600 dark:text-slate-300">
              {t('Month')}
              <input type="month" className="input mt-1" value={form.period} onChange={(e) => setForm({ ...form, period: e.target.value })} />
            </label>
            <label className="font-medium text-slate-600 dark:text-slate-300">
              {t('Amount')}
              <input type="number" step="0.01" className="input mt-1 w-32" value={form.amount} onChange={(e) => setForm({ ...form, amount: e.target.value })} />
            </label>
            <label className="font-medium text-slate-600 dark:text-slate-300">
              {t('Actuarial report reference')}
              <input className="input mt-1" maxLength={200} value={form.reportReference} onChange={(e) => setForm({ ...form, reportReference: e.target.value })} />
            </label>
            <label className="flex-1 font-medium text-slate-600 dark:text-slate-300">
              {t('Reason')}
              <input className="input mt-1 w-full" maxLength={1000} value={form.reason} onChange={(e) => setForm({ ...form, reason: e.target.value })} />
            </label>
            <button type="button" className="btn-primary h-8 px-3 text-xs" disabled={!trueUpValid || !!busy}
              onClick={() => run('trueup', () => financeGlAdjustmentsApi.requestTrueUp({
                companyId, period: form.period, amount, reportReference: form.reportReference.trim(), reason: form.reason.trim(),
              }).then(() => setForm({ period: form.period, amount: '', reportReference: '', reason: '' })), t('Sent for approval. A second person must approve it before it posts.'))}>
              {busy === 'trueup' ? t('Sending…') : t('Request adjustment')}
            </button>
          </div>
        )}
        <AdjustmentList requests={data.trueUps} currency={cur} onChanged={load} emptyText={t('No actuarial adjustment has been requested.')} />
      </div>
    </section>
  );
}

function Figure({ label, value, definition }: { label: string; value: string; definition: string }) {
  return (
    <div className="rounded-lg border border-slate-200 p-3 dark:border-white/10">
      <dt className="text-[11px] font-semibold text-slate-500">{label}</dt>
      <dd className="mt-1 text-base font-semibold text-slate-900 dark:text-white">{value}</dd>
      <dd className="mt-1 text-[11px] leading-snug text-slate-500">{definition}</dd>
    </div>
  );
}

function LegacyAbsenceSection({ companyId, currency, canManage }: { companyId: string; currency: string | null; canManage: boolean }) {
  const t = useT();
  const f = useFormat();
  const [data, setData] = useState<LegacyAbsenceSummary | null>(null);
  const [loadError, setLoadError] = useState('');
  const [open, setOpen] = useState<string | null>(null);
  const [form, setForm] = useState({ accountingDate: today(), targetAccountCode: '', reason: '' });
  const [busy, setBusy] = useState(false);
  const [actionError, setActionError] = useState('');
  const [notice, setNotice] = useState('');
  const cur = currency ?? undefined;

  const load = useCallback(() => {
    setData(null);
    setLoadError('');
    financeGlAdjustmentsApi.legacyAbsence(companyId)
      .then(setData)
      .catch((err) => setLoadError(apiErrorReason(err, t('The old absence-deduction balances could not be loaded.'))));
  }, [companyId, t]);
  useEffect(() => { load(); setOpen(null); setNotice(''); setActionError(''); }, [load]);

  const submit = async (sourceAccount: string) => {
    setBusy(true);
    setActionError('');
    setNotice('');
    try {
      await financeGlAdjustmentsApi.requestLegacyReclass({
        companyId, sourceAccount, accountingDate: form.accountingDate,
        targetAccountCode: form.targetAccountCode.trim() || null, reason: form.reason.trim(),
      });
      setOpen(null);
      setForm({ accountingDate: today(), targetAccountCode: '', reason: '' });
      setNotice(t('Sent for approval. A second person must approve it before it posts.'));
      load();
    } catch (err) {
      setActionError(apiErrorReason(err, t('That could not be saved.')));
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="surface space-y-3 p-4" aria-labelledby="legacy-absence-heading" data-testid="legacy-absence">
      <div>
        <h3 id="legacy-absence-heading" className="text-sm font-semibold text-slate-900 dark:text-white">
          {t('Move balances booked under the old absence-deduction accounts')}
        </h3>
        <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
          {t('Attendance and unpaid-leave deductions used to be booked as amounts owed (2104 and 2105). Nobody is owed them: they reduce payroll cost, and new payrolls now book them that way. What is still sitting in the old accounts is only moved when you ask and a second person approves.')}
        </p>
      </div>

      {actionError && <p role="alert" className="rounded-lg bg-rose-50 px-3 py-2 text-xs text-rose-700 dark:bg-rose-500/10 dark:text-rose-300" data-testid="legacy-error">{actionError}</p>}
      {notice && <p role="status" className="rounded-lg bg-emerald-50 px-3 py-2 text-xs text-emerald-800 dark:bg-emerald-500/10 dark:text-emerald-300">{notice}</p>}

      {loadError ? (
        <div role="alert" className="text-xs text-rose-600 dark:text-rose-400" data-testid="legacy-load-error">
          {loadError} <button type="button" className="underline" onClick={load}>{t('Try again')}</button>
        </div>
      ) : data === null ? (
        <p className="text-xs text-slate-400">{t('Loading the old absence-deduction balances…')}</p>
      ) : (
        <>
          {data.accounts.length === 0 ? (
            <p className="text-xs text-slate-500" data-testid="legacy-empty">{t('Nothing to move: the old absence-deduction accounts are empty for this company.')}</p>
          ) : (
            <ul className="divide-y divide-slate-100 text-xs dark:divide-white/5">
              {data.accounts.map((a) => (
                <li key={a.account} className="py-2" data-testid={`legacy-${a.code}`}>
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="font-semibold">{a.account}</span>
                    <span>{a.balance >= 0
                      ? t('{amount} still recorded as owed', { amount: f.money(a.balance, cur) })
                      : t('{amount} debit balance', { amount: f.money(-a.balance, cur) })}</span>
                    <span className="text-slate-500">{t('Suggested destination: {account}', { account: a.suggestedTarget })}</span>
                    {a.pendingRequestId ? (
                      <span className="ms-auto text-amber-700 dark:text-amber-300">{t('A move is waiting for approval')}</span>
                    ) : canManage && a.balance !== 0 ? (
                      <button type="button" className="btn-secondary ms-auto h-7 px-2 text-xs" onClick={() => setOpen(open === a.account ? null : a.account)}>
                        {t('Move this balance')}
                      </button>
                    ) : null}
                  </div>
                  {open === a.account && (
                    <div className="mt-2 flex flex-wrap items-end gap-2" data-testid="legacy-form">
                      <label className="font-medium text-slate-600 dark:text-slate-300">
                        {t('Accounting date')}
                        <input type="date" className="input mt-1" value={form.accountingDate} onChange={(e) => setForm({ ...form, accountingDate: e.target.value })} />
                      </label>
                      <label className="font-medium text-slate-600 dark:text-slate-300">
                        {t('Different destination account (optional)')}
                        <input className="input mt-1 w-40" maxLength={30} value={form.targetAccountCode} placeholder={t('For example, a retained-earnings account code')}
                          onChange={(e) => setForm({ ...form, targetAccountCode: e.target.value })} />
                      </label>
                      <label className="flex-1 font-medium text-slate-600 dark:text-slate-300">
                        {t('Reason')}
                        <input className="input mt-1 w-full" maxLength={1000} value={form.reason} onChange={(e) => setForm({ ...form, reason: e.target.value })} />
                      </label>
                      <button type="button" className="btn-primary h-8 px-3 text-xs" disabled={busy || !form.reason.trim() || !form.accountingDate}
                        onClick={() => void submit(a.account)}>
                        {busy ? t('Sending…') : t('Request the move')}
                      </button>
                    </div>
                  )}
                </li>
              ))}
            </ul>
          )}
          {data.unattributed.length > 0 && (
            <p className="text-[11px] text-slate-500" data-testid="legacy-unattributed">
              {t('{amount} sits in these accounts without a company (posted before companies were recorded on journals). It is not moved from here; correct it with a manual journal in your ERP.',
                { amount: f.money(data.unattributed.reduce((s, u) => s + u.balance, 0), cur) })}
            </p>
          )}
          <AdjustmentList requests={data.requests} currency={cur} onChanged={load} emptyText={t('No move has been requested.')} />
        </>
      )}
    </section>
  );
}

/** Requests with their status, and Approve / Reject only where the server says this viewer may decide. */
function AdjustmentList({ requests, currency, onChanged, emptyText }: {
  requests: GlAdjustment[]; currency?: string; onChanged: () => void; emptyText: string;
}) {
  const t = useT();
  const f = useFormat();
  const [rejecting, setRejecting] = useState<string | null>(null);
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState('');
  const [error, setError] = useState('');

  const statusLabel = (r: GlAdjustment) =>
    r.status === 'Approved' ? t('Approved and posted') : r.status === 'Rejected' ? t('Rejected') : t('Waiting for approval');
  const what = (r: GlAdjustment) => r.kind === 'EosbTrueUp'
    ? (r.amount >= 0
      ? t('Raise the provision by {amount} for {period} (report {reference})', { amount: f.money(r.amount, currency), period: r.period, reference: r.reportReference ?? '—' })
      : t('Lower the provision by {amount} for {period} (report {reference})', { amount: f.money(-r.amount, currency), period: r.period, reference: r.reportReference ?? '—' }))
    : t('Move {amount} from {source} to {target} on {date}', {
      amount: f.money(Math.abs(r.amount), currency), source: r.sourceAccount ?? '—', target: r.targetAccount ?? '—', date: f.date(r.accountingDate),
    });

  const decide = async (key: string, call: () => Promise<unknown>) => {
    setBusy(key);
    setError('');
    try { await call(); setRejecting(null); setNote(''); onChanged(); }
    catch (err) { setError(apiErrorReason(err, t('The decision could not be saved.'))); }
    finally { setBusy(''); }
  };

  if (requests.length === 0) return <p className="text-xs text-slate-500">{emptyText}</p>;
  return (
    <div className="space-y-1">
      {error && <p role="alert" className="rounded-lg bg-rose-50 px-3 py-2 text-xs text-rose-700 dark:bg-rose-500/10 dark:text-rose-300" data-testid="adjustment-error">{error}</p>}
      <ul className="divide-y divide-slate-100 text-xs dark:divide-white/5" data-testid="adjustment-requests">
        {requests.map((r) => (
          <li key={r.id} className="py-2" data-testid={`adjustment-${r.id}`}>
            <div className="flex flex-wrap items-center gap-2">
              <span className={`rounded-full px-2 py-0.5 font-semibold ${r.status === 'Pending'
                ? 'bg-amber-100 text-amber-800 dark:bg-amber-500/15 dark:text-amber-300'
                : r.status === 'Approved' ? 'bg-emerald-100 text-emerald-800 dark:bg-emerald-500/15 dark:text-emerald-300'
                  : 'bg-slate-200 text-slate-700 dark:bg-white/10 dark:text-slate-200'}`}>{statusLabel(r)}</span>
              <span className="font-medium text-slate-800 dark:text-slate-100">{what(r)}</span>
              <span className="text-slate-500">{t('Requested by {name}: {reason}', { name: r.requestedByName ?? '—', reason: r.reason })}</span>
              {r.decidedByName && (
                <span className="text-slate-500">{t('Decided by {name} on {date}', { name: r.decidedByName, date: r.decidedAtUtc ? f.dateTime(r.decidedAtUtc) : '—' })}</span>
              )}
              {r.status === 'Pending' && r.requestedByMe && <span className="text-slate-500">{t('You requested this, so someone else must approve it.')}</span>}
              {r.canApprove && (
                <span className="ms-auto flex gap-2">
                  <button type="button" className="btn-primary h-7 px-2 text-xs" disabled={!!busy}
                    onClick={() => void decide(`approve-${r.id}`, () => financeGlAdjustmentsApi.approve(r.id))}>
                    {busy === `approve-${r.id}` ? t('Posting…') : t('Approve and post')}
                  </button>
                  <button type="button" className="btn-secondary h-7 px-2 text-xs" disabled={!!busy}
                    onClick={() => { setRejecting(rejecting === r.id ? null : r.id); setNote(''); }}>
                    {t('Reject')}
                  </button>
                </span>
              )}
            </div>
            {rejecting === r.id && (
              <div className="mt-2 flex flex-wrap items-end gap-2">
                <label className="flex-1">
                  {t('Why reject it? (required)')}
                  <input className="input mt-1 w-full" maxLength={1000} value={note} onChange={(e) => setNote(e.target.value)} />
                </label>
                <button type="button" className="btn-primary h-8 px-3 text-xs" disabled={!note.trim() || !!busy}
                  onClick={() => void decide(`reject-${r.id}`, () => financeGlAdjustmentsApi.reject(r.id, note.trim()))}>
                  {busy === `reject-${r.id}` ? t('Saving…') : t('Confirm rejection')}
                </button>
              </div>
            )}
          </li>
        ))}
      </ul>
    </div>
  );
}

export default EosbProvisionPanel;
