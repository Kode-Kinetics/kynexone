'use client';

import { useCallback, useEffect, useId, useLayoutEffect, useMemo, useState } from 'react';
import { AlertTriangle, Pencil, Scale } from 'lucide-react';
import { payrollRulesApi, type PendingPayrollRuleChange } from '../../api/payrollRules';
import { useAuth } from '../../contexts/AuthContext';
import { useLocale } from '../../contexts/LocaleContext';
import { apiErrorReason } from '../../api/client';
import { useT } from '../../hooks/useT';
import { useFormat } from '../../hooks/useFormat';
import { useCompany } from '../../contexts/CompanyContext';
import {
  CASHOUT_NOTICE_KEY, EMPTY_RULE_FORM, PAYROLL_RULE_KEYS, acknowledgedWarningCodes, bilingualReason, changedWarnings, effectiveFromOk,
  ruleFormFromViews, ruleFormToChanges, todayIso, validateRuleForm, warningsForForm,
  type PayrollRuleErrors, type PayrollRuleForm, type PayrollRuleKey, type PayrollRuleView,
} from '../../lib/payrollRules';
import { LoadFailedNotice } from '../ui/LoadFailedRow';
import { ConfirmDialog, type ConfirmState } from '../ui/ConfirmDialog';
import { StatusChip } from '../StatusChip';
import { SettingsDrawer } from '../companySetup/rules/SettingsDrawer';

const input = 'w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 focus:border-sapphire focus:outline-none dark:border-white/10 dark:bg-white/5 dark:text-white';

const OPTIONS: Partial<Record<PayrollRuleKey, { value: string; label: string }[]>> = {
  rest_day_work_mode: [{ value: 'PaidPremium', label: 'Pay as overtime at the premium rate' }, { value: 'CompensatoryDay', label: 'Give a compensatory day off' }],
  leave_cashout_on_exit: [{ value: 'true', label: 'Pay out untaken annual leave' }, { value: 'false', label: 'Do not pay out untaken annual leave' }],
  leave_cashout_basis: [{ value: 'Gross', label: 'Full monthly package' }, { value: 'Basic', label: 'Basic wage' }, { value: 'BasicHousing', label: 'Basic wage plus housing' }],
  proration_basis: [{ value: 'Calendar30', label: '30-day month' }, { value: 'CalendarActual', label: 'Actual days in the month' }],
  absence_deduction_base: [{ value: 'Basic', label: 'Basic wage' }, { value: 'Gross', label: 'Full monthly package' }],
};

/** Plain-words warning per server warning code. */
const WARNING_TEXT: Record<string, string> = {
  BELOW_OVERTIME_FLOOR: 'This is below the statutory 150% (Art. 107). It is allowed, and recorded against the person who confirms it.',
  LEAVE_CASHOUT_OFF: 'Saudi law pays out accrued, untaken annual leave when employment ends (Art. 111). Turning this off withholds that pay, and is recorded against the person who confirms it.',
  PROBATION_OVER_90: 'This is longer than the 90-day statutory probation (Art. 53). Up to 180 days needs the employee’s written agreement. It is recorded against the person who confirms it.',
  PROBATION_OVER_180: 'This is longer than the 180-day statutory maximum even with written agreement (Art. 53). It is recorded against the person who confirms it.',
};

const LABELS: Record<PayrollRuleKey, { label: string; help: string }> = {
  rest_day_work_mode: { label: 'Work on a rest day or holiday', help: 'What an employee gets for hours worked on a weekly rest day or a company holiday.' },
  rest_day_multiplier: { label: 'Rest-day pay multiplier', help: 'Multiplier on the hourly rate for rest-day overtime, for example 1.5. Leave empty to keep the platform rate.' },
  holiday_multiplier: { label: 'Holiday pay multiplier', help: 'Multiplier on the hourly rate for work on a company holiday. Leave empty to keep the platform rate.' },
  leave_cashout_on_exit: { label: 'Annual leave when employment ends', help: 'Whether untaken annual leave is paid in the final settlement. Leave empty for the default: paid.' },
  leave_cashout_basis: { label: 'Leave payout is calculated on', help: 'The monthly wage divided by 30 to price one day. Leave empty for the full monthly package.' },
  max_probation_days: { label: 'Longest probation (days)', help: 'Offers with a longer probation are refused. Leave empty to keep the previous limit of twelve months.' },
  proration_basis: { label: 'Part-month pay is calculated on', help: 'How joiners and leavers are paid for a part month. Leave empty to keep the company’s current basis.' },
  absence_deduction_base: { label: 'An absent day is deducted from', help: 'The wage an absent day is priced on. Leave empty for basic wage.' },
};

function display(key: PayrollRuleKey, value: string | null, t: ReturnType<typeof useT>): string {
  if (value == null) return t('Platform default');
  const option = OPTIONS[key]?.find(o => o.value === value);
  if (option) return t(option.label);
  if (key === 'max_probation_days') return t('{n} days', { n: value });
  return t('{n}× the hourly rate', { n: value });
}

export function PayrollRulesPanel({ onDirtyChange }: { onDirtyChange?: (dirty: boolean) => void }) {
  const t = useT();
  const format = useFormat();
  const { companies, selectedCompanyId } = useCompany();
  const [chosen, setChosen] = useState<string>('');
  const companyId = selectedCompanyId ?? (chosen || companies[0]?.id || '');
  const { user, hasPermission } = useAuth();
  const [rules, setRules] = useState<PayrollRuleView[]>([]);
  const [pending, setPending] = useState<PendingPayrollRuleChange[]>([]);
  const [annualLeaveTypeFound, setAnnualLeaveTypeFound] = useState(true);
  const [cashOutNoticeSeen, setCashOutNoticeSeen] = useState(true);
  const [rejecting, setRejecting] = useState<{ id: string; note: string } | null>(null);
  const [deciding, setDeciding] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<unknown>(null);
  const [editing, setEditing] = useState(false);
  const [dirty, setDirty] = useState(false);
  const [notice, setNotice] = useState<{ kind: 'ok' | 'error'; text: string } | null>(null);
  const [confirm, setConfirm] = useState<ConfirmState | null>(null);

  useLayoutEffect(() => { onDirtyChange?.(editing && dirty); return () => onDirtyChange?.(false); }, [editing, dirty, onDirtyChange]);
  useEffect(() => {
    try { setCashOutNoticeSeen(window.localStorage.getItem(CASHOUT_NOTICE_KEY) === '1'); } catch { setCashOutNoticeSeen(false); }
  }, []);
  const dismissCashOutNotice = () => {
    try { window.localStorage.setItem(CASHOUT_NOTICE_KEY, '1'); } catch { /* the notice simply shows again */ }
    setCashOutNoticeSeen(true);
  };

  const load = useCallback(async () => {
    if (!companyId) { setLoading(false); return; }
    setLoading(true);
    try {
      const view = await payrollRulesApi.get(companyId);
      setRules(view.rules);
      setPending(view.pending ?? []);
      setAnnualLeaveTypeFound(view.annualLeaveTypeFound ?? true);
      setLoadError(null);
    } catch (error) {
      setRules([]);
      setPending([]);
      setLoadError(error);
    } finally {
      setLoading(false);
    }
  }, [companyId]);
  useEffect(() => { void load(); }, [load]);

  const decide = async (id: string, decision: 'approve' | 'reject', note?: string) => {
    setDeciding(id);
    setNotice(null);
    try {
      if (decision === 'approve') await payrollRulesApi.approve(id);
      else await payrollRulesApi.reject(id, note ?? '');
      setRejecting(null);
      setNotice({ kind: 'ok', text: decision === 'approve' ? t('Change approved and applied.') : t('Change rejected.') });
      await load();
    } catch (error) {
      setNotice({ kind: 'error', text: apiErrorReason(error, t('The decision could not be saved. Try again.')) });
    } finally {
      setDeciding(null);
    }
  };

  return (
    <div className="space-y-4" data-testid="payroll-rules">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <p className="max-w-2xl text-sm leading-6 text-slate-600 dark:text-slate-300">
          {t('These are your company’s own payroll choices. Where Saudi law sets a minimum, you can still choose a lower value: you will see a warning and must confirm it, and who confirmed and when is recorded.')}
        </p>
        <div className="flex items-end gap-2">
          {selectedCompanyId == null && companies.length > 1 && (
            <label className="text-xs font-medium text-slate-700 dark:text-slate-300">
              {t('Company')}
              <select className={`${input} mt-1`} value={companyId} onChange={event => setChosen(event.target.value)}>
                {companies.map(company => <option key={company.id} value={company.id}>{company.name}</option>)}
              </select>
            </label>
          )}
          <button type="button" className="btn-primary shrink-0" disabled={loadError != null || loading || !companyId} onClick={() => { setNotice(null); setEditing(true); }}>
            <Pencil className="h-4 w-4" aria-hidden="true" />{t('Edit payroll rules')}
          </button>
        </div>
      </div>

      {notice && (
        <div role={notice.kind === 'error' ? 'alert' : 'status'}
          className={`rounded-lg border px-4 py-3 text-sm ${notice.kind === 'error' ? 'border-rose-200 bg-rose-50 text-rose-700' : 'border-emerald-200 bg-emerald-50 text-emerald-700'}`}>
          {notice.text}
        </div>
      )}

      {!cashOutNoticeSeen && !loading && loadError == null && (
        <div role="status" data-testid="payroll-rules-cashout-notice" className="flex items-start justify-between gap-3 rounded-lg border border-sky-200 bg-sky-50 px-4 py-3 text-sm text-sky-900 dark:border-sky-500/30 dark:bg-sky-500/10 dark:text-sky-100">
          <p>{t('Untaken annual leave is now paid out when employment ends unless you turn it off here (Saudi Labour Law Art. 111). A cap set on the annual-leave policy still limits the payout.')}</p>
          <button type="button" className="btn-secondary h-8 shrink-0 px-3 text-xs" onClick={dismissCashOutNotice}>{t('Got it')}</button>
        </div>
      )}
      {!annualLeaveTypeFound && !loading && loadError == null && (
        <div role="alert" data-testid="payroll-rules-no-annual-type" className="rounded-lg border border-amber-300 bg-amber-50 px-4 py-3 text-sm text-amber-900 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-100">
          {t('No annual-leave type was found (code ANNUAL or category Annual). Leave payout on exit pays nothing until one exists. Set the category of your annual leave type to Annual.')}
        </div>
      )}

      {pending.length > 0 && !loading && loadError == null && (
        <section className="surface space-y-3 p-4" data-testid="payroll-rules-pending" aria-label={t('Changes waiting for approval')}>
          <h3 className="font-semibold text-slate-900 dark:text-white">{t('Changes waiting for approval')}</h3>
          {pending.map(item => {
            const mine = item.requestedBy != null && item.requestedBy === user?.id;
            const canDecide = hasPermission('payroll.approve') && !mine;
            return (
              <article key={item.id} className="rounded-lg border border-slate-200 p-3 dark:border-white/10" data-testid={`payroll-rule-pending-${item.id}`}>
                <ul className="space-y-1 text-sm text-slate-800 dark:text-slate-200">
                  {item.changes.map(change => (
                    <li key={change.key}>
                      <span className="font-medium">{t(LABELS[change.key]?.label ?? change.key)}</span>{': '}
                      {display(change.key, change.from, t)} → {display(change.key, change.to, t)}
                    </li>
                  ))}
                </ul>
                <p className="mt-2 text-xs text-slate-500 dark:text-slate-400">
                  {t('From {date}.', { date: item.effectiveFrom })}{item.reason ? ` ${t('Reason: {reason}', { reason: item.reason })}` : ''}
                </p>
                {canDecide ? (
                  <div className="mt-3 space-y-2">
                    {rejecting?.id === item.id && (
                      <label className="block text-xs font-medium text-slate-700 dark:text-slate-300">
                        {t('Why are you rejecting this change?')}
                        <input className={`${input} mt-1`} value={rejecting.note} maxLength={500} onChange={event => setRejecting({ id: item.id, note: event.target.value })} />
                      </label>
                    )}
                    <div className="flex flex-wrap gap-2">
                      <button type="button" className="btn-primary h-8 px-3 text-xs" disabled={deciding === item.id}
                        onClick={() => { void decide(item.id, 'approve'); }}>{t('Approve')}</button>
                      {rejecting?.id === item.id ? (
                        <button type="button" className="btn-secondary h-8 px-3 text-xs" disabled={deciding === item.id || rejecting.note.trim().length < 5}
                          onClick={() => { void decide(item.id, 'reject', rejecting.note.trim()); }}>{t('Confirm rejection')}</button>
                      ) : (
                        <button type="button" className="btn-secondary h-8 px-3 text-xs" disabled={deciding === item.id}
                          onClick={() => setRejecting({ id: item.id, note: '' })}>{t('Reject')}</button>
                      )}
                    </div>
                  </div>
                ) : (
                  <p className="mt-3 text-xs font-medium text-amber-700 dark:text-amber-300">
                    {mine ? t('You made this change. A different payroll approver has to decide it.') : t('Waiting for a payroll approver.')}
                  </p>
                )}
              </article>
            );
          })}
        </section>
      )}

      {loading ? <p role="status" className="text-sm text-slate-400">{t('Loading…')}</p>
        : loadError != null ? <div className="surface"><LoadFailedNotice error={loadError} onRetry={() => { void load(); }} /></div>
        : (
          <div className="grid gap-4 md:grid-cols-2" data-testid="payroll-rules-list">
            {PAYROLL_RULE_KEYS.map(key => {
              const view = rules.find(r => r.key === key);
              return (
                <article key={key} className="surface p-4" data-testid={`payroll-rule-${key}`}>
                  <div className="flex items-start justify-between gap-3">
                    <div className="min-w-0">
                      <Scale className="mb-2 h-5 w-5 text-sapphire dark:text-cyanAccent" aria-hidden="true" />
                      <h3 className="font-semibold text-slate-900 dark:text-white">{t(LABELS[key].label)}</h3>
                    </div>
                    <StatusChip label={view?.isSet ? t('Your choice') : t('Platform default')} tone={view?.isSet ? 'emerald' : 'slate'} dot />
                  </div>
                  <p className="mt-2 text-sm font-medium text-slate-800 dark:text-slate-200">{display(key, view?.isSet ? view.value : null, t)}</p>
                  {view?.hasUpcoming && view.upcomingFrom && (
                    <p className="mt-1 text-xs text-slate-500 dark:text-slate-400" data-testid={`payroll-rule-${key}-upcoming`}>
                      {t('From {date}: {value}', { date: view.upcomingFrom, value: display(key, view.upcomingValue ?? null, t) })}
                    </p>
                  )}
                  {view?.warningCode && (
                    <p className="mt-2 flex items-start gap-1.5 text-xs text-amber-700 dark:text-amber-300" data-testid={`payroll-rule-${key}-warning`}>
                      <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden="true" />
                      <span>
                        {t(WARNING_TEXT[view.warningCode] ?? view.warning ?? '')}
                        {view.acknowledged && view.acknowledgedAtUtc ? ` ${t('Confirmed on {date}.', { date: format.dateTime(view.acknowledgedAtUtc) })}` : ''}
                      </span>
                    </p>
                  )}
                </article>
              );
            })}
          </div>
        )}

      <SettingsDrawer open={editing} title={t('Edit payroll rules')} description={t('Changes apply to payroll processed from now on.')}
        onClose={() => setEditing(false)} testId="payroll-rules-editor">
        {editing && (
          <RulesEditor views={rules} companyId={companyId} onDirty={setDirty} onCancel={() => setEditing(false)}
            onConfirm={setConfirm}
            onSaved={async status => {
              setEditing(false); setConfirm(null);
              setNotice({ kind: 'ok', text: status === 'pending' ? t('Sent for approval. It applies once a different payroll approver approves it.') : t('Payroll rules saved.') });
              await load();
            }} />
        )}
      </SettingsDrawer>
      <ConfirmDialog state={confirm} onCancel={() => setConfirm(null)} />
    </div>
  );
}

function RulesEditor({ views, companyId, onDirty, onCancel, onConfirm, onSaved }: {
  views: PayrollRuleView[]; companyId: string; onDirty: (dirty: boolean) => void; onCancel: () => void;
  onConfirm: (state: ConfirmState | null) => void; onSaved: (status: 'applied' | 'pending') => void | Promise<void>;
}) {
  const t = useT();
  const { locale } = useLocale();
  const initial = useMemo(() => (views.length ? ruleFormFromViews(views) : EMPTY_RULE_FORM), [views]);
  const [form, setForm] = useState<PayrollRuleForm>(initial);
  const [errors, setErrors] = useState<PayrollRuleErrors>({});
  const [acknowledged, setAcknowledged] = useState(false);
  const [note, setNote] = useState('');
  const [saving, setSaving] = useState(false);
  const [serverError, setServerError] = useState('');
  const [effectiveFrom, setEffectiveFrom] = useState(todayIso());
  const idBase = useId();

  // Inline hints cover every field; the acknowledgement covers only the rules being changed now.
  const fieldWarnings = warningsForForm(form);
  const warnings = changedWarnings(form, initial);
  const changes = ruleFormToChanges(form, initial);
  useEffect(() => { onDirty(changes.length > 0); return () => onDirty(false); }, [changes.length, onDirty]);

  const set = (key: PayrollRuleKey, value: string) => {
    setForm(current => ({ ...current, [key]: value }));
    setErrors(current => ({ ...current, [key]: undefined }));
    setAcknowledged(false);
  };

  const submit = async () => {
    setSaving(true);
    setServerError('');
    try {
      const result = await payrollRulesApi.save({
        companyId, changes, acknowledge: warnings.length > 0 && acknowledged, note: note.trim() || undefined,
        effectiveFrom: effectiveFrom === todayIso() ? undefined : effectiveFrom,
        acknowledgedWarnings: warnings.length > 0 ? acknowledgedWarningCodes(form, initial) : undefined,
      });
      await onSaved(result.status);
    } catch (error) {
      onConfirm(null);
      setServerError(bilingualReason(error, locale, apiErrorReason(error, t('The payroll rules could not be saved. Try again.'))));
      setSaving(false);
    }
  };

  const save = () => {
    const found = validateRuleForm(form);
    setErrors(found);
    if (Object.keys(found).length > 0 || changes.length === 0) return;
    if (!effectiveFromOk(effectiveFrom)) { setServerError(t('Choose today or a later date.')); return; }
    if (warnings.length > 0) {
      if (!acknowledged || note.trim().length < 5) return;
      onConfirm({
        title: t('Save values below the statutory minimum?'),
        message: t('You are choosing values that are below what Saudi law sets. They will be saved with your name and the time.'),
        confirmLabel: t('Confirm and save'), onConfirm: submit, closeOnConfirm: false,
      });
      return;
    }
    void submit();
  };

  return (
    <form className="space-y-5" noValidate onSubmit={event => { event.preventDefault(); save(); }}>
      {PAYROLL_RULE_KEYS.map(key => {
        const id = `${idBase}-${key}`;
        const options = OPTIONS[key];
        const code = fieldWarnings.find(w => w.key === key)?.code;
        return (
          <div key={key}>
            <label htmlFor={id} className="mb-1 block text-xs font-medium text-slate-700 dark:text-slate-300">{t(LABELS[key].label)}</label>
            {options ? (
              <select id={id} className={input} value={form[key]} onChange={event => set(key, event.target.value)}>
                <option value="">{t('Platform default')}</option>
                {options.map(o => <option key={o.value} value={o.value}>{t(o.label)}</option>)}
              </select>
            ) : (
              <input id={id} className={input} value={form[key]} inputMode="decimal" onChange={event => set(key, event.target.value)}
                aria-invalid={errors[key] ? true : undefined} aria-describedby={`${id}-help`} />
            )}
            <p id={`${id}-help`} className={`mt-1 text-xs leading-5 ${errors[key] ? 'text-rose-600' : 'text-slate-500 dark:text-slate-400'}`}>{errors[key] ? t(errors[key]!) : t(LABELS[key].help)}</p>
            {code && <p className="mt-1 flex items-start gap-1.5 text-xs text-amber-700 dark:text-amber-300" role="status"><AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden="true" />{t(WARNING_TEXT[code])}</p>}
          </div>
        );
      })}

      <div>
        <label htmlFor={`${idBase}-from`} className="mb-1 block text-xs font-medium text-slate-700 dark:text-slate-300">{t('Takes effect from')}</label>
        <input id={`${idBase}-from`} type="date" className={input} value={effectiveFrom} min={todayIso()} onChange={event => setEffectiveFrom(event.target.value)} />
        <p className="mt-1 text-xs leading-5 text-slate-500 dark:text-slate-400">
          {t('A payroll period that began before this date keeps the rule it began with. A change that lowers pay is sent to a different payroll approver before it applies.')}
        </p>
      </div>

      {warnings.length === 0 && changes.length > 0 && (
        <label className="block text-xs font-medium text-slate-700 dark:text-slate-300">
          {t('Reason (needed if the change lowers pay)')}
          <input className={`${input} mt-1`} value={note} maxLength={500} onChange={event => setNote(event.target.value)} />
        </label>
      )}

      {warnings.length > 0 && (
        <fieldset className="space-y-2 rounded-xl border border-amber-300 bg-amber-50/60 p-4 dark:border-amber-500/30 dark:bg-amber-500/10">
          <legend className="px-1 text-xs font-semibold text-amber-800 dark:text-amber-200">{t('Below the statutory minimum')}</legend>
          <label className="flex items-start gap-2 text-sm text-slate-800 dark:text-slate-200">
            <input type="checkbox" className="mt-1" checked={acknowledged} onChange={event => setAcknowledged(event.target.checked)} />
            <span>{t('I understand these values are below the statutory minimum and I confirm them for this company.')}</span>
          </label>
          <label className="block text-xs font-medium text-slate-700 dark:text-slate-300">
            {t('Reason (required)')}
            <input className={`${input} mt-1`} value={note} maxLength={500} onChange={event => setNote(event.target.value)} required aria-required="true" />
          </label>
        </fieldset>
      )}

      {serverError && <p role="alert" className="rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">{serverError}</p>}

      <div className="flex justify-end gap-2 border-t border-slate-200/80 pt-4 dark:border-white/10">
        <button type="button" className="btn-secondary" onClick={onCancel} disabled={saving}>{t('Cancel')}</button>
        <button type="submit" className="btn-primary" disabled={saving || changes.length === 0 || (warnings.length > 0 && (!acknowledged || note.trim().length < 5))}>
          {saving ? t('Saving…') : t('Save changes')}
        </button>
      </div>
    </form>
  );
}
