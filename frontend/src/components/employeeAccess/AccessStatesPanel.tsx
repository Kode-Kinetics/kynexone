'use client';

import { useId, useState, type ReactNode } from 'react';
import { Ban, CheckCircle2, Circle, Clock, KeyRound } from 'lucide-react';
import type { EmployeeAccessDto } from '../../api/employeeAccess';
import { useLocale } from '../../contexts/LocaleContext';
import type { AccessTone, EmployeeRowState, LoginRowState, PasswordRowState, ThreeStates } from '../../lib/employeeAccess';
import { StatusChip } from '../StatusChip';

const ICON = 'h-3.5 w-3.5 shrink-0';
const EMPLOYEE_CHIP: Record<EmployeeRowState, { label: string; tone: AccessTone; icon: ReactNode }> = {
  not_active: { label: 'Not active yet', tone: 'slate', icon: <Circle className={ICON} aria-hidden="true" /> },
  awaiting_pay_approval: { label: 'Awaiting pay approval', tone: 'amber', icon: <Clock className={ICON} aria-hidden="true" /> },
  active: { label: 'Active', tone: 'emerald', icon: <CheckCircle2 className={ICON} aria-hidden="true" /> },
  suspended: { label: 'Employment suspended', tone: 'amber', icon: <Ban className={ICON} aria-hidden="true" /> },
  offboarded: { label: 'Offboarding in progress', tone: 'amber', icon: <Clock className={ICON} aria-hidden="true" /> },
  ended: { label: 'Employment ended', tone: 'slate', icon: <Circle className={ICON} aria-hidden="true" /> },
  inactive: { label: 'Employee inactive', tone: 'slate', icon: <Circle className={ICON} aria-hidden="true" /> },
};
const LOGIN_CHIP: Record<LoginRowState, { label: string; tone: AccessTone; icon: ReactNode }> = {
  no_login: { label: 'No login yet', tone: 'slate', icon: <Circle className={ICON} aria-hidden="true" /> },
  login_enabled: { label: 'Login enabled', tone: 'emerald', icon: <CheckCircle2 className={ICON} aria-hidden="true" /> },
  access_stopped: { label: 'Access stopped', tone: 'rose', icon: <Ban className={ICON} aria-hidden="true" /> },
};
const PASSWORD_CHIP: Record<PasswordRowState, { label: string; tone: AccessTone; icon: ReactNode }> = {
  not_set_up: { label: 'Not set up', tone: 'slate', icon: <Circle className={ICON} aria-hidden="true" /> },
  code_given: { label: 'Code given, not used', tone: 'blue', icon: <KeyRound className={ICON} aria-hidden="true" /> },
  password_set: { label: 'Password set', tone: 'emerald', icon: <CheckCircle2 className={ICON} aria-hidden="true" /> },
  unknown: { label: 'Password status unknown', tone: 'slate', icon: <Circle className={ICON} aria-hidden="true" /> },
};

export interface ActivationControl {
  /** employees.approve, and the employee is not active yet. */
  allowed: boolean;
  /** Readiness could not be loaded: the server's answer is unknown, so Activate waits. */
  unavailable: boolean;
  loading?: boolean;
  /** readiness.blocking.length > 0. The only rule that disables Activate besides a request in flight. */
  blocked: boolean;
  busy: boolean;
  onActivate(): void;
}

/**
 * Three separate answers about one person, never merged into one badge:
 *   A Employee active · B Login enabled · C Password configured.
 * Each row says its state (icon + words), who can act, the next action, and has at most one button.
 */
export function AccessStatesPanel({
  states, access, accessProblem, onRetryAccess, detailRow, activation, canIssue, canReset, waitingReason, issuing, detail, workEmailNote,
  employeeName, onIssue, onAddWorkEmail,
}: {
  states: ThreeStates;
  /** Null while the sign-in status loads or could not be loaded (see accessProblem). */
  access: EmployeeAccessDto | null;
  accessProblem: 'loading' | 'failed' | null;
  onRetryAccess(): void;
  /** Which row carries the detail line (stopped / blocked reasons sit with Login, code dates with Password). */
  detailRow: 'login' | 'password';
  activation: ActivationControl;
  canIssue: boolean;
  canReset: boolean;
  /** A sentence (already translated) that replaces the B / C button, e.g. "You can't give access to yourself." */
  waitingReason: string | null;
  issuing: boolean;
  /** One line of detail for the password row (last sign-in, code expiry). */
  detail: string;
  workEmailNote?: ReactNode;
  employeeName: string;
  onIssue(delivery?: 'email' | 'print'): void;
  onAddWorkEmail(): void;
}) {
  const { t } = useLocale();
  const titleId = useId();
  const [confirming, setConfirming] = useState<null | 'new_code' | 'reset'>(null);
  const emailing = !!access?.emailDelivery;
  const canGive = !!access && canIssue && access.canIssue && !waitingReason;
  const employeeChip = EMPLOYEE_CHIP[states.employee];
  const loginChip = states.login ? LOGIN_CHIP[states.login] : null;
  const passwordChip = states.password ? PASSWORD_CHIP[states.password] : null;

  // A: who decides activation is employees.approve; the button waits for the server's own blockers.
  const activateNext = states.employee === 'active' ? 'Nothing to do.'
    : ['suspended', 'offboarded', 'ended', 'inactive'].includes(states.employee) ? 'Change the employment status from the employee record.'
    : !activation.allowed ? 'Ask an HR approver to activate.'
      : activation.loading ? 'Loading readiness…'
        : activation.unavailable ? 'Readiness could not be checked.'
        : activation.blocked ? (states.employee === 'awaiting_pay_approval' ? 'Wait for the pay package to be approved.' : 'Clear the activation blockers first.')
          : 'Activate the employee.';

  const loginNext = !access || states.needsWorkEmail ? null
    : states.login === 'login_enabled' ? 'Nothing to do.'
      : states.login === 'access_stopped' ? 'A system admin must restore access.'
        : access?.state === 'blocked' ? 'This needs a system admin first.'
        : states.newCodeLive ? 'The login switches on when the employee uses the code.'
        : canIssue ? 'Give access so the employee can sign in.' : 'Ask HR to give access.';
  const passwordNext = !access || states.needsWorkEmail ? null
    : states.password === 'password_set' && states.newCodeLive ? 'A new sign-in code is waiting. The current password still works until it is used.'
    : states.password === 'password_set' ? (canReset ? 'Reset sign-in only if the employee is locked out.' : 'Ask an HR manager to reset sign-in.')
      : states.password === 'code_given' ? 'The employee uses the code to set a password.'
        : states.password === 'unknown' ? 'Restore access first.'
          : 'The employee sets a password with a sign-in code.';

  const doIssue = (delivery?: 'email' | 'print') => { setConfirming(null); onIssue(delivery); };

  const loginButton = !!access && !states.needsWorkEmail && access.state === 'not_started' && canGive
    ? <RowButton primary disabled={issuing} onClick={() => doIssue()}>{issuing ? t('Preparing the slips…') : emailing ? t('Email sign-in code') : t('Give access')}</RowButton>
    : null;
  const passwordButton = states.needsWorkEmail || !canGive || confirming || states.login === 'access_stopped' ? null
    : states.password === 'code_given' || (states.password === 'password_set' && states.newCodeLive) ? <RowButton disabled={issuing} onClick={() => setConfirming('new_code')}>{t('Give new code')}</RowButton>
      : states.password === 'password_set' && canReset ? <RowButton disabled={issuing} onClick={() => setConfirming('reset')}>{t('Reset sign-in')}</RowButton>
        : null;

  return (
    <div role="group" aria-labelledby={titleId} data-testid="access-states">
      <p id={titleId} className="sr-only">{t('Employment and sign-in status')}</p>
      <dl className="divide-y divide-slate-100 dark:divide-white/10">
        <Row label={t('Employee active')} testId="state-employee" chip={<StatusChip label={t(employeeChip.label)} tone={employeeChip.tone} icon={employeeChip.icon} />}
          who={t('An HR approver with employee approval rights')} next={t(activateNext)}
          button={states.employee !== 'active' && activation.allowed
            ? <RowButton primary disabled={activation.busy || activation.unavailable || activation.blocked} onClick={activation.onActivate}>{activation.busy ? t('Activating…') : t('Activate')}</RowButton>
            : null} />
        <Row label={t('Login enabled')} testId="state-login" dim={states.needsWorkEmail || !access}
          chip={loginChip ? <StatusChip label={t(loginChip.label)} tone={loginChip.tone} icon={loginChip.icon} /> : null}
          who={t('HR with sign-in access rights')}
          next={accessProblem === 'loading' ? t('Checking self-service…') : accessProblem === 'failed' ? t('Self-service could not be checked.') : states.needsWorkEmail ? t('Needs work email first') : loginNext ? t(loginNext) : ''}
          detail={detailRow === 'login' ? detail : undefined}
          note={waitingReason && states.login === 'no_login' && !states.needsWorkEmail ? waitingReason : undefined}
          button={accessProblem === 'failed' ? <RowButton onClick={onRetryAccess}>{t('Try again')}</RowButton> : states.needsWorkEmail && canIssue ? <RowButton primary onClick={onAddWorkEmail}>{t('Add work email')}</RowButton> : loginButton} />
        <Row label={t('Password configured')} testId="state-password" dim={states.needsWorkEmail || !access}
          chip={passwordChip ? <StatusChip label={t(passwordChip.label)} tone={passwordChip.tone} icon={passwordChip.icon} /> : null}
          who={t(states.password === 'password_set' ? 'HR with sign-in reset rights' : 'HR with sign-in access rights')}
          next={!access ? '' : states.needsWorkEmail ? t('Needs work email first') : passwordNext ? t(passwordNext) : ''}
          detail={detailRow === 'password' ? detail : undefined}
          note={waitingReason && states.password !== 'not_set_up' && !states.needsWorkEmail ? waitingReason : undefined}
          button={passwordButton}>
          {confirming && (
            <div className="mt-2.5 rounded-lg border border-amber-200 bg-amber-50 p-2.5 dark:border-amber-500/30 dark:bg-amber-500/10" role="alertdialog" aria-label={confirming === 'reset' ? t('Reset sign-in') : t('Give new code')}>
              <p className="text-xs text-amber-900 dark:text-amber-200">{confirming === 'reset'
                ? t("Reset {name}'s sign-in? Their current password keeps working until they use the new code. Then print a new slip for them.", { name: employeeName })
                : t('The old code will stop working. Continue?')}</p>
              <div className="mt-2 flex justify-end gap-2">
                <button type="button" onClick={() => setConfirming(null)} className="btn-secondary min-h-11 px-3 text-xs md:min-h-8">{t('Cancel')}</button>
                <button type="button" disabled={issuing} onClick={() => doIssue(confirming === 'reset' ? (emailing ? 'print' : undefined) : (emailing ? 'email' : undefined))} className="btn-primary min-h-11 px-3 text-xs disabled:opacity-60 md:min-h-8">{confirming === 'reset' ? t('Reset and print') : t('Give new code')}</button>
              </div>
            </div>
          )}
        </Row>
      </dl>
      <p className="mt-3 text-xs text-slate-600 dark:text-slate-300">{t('Signing in does not activate employment.')}</p>
      {workEmailNote}
    </div>
  );
}

function RowButton({ children, onClick, disabled, primary }: { children: ReactNode; onClick(): void; disabled?: boolean; primary?: boolean }) {
  return (
    <button type="button" onClick={onClick} disabled={disabled} className={`${primary ? 'btn-primary' : 'btn-secondary'} mt-2 min-h-11 w-full justify-center px-3 text-sm disabled:opacity-60 sm:w-auto md:min-h-9`}>
      {children}
    </button>
  );
}

function Row({ label, chip, who, next, detail, note, button, dim, testId, children }: {
  label: string; chip: ReactNode; who: string; next: string; detail?: string; note?: string; button: ReactNode; dim?: boolean; testId: string; children?: ReactNode;
}) {
  const { t } = useLocale();
  return (
    <div className="py-3 first:pt-0 last:pb-0" data-testid={testId}>
      <dt className="text-sm font-bold text-slate-900 dark:text-white">{label}</dt>
      <dd className="mt-1 space-y-1">
        {chip}
        <p className="text-xs text-slate-600 dark:text-slate-300"><span className="font-semibold">{t('Who can act')}:</span> {who}</p>
        {next && <p className={`text-xs ${dim ? 'italic text-slate-500 dark:text-slate-400' : 'text-slate-600 dark:text-slate-300'}`} data-testid={`${testId}-next`}><span className="font-semibold not-italic">{t('Next action')}:</span> {next}</p>}
        {detail && <p className="text-xs text-slate-600 dark:text-slate-300" data-testid="access-detail">{detail}</p>}
        {note && <p className="rounded-md bg-slate-50 px-2 py-1.5 text-xs font-medium text-slate-700 dark:bg-white/[0.05] dark:text-slate-200" data-testid="access-reason">{note}</p>}
        {button}
        {children}
      </dd>
    </div>
  );
}
