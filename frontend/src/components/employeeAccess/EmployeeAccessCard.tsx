'use client';

import { useCallback, useEffect, useState } from 'react';
import { KeyRound } from 'lucide-react';
import { employeeAccessApi, type EmployeeAccessDto, type IssueWelcomeCodesResult } from '../../api/employeeAccess';
import { useLocale } from '../../contexts/LocaleContext';
import { useTenantSettings } from '../../contexts/TenantSettingsContext';
import { ACCESS_STATE_COPY, GENERIC_SKIP_KEY, dateLine, skipReasonKey } from '../../lib/employeeAccess';
import { StatusChip } from '../StatusChip';
import type { IssueOptions } from './useWelcomeCodes';

/**
 * Self-service on the employee profile: one status, one line of detail, one button.
 *
 *   Waiting for work email → Add work email      No access yet → Give access
 *   Code given             → Give new code (confirms: the old code stops working)
 *   Using KynexOne         → Reset sign-in (employees.access.reset only; confirms first)
 *   Access stopped / Needs admin help → no button, the reason instead
 */
export function EmployeeAccessCard({
  employeeId, employeeName, refreshKey, canIssue, canReset, issuing, onIssue, onAddWorkEmail,
}: {
  employeeId: number;
  employeeName: string;
  /** Bumped by the page after a slip view closes or a work email is saved. */
  refreshKey: number;
  /** employees.access.issue */
  canIssue: boolean;
  /** employees.access.reset */
  canReset: boolean;
  /** A code request is in flight (the button is disabled meanwhile). */
  issuing: boolean;
  onIssue: (employeeIds: number[], options: IssueOptions) => Promise<IssueWelcomeCodesResult | null>;
  onAddWorkEmail: () => void;
}) {
  const { t, locale } = useLocale();
  const { defaultTimezone } = useTenantSettings();
  const [access, setAccess] = useState<EmployeeAccessDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [failed, setFailed] = useState(false);
  // Which delivery the pending confirmation is for (Give new code can be emailed or printed).
  const [confirming, setConfirming] = useState<false | 'email' | 'print'>(false);
  const [skipNote, setSkipNote] = useState('');
  const lang = locale === 'ar' ? 'ar' : 'en';
  const when = (iso: string) => dateLine(iso, lang, defaultTimezone);

  const load = useCallback(async () => {
    setLoading(true);
    setFailed(false);
    try {
      setAccess(await employeeAccessApi.get(employeeId));
    } catch {
      setAccess(null);
      setFailed(true);
    } finally {
      setLoading(false);
    }
  }, [employeeId]);

  useEffect(() => { setConfirming(false); setSkipNote(''); void load(); }, [load, refreshKey]);

  const issue = async (delivery?: 'email' | 'print') => {
    setConfirming(false);
    setSkipNote('');
    const result = await onIssue([employeeId], { names: { [employeeId]: employeeName }, quietSkips: true, delivery, companyEmails: !!access?.emailDelivery });
    if (result && result.issued.length === 0 && result.skipped.length > 0) {
      const s = result.skipped[0];
      const key = skipReasonKey(s.reasonCode);
      setSkipNote(key ? t(key) : lang === 'en' && s.reason ? s.reason : t(GENERIC_SKIP_KEY));
      void load();
    }
  };

  if (loading && !access) {
    return <Shell title={t('Self-service')}><p className="text-xs text-slate-500">{t('Checking self-service…')}</p></Shell>;
  }
  if (failed || !access) {
    return (
      <Shell title={t('Self-service')}>
        <p className="text-xs text-slate-600 dark:text-slate-300">{t('Self-service could not be checked.')}</p>
        <button type="button" onClick={() => void load()} className="btn-secondary mt-2 h-8 px-3 text-xs">{t('Try again')}</button>
      </Shell>
    );
  }

  const copy = ACCESS_STATE_COPY[access.state] ?? ACCESS_STATE_COPY.blocked;
  const detail = detailLine(access, employeeName, t, when);

  // The one button (or, when the company can email codes, "Email sign-in code" first and
  // "Print sign-in slip" second), if this person may press it.
  const emailing = !!access.emailDelivery;
  const replacesCode = access.state === 'code_given';
  let primary: { label: string; delivery?: 'email' | 'print'; run?: () => void } | null = null;
  let secondary: { label: string; delivery: 'print' } | null = null;
  let confirmText = '';
  let confirmLabel = '';
  if (access.state === 'waiting_for_work_email' && canIssue) primary = { label: t('Add work email'), run: onAddWorkEmail };
  else if (access.canIssue && (access.state === 'not_started' || replacesCode) && canIssue) {
    primary = emailing ? { label: t('Email sign-in code'), delivery: 'email' } : { label: replacesCode ? t('Give new code') : t('Give access') };
    if (emailing) secondary = { label: t('Print sign-in slip'), delivery: 'print' };
    if (replacesCode) { confirmText = t('The old code will stop working. Continue?'); confirmLabel = t('Give new code'); }
  } else if (access.canIssue && access.state === 'active' && canReset) {
    primary = { label: t('Reset sign-in'), delivery: emailing ? 'print' : undefined };
    confirmText = t("Reset {name}'s sign-in? Their current password keeps working until they use the new code. Then print a new slip for them.", { name: employeeName });
    confirmLabel = t('Reset and print');
  }

  const press = (delivery?: 'email' | 'print', run?: () => void) => {
    if (run) { run(); return; }
    if (confirmText) { setConfirming(delivery ?? 'print'); return; }
    void issue(delivery);
  };

  return (
    <Shell title={t('Self-service')} pill={<StatusChip label={t(copy.label)} tone={copy.tone} dot />}>
      {detail && <p className="text-xs text-slate-600 dark:text-slate-300" data-testid="access-detail">{detail}</p>}
      {skipNote && <p role="alert" className="mt-2 rounded-md bg-amber-50 px-2 py-1.5 text-xs font-medium text-amber-900 dark:bg-amber-500/10 dark:text-amber-200">{skipNote}</p>}
      {primary && !confirming && (
        <div className="mt-2.5 flex flex-col gap-1.5">
          <button
            type="button"
            disabled={issuing}
            onClick={() => press(primary!.delivery, primary!.run)}
            className="btn-primary h-9 w-full justify-center px-3 text-sm disabled:opacity-60"
          >
            <KeyRound className="h-4 w-4" aria-hidden="true" />
            {issuing ? t('Preparing the slips…') : primary.label}
          </button>
          {secondary && (
            <button type="button" disabled={issuing} onClick={() => press(secondary!.delivery)} className="btn-secondary h-9 w-full justify-center px-3 text-sm disabled:opacity-60">
              {secondary.label}
            </button>
          )}
        </div>
      )}
      {confirmText && confirming && (
        <div className="mt-2.5 rounded-lg border border-amber-200 bg-amber-50 p-2.5 dark:border-amber-500/30 dark:bg-amber-500/10" role="alertdialog" aria-label={primary?.label}>
          <p className="text-xs text-amber-900 dark:text-amber-200">{confirmText}</p>
          <div className="mt-2 flex justify-end gap-2">
            <button type="button" onClick={() => setConfirming(false)} className="btn-secondary h-8 px-3 text-xs">{t('Cancel')}</button>
            <button type="button" disabled={issuing} onClick={() => void issue(emailing ? confirming || undefined : undefined)} className="btn-primary h-8 px-3 text-xs disabled:opacity-60">{confirmLabel}</button>
          </div>
        </div>
      )}
    </Shell>
  );
}

function detailLine(
  a: EmployeeAccessDto,
  name: string,
  t: (key: string, params?: Record<string, string | number>) => string,
  when: (iso: string) => string,
): string {
  switch (a.state) {
    case 'waiting_for_work_email':
      return t('Add a work email so {name} can sign in.', { name });
    case 'not_started':
      return a.lastCodeExpiredAtUtc ? t('The last code expired on {date}.', { date: when(a.lastCodeExpiredAtUtc) }) : t('No code has been given yet.');
    case 'code_given':
      if (!a.codeExpiresAtUtc) return '';
      return a.codeIssuedByName
        ? t('Code given by {issuer}. Valid until {date}.', { issuer: a.codeIssuedByName, date: when(a.codeExpiresAtUtc) })
        : t('Valid until {date}', { date: when(a.codeExpiresAtUtc) });
    case 'active':
      return a.lastSignInAtUtc ? t('Last signed in on {date}.', { date: when(a.lastSignInAtUtc) }) : '';
    case 'stopped':
      return /left|leav|offboard|terminat|resign|former/i.test(a.stoppedReason ?? '')
        ? t('Access stopped because the employee has left the company.')
        : t('Access stopped by a system admin.');
    case 'blocked':
      if (a.blockedCode === 'company_email_domain_missing') {
        return t('The company email ending (for example @evostel.com) is not set up. Ask your system admin to add it in company settings.');
      }
      if (a.blockedCode === 'email_belongs_to_existing_login' || a.blockedCode === 'email_belongs_to_former_employee') {
        return t('This email is already used to sign in by someone else. Ask your system admin to fix it.');
      }
      return t('This needs a system admin first.');
    default:
      return '';
  }
}

function Shell({ title, pill, children }: { title: string; pill?: React.ReactNode; children: React.ReactNode }) {
  return (
    <section className="rounded-lg border border-slate-200 p-3 dark:border-white/10" data-testid="employee-access-card" aria-label={title}>
      <div className="mb-1.5 flex flex-wrap items-center justify-between gap-2">
        <p className="text-xs font-bold uppercase text-slate-400">{title}</p>
        {pill}
      </div>
      {children}
    </section>
  );
}
