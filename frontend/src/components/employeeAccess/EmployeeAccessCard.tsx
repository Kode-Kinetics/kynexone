'use client';

import { useCallback, useEffect, useState } from 'react';
import { employeeAccessApi, type EmployeeAccessDto, type IssueWelcomeCodesResult } from '../../api/employeeAccess';
import { useLocale } from '../../contexts/LocaleContext';
import { useTenantSettings } from '../../contexts/TenantSettingsContext';
import { BLOCKED_REASON_KEYS, GENERIC_SKIP_KEY, STOPPED_REASON_KEYS, dateLine, deriveThreeStates, skipReasonKey } from '../../lib/employeeAccess';
import { AccessStatesPanel } from './AccessStatesPanel';
import type { IssueOptions } from './useWelcomeCodes';

/**
 * Self-service on the employee profile: loads the sign-in status and renders it as three separate
 * answers (Employee active, Login enabled, Password configured), see AccessStatesPanel.
 * Reset sign-in is one person at a time and needs employees.access.reset; it confirms first.
 */
export function EmployeeAccessCard({
  employeeId, employeeName, employeeStatus, workEmail, readiness, readinessLoading = false, canApprove, activating, onActivate, refreshKey, canIssue, canReset, isSelf = false, issuing, onIssue, onAddWorkEmail,
}: {
  employeeId: number;
  employeeName: string;
  employeeStatus: string;
  workEmail?: string | null;
  /** Null when readiness could not be loaded. */
  readiness: { blocking: ReadonlyArray<{ key: string }> } | null;
  readinessLoading?: boolean;
  /** employees.approve */
  canApprove: boolean;
  activating: boolean;
  onActivate(): void;
  /** Bumped by the page after a slip view closes or a work email is saved. */
  refreshKey: number;
  /** employees.access.issue */
  canIssue: boolean;
  /** employees.access.reset */
  canReset: boolean;
  /** This profile is the signed-in HR person's own: nobody gives themselves a code. */
  isSelf?: boolean;
  /** A code request is in flight (the button is disabled meanwhile). */
  issuing: boolean;
  onIssue: (employeeIds: number[], options: IssueOptions) => Promise<IssueWelcomeCodesResult | null>;
  onAddWorkEmail: () => void;
}) {
  const { t, locale } = useLocale();
  const say = (code: string | null | undefined) => t(skipReasonKey(code) ?? GENERIC_SKIP_KEY, { name: employeeName });
  const { defaultTimezone } = useTenantSettings();
  const [access, setAccess] = useState<EmployeeAccessDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [failed, setFailed] = useState(false);
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

  useEffect(() => { setSkipNote(''); void load(); }, [load, refreshKey]);

  const issue = async (delivery?: 'email' | 'print') => {
    setSkipNote('');
    const result = await onIssue([employeeId], { names: { [employeeId]: employeeName }, quietSkips: true, delivery, companyEmails: !!access?.emailDelivery });
    if (result && result.issued.length === 0 && result.skipped.length > 0) {
      setSkipNote(say(result.skipped[0].reasonCode));
      void load();
    }
  };

  const problem: 'loading' | 'failed' | null = access ? null : loading ? 'loading' : 'failed';

  const detail = access ? detailLine(access, employeeName, t, when) : '';
  // Something the API (or this screen) says stands in the way: the one sentence replaces the button.
  // Only people who can give access see why they can't right now; everyone else sees the state alone.
  const waitingCode = !access || !canIssue || access.state === 'stopped' || access.state === 'blocked' ? null
    : isSelf ? 'cannot_issue_for_self'
      : !access.canIssue && access.reasonCode && skipReasonKey(access.reasonCode) ? access.reasonCode
        : null;
  const states = deriveThreeStates({ status: employeeStatus, workEmail: workEmail ?? access?.workEmail }, access, readiness);

  return (
    <Shell title={t('Self-service')}>
      <AccessStatesPanel
        states={states}
        access={access}
        accessProblem={problem}
        onRetryAccess={() => void load()}
        detailRow={access && (access.state === 'stopped' || access.state === 'blocked') ? 'login' : 'password'}
        employeeName={employeeName}
        activation={{
          allowed: canApprove && employeeStatus !== 'Active',
          unavailable: !readiness,
          loading: readinessLoading && !readiness,
          blocked: (readiness?.blocking.length ?? 0) > 0,
          busy: activating,
          onActivate,
        }}
        canIssue={canIssue}
        canReset={canReset}
        waitingReason={waitingCode ? say(waitingCode) : null}
        issuing={issuing}
        detail={detail}
        onIssue={(delivery) => void issue(delivery)}
        onAddWorkEmail={onAddWorkEmail}
        workEmailNote={skipNote ? <p role="alert" className="mt-2 rounded-md bg-amber-50 px-2 py-1.5 text-xs font-medium text-amber-900 dark:bg-amber-500/10 dark:text-amber-200">{skipNote}</p> : undefined}
      />
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
    case 'stopped': {
      const key = STOPPED_REASON_KEYS[a.stoppedReason ?? ''];
      return key ? t(key) : '';
    }
    case 'blocked':
      return t(BLOCKED_REASON_KEYS[a.blockedCode ?? ''] ?? BLOCKED_REASON_KEYS[a.blockedReason ?? ''] ?? 'This needs a system admin first.');
    default:
      return '';
  }
}

function Shell({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section className="rounded-lg border border-slate-200 p-3 dark:border-white/10" data-testid="employee-access-card" aria-label={title}>
      <div className="mb-2">
        <p className="text-xs font-bold uppercase text-slate-500 dark:text-slate-400">{title}</p>
      </div>
      {children}
    </section>
  );
}
