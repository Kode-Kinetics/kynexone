'use client';

import { useEffect, useId, useRef, useState } from 'react';
import { Modal } from '../Modal';
import { EmployeeSearchSelect, type EmployeeSelection } from '../EmployeeSearchSelect';
import { usersApi } from '../../api/identity';
import { employeesApi, type EmployeeListItem } from '../../api/employees';
import type { EmployeeLoginInvitation, EmployeeLoginLinkResult, EmployeeLoginStatus, UserListItem } from '../../api/identity';
import { localizedRefusal } from '../../lib/accessCeiling';
import { useLocale } from '../../contexts/LocaleContext';

/**
 * One dialog for both ways an employee reaches Self-Service:
 *   - from a login's row ("Link to employee record"): link that existing login to the employee whose work email it carries;
 *   - from the toolbar ("Invite employee"): an employee with no login gets a self-service invitation.
 * The server decides the next step (GET /api/access/employee-logins/{id} → nextAction); this only explains it and
 * offers the one action that matches. Self-Service reads the employee only from the login's link, never by email.
 */
interface Props {
  /** The login the dialog was opened from, or null when opened from "Invite employee". */
  user: UserListItem | null;
  onClose: () => void;
  /** Something changed (a link or an invitation); the caller reloads its list. */
  onChanged: () => void;
}

type Outcome =
  | { kind: 'linked'; employeeName: string; linked: EmployeeLoginLinkResult }
  | { kind: 'invited'; invitation: EmployeeLoginInvitation };

/** The server's refusal codes this dialog words itself, so the reader gets their own language. */
const LOGIN_OTHER_COMPANY = 'login_other_company';
const LOGIN_POINTER_CONFLICT = 'login_pointer_conflict';
const LOGIN_NEEDS_GROUP_ADMIN = 'login_needs_group_admin';
const LOGIN_NOT_MANAGEABLE = 'login_not_manageable';
const LOGIN_CREDENTIAL_HANDLED_BY_CALLER = 'login_credential_handled_by_caller';
const WORK_EMAIL_CHANGED_BY_PARTY = 'work_email_changed_by_party';
/** The lifecycle states that can hold a login (AuthCurrentEligibility). */
const LINKABLE_STATUSES = ['Active', 'Invited'] as const;

const btnPrimary = 'rounded-lg bg-violet-600 px-4 py-2 text-sm font-medium text-white hover:bg-violet-700 disabled:opacity-60';
const btnSecondary = 'rounded-lg border border-slate-200 px-3 py-2 text-sm hover:bg-slate-50 dark:border-slate-700 dark:hover:bg-slate-700';

export function LinkEmployeeLoginDialog({ user, onClose, onChanged }: Props) {
  const { t, locale } = useLocale();
  const reasonId = useId();
  const [employee, setEmployee] = useState<EmployeeSelection | null>(null);
  const [status, setStatus] = useState<EmployeeLoginStatus | null>(null);
  const [loadingStatus, setLoadingStatus] = useState(false);
  const [statusError, setStatusError] = useState('');
  const [reason, setReason] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState('');
  const [outcome, setOutcome] = useState<Outcome | null>(null);
  const [copied, setCopied] = useState(false);

  // Opened from a login: look for its employee record so the admin does not have to. The employee search matches
  // work email as a SUBSTRING (ann@ also finds joann@) and does not return the email, so a hit is only a candidate:
  // the status endpoint gives each candidate's work email, and only an exact match (case-insensitive) is picked
  // automatically. Other candidates are offered to choose from. No candidate by email → offer employees named like
  // the login, since the usual cause is a work email that was never filled in.
  const [suggestions, setSuggestions] = useState<{ by: 'email' | 'name'; items: EmployeeListItem[] } | null>(null);
  const [lookingUp, setLookingUp] = useState(Boolean(user));
  const [autoPicked, setAutoPicked] = useState(false);
  // Set by any choice the admin makes; a lookup still in flight must never overwrite it.
  const choseByHand = useRef(false);
  const userEmail = user?.email ?? '';
  const userName = user?.fullName ?? '';
  useEffect(() => {
    if (!userEmail) return;
    let current = true;
    const live = () => current && !choseByHand.current;
    const wanted = userEmail.trim().toLowerCase();
    (async () => {
      try {
        const byEmail = await employeesApi.list({ search: userEmail, pageSize: 5, status: LINKABLE_STATUSES });
        if (!live()) return;
        if (byEmail.items.length > 0) {
          const checked = await Promise.all(byEmail.items.map((e) =>
            usersApi.employeeLoginStatus(e.id).then((st) => ({ e, exact: st.workEmail.trim().toLowerCase() === wanted }), () => ({ e, exact: false }))));
          if (!live()) return;
          const exact = checked.filter((c) => c.exact);
          if (exact.length === 1) {
            const e = exact[0].e;
            setEmployee({ intId: e.id, publicId: e.publicId, fullName: e.fullName, employeeCode: e.employeeCode, department: e.department ?? '' });
            setAutoPicked(true);
            return;
          }
          setSuggestions({ by: 'email', items: byEmail.items });
          return;
        }
        const nameTerm = userName.trim();
        if (!nameTerm) return;
        const byName = await employeesApi.list({ search: nameTerm, pageSize: 5, status: LINKABLE_STATUSES });
        if (live() && byName.items.length > 0) setSuggestions({ by: 'name', items: byName.items });
      } catch {
        // A failed lookup only means no suggestion; the search box still works.
      } finally {
        if (current) setLookingUp(false);
      }
    })();
    return () => { current = false; };
    // Keyed on the login's email and name, not the object: a parent re-render must not redo the lookup.
  }, [userEmail, userName]);

  const choose = (selection: EmployeeSelection | null) => {
    choseByHand.current = true;
    setLookingUp(false);
    setEmployee(selection);
    setAutoPicked(false);
  };
  const pick = (e: EmployeeListItem) =>
    choose({ intId: e.id, publicId: e.publicId, fullName: e.fullName, employeeCode: e.employeeCode, department: e.department ?? '' });

  // A new employee starts a new question: forget the previous answer, error and outcome.
  useEffect(() => {
    setStatus(null); setStatusError(''); setError(''); setOutcome(null); setCopied(false);
    if (!employee) return;
    let current = true;
    setLoadingStatus(true);
    usersApi.employeeLoginStatus(employee.intId)
      .then((s) => { if (current) setStatus(s); })
      .catch((e: unknown) => { if (current) setStatusError(localizedRefusal(e, locale) ?? t('The employee record could not be checked. Try again.')); })
      .finally(() => { if (current) setLoadingStatus(false); });
    return () => { current = false; };
  }, [employee, locale, t]);

  const name = status?.employeeName ?? employee?.fullName ?? '';

  /** A coded refusal in the reader's language, or null for a code this dialog does not word itself. */
  const codedRefusal = (code: unknown, subject: unknown): string | null => {
    const named = typeof subject === 'string' && subject ? subject : null;
    if (code === LOGIN_OTHER_COMPANY && named)
      return t('This login works in a different company. Give it access to {company} first, or link it from that company.', { company: named });
    if (code === LOGIN_POINTER_CONFLICT)
      return named
        ? t("This login is still recorded on {employee}'s employee record. Contact support to resolve it.", { employee: named })
        : t('This login is still recorded on another employee record. Contact support to resolve it.');
    if (code === LOGIN_NEEDS_GROUP_ADMIN) return t('Only a group-level administrator can link a login that has no company access yet.');
    if (code === LOGIN_NOT_MANAGEABLE) return t('A login already uses this work email, but it is outside your access. An administrator who manages it must link it.');
    if (code === LOGIN_CREDENTIAL_HANDLED_BY_CALLER) return t("You have handled this login's credentials (you created it, set its password, or were shown a reset or invitation link for it), so you cannot link it to an employee record. Another administrator must link it.");
    if (code === WORK_EMAIL_CHANGED_BY_PARTY) return t('The work email on this employee record was last changed by you or by this login, so the link needs a different administrator.');
    return null;
  };

  /** A refusal from a write: the coded ones in the reader's language, the rest as the server said them. */
  const writeError = (e: unknown, fallback: string) => {
    const data = (e as { response?: { data?: { code?: unknown; subject?: unknown } } } | null)?.response?.data;
    return codedRefusal(data?.code, data?.subject) ?? localizedRefusal(e, locale) ?? fallback;
  };

  const link = async (userId: string) => {
    if (!status) return;
    if (!reason.trim()) { setError(t('Give a reason. It is kept in the audit trail.')); return; }
    setSubmitting(true); setError('');
    try {
      const linked = await usersApi.linkExistingLogin({ employeeId: status.employeeId, userId, reason: reason.trim() });
      setOutcome({ kind: 'linked', employeeName: status.employeeName, linked });
      onChanged();
    } catch (e: unknown) {
      setError(writeError(e, t('The login could not be linked.')));
    }
    setSubmitting(false);
  };

  const invite = async () => {
    if (!status) return;
    setSubmitting(true); setError('');
    try {
      const invitation = await usersApi.inviteEmployee({ employeeId: status.employeeId, accessMode: 'ESSOnly' });
      setOutcome({ kind: 'invited', invitation });
      onChanged();
    } catch (e: unknown) {
      setError(localizedRefusal(e, locale) ?? t('The invitation could not be sent.'));
    }
    setSubmitting(false);
  };

  // What to say, and the one action that matches the server's next step.
  let explanation: string | null = null;
  let resetNotice: string | null = null;
  let action: { label: string; run: () => void; needsReason: boolean } | null = null;
  if (status && !outcome) {
    const pendingInvitation = status.linkedLogin && !status.linkedLogin.isActive
      && ['Invited', 'PendingPasswordSetup'].includes(status.linkedLogin.status);
    switch (status.nextAction) {
      case 'linked':
        if (pendingInvitation) {
          explanation = t('{name} has been invited but has not set a password yet. You can send the invitation again.', { name });
          action = { label: t('Send self-service invitation'), run: invite, needsReason: false };
        } else if (user && status.linkedLogin?.userId === user.id) {
          explanation = t('This login is already linked to {name}. Nothing to do.', { name });
        } else {
          explanation = t('{name} is already linked to the login {email}.', { name, email: status.linkedLogin?.email ?? '' });
        }
        break;
      case 'link_existing':
        if (user && status.matchingLogin && status.matchingLogin.userId !== user.id) {
          explanation = t('{name}\'s work email belongs to a different login ({email}). Link that login instead, or correct the work email on the employee record.',
            { name, email: status.matchingLogin.email });
        } else if (status.matchingLogin) {
          explanation = t('The login {email} uses {name}\'s work email. Linking it lets {name} use Self-Service with the access the login already has.',
            { name, email: status.matchingLogin.email });
          const target = status.matchingLogin.userId;
          action = { label: t('Link this login'), run: () => { void link(target); }, needsReason: true };
          if (status.willResetCredential)
            resetNotice = t("Linking will reset this login's password. {name} will set a new one from an invitation.", { name });
        }
        break;
      case 'invite':
        explanation = user
          ? t('This login\'s email ({email}) does not match {name}\'s work email ({workEmail}). Correct the work email on the employee record so they match, or send {name} a self-service invitation instead.',
            { name, email: user.email, workEmail: status.workEmail })
          : t('{name} has no login yet. An invitation to {workEmail} lets them set a password and use Self-Service.',
            { name, workEmail: status.workEmail });
        action = { label: t('Send self-service invitation'), run: () => { void invite(); }, needsReason: false };
        break;
      case 'needs_work_email':
      case 'blocked':
        explanation = codedRefusal(status.reasonCode, status.reasonSubject)
          ?? status.reason ?? t('This employee cannot be linked right now.');
        break;
    }
  }

  const footer = (
    <div className="flex justify-end gap-2">
      <button type="button" onClick={onClose} className={btnSecondary}>{outcome ? t('Close') : t('Cancel')}</button>
      {action && (
        <button type="button" onClick={action.run} disabled={submitting || (action.needsReason && !reason.trim())} className={btnPrimary}>
          {submitting ? t('Working…') : action.label}
        </button>
      )}
    </div>
  );

  return (
    <Modal isOpen size="lg" title={user ? t('Link to employee record') : t('Invite employee')} onClose={onClose} footer={footer}>
      <div className="space-y-4">
        {user && (
          <p className="text-sm text-slate-600 dark:text-slate-400">
            {t('Login: {email}', { email: user.email })}
          </p>
        )}
        <div>
          <p className="mb-1 text-xs font-medium text-slate-600 dark:text-slate-400">{t('Employee')}</p>
          <EmployeeSearchSelect
            value={employee}
            onChange={choose}
            placeholder={t('Search employees by name, code or work email')}
            statuses={LINKABLE_STATUSES}
            inlineResults
            autoFocus={!user}
          />
          {user && lookingUp && !employee && (
            <p className="mt-2 text-sm text-slate-500">{t('Looking for the employee record that uses {email}…', { email: user.email })}</p>
          )}
          {autoPicked && employee && (
            <p className="mt-2 text-sm text-emerald-700 dark:text-emerald-400" data-testid="employee-auto-picked">
              {t('Found automatically: {name} has the work email {email}.', { name: employee.fullName, email: user?.email ?? '' })}
            </p>
          )}
          {user && !employee && !lookingUp && (
            <div className="mt-3 space-y-2" data-testid="employee-suggestions">
              <p className="text-sm text-slate-600 dark:text-slate-400">
                {suggestions?.by === 'email'
                  ? t('No employee record has exactly the work email {email}, but these records mention it. Choose the right one below.', { email: user.email })
                  : suggestions?.by === 'name'
                    ? t('No employee record has the work email {email}. The employees below have a similar name. Choose one, then check its work email.', { email: user.email })
                    : t('No employee record has the work email {email}. Search by name above, then set that work email on the record.', { email: user.email })}
              </p>
              {suggestions && (
                <ul className="divide-y divide-slate-100 rounded-lg border border-slate-200 dark:divide-white/[0.06] dark:border-white/[0.1]">
                  {suggestions.items.map((e) => (
                    <li key={e.id}>
                      <button type="button" onClick={() => pick(e)}
                        className="flex w-full items-center justify-between gap-3 px-3 py-2 text-start hover:bg-slate-50 dark:hover:bg-white/[0.05]">
                        <span className="min-w-0">
                          <span className="block truncate text-sm font-medium text-slate-900 dark:text-white">{e.fullName}</span>
                          <span className="block text-xs text-slate-500">{e.employeeCode}{e.department ? ` · ${e.department}` : ''}{e.status !== 'Active' ? ` · ${t(e.status)}` : ''}</span>
                        </span>
                        <span className="shrink-0 text-xs font-medium text-violet-700 dark:text-violet-300">{t('Choose this employee')}</span>
                      </button>
                    </li>
                  ))}
                </ul>
              )}
            </div>
          )}
        </div>

        {loadingStatus && <p className="text-sm text-slate-500">{t('Checking the employee record…')}</p>}
        {statusError && <p role="alert" className="text-sm text-red-600 dark:text-red-400">{statusError}</p>}

        {status && !outcome && (
          <div className="space-y-3 rounded-lg border border-slate-200 p-3 dark:border-slate-700" data-testid="employee-login-status">
            <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-sm">
              <dt className="text-slate-500">{t('Work email')}</dt>
              <dd className="field-ltr break-all text-slate-800 dark:text-slate-200">{status.workEmail || '—'}</dd>
              <dt className="text-slate-500">{t('Linked login')}</dt>
              <dd className="break-all text-slate-800 dark:text-slate-200">{status.linkedLogin?.email ?? t('No linked login')}</dd>
            </dl>
            {explanation && <p className="text-sm text-slate-700 dark:text-slate-300">{explanation}</p>}
            {resetNotice && (
              <p data-testid="link-will-reset-credential" className="rounded-lg bg-amber-50 p-3 text-sm text-amber-800 dark:bg-amber-900/20 dark:text-amber-300">
                {resetNotice}
              </p>
            )}
            {action?.needsReason && (
              <div className="flex flex-col gap-1">
                <label htmlFor={reasonId} className="text-xs font-medium text-slate-600 dark:text-slate-400">{t('Reason (kept in the audit trail)')}</label>
                <input
                  id={reasonId}
                  required
                  value={reason}
                  onChange={(e) => setReason(e.target.value)}
                  placeholder={t('For example: the login was created before the employee record')}
                  maxLength={500}
                  className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-800 shadow-sm focus:border-violet-500 focus:outline-none dark:border-slate-700 dark:bg-slate-800 dark:text-slate-200"
                />
              </div>
            )}
          </div>
        )}

        {error && <p role="alert" className="text-sm text-red-600 dark:text-red-400">{error}</p>}

        {outcome?.kind === 'linked' && !outcome.linked.credentialReset && (
          <div role="status" className="space-y-1 rounded-lg bg-emerald-50 p-3 text-sm text-emerald-800 dark:bg-emerald-900/20 dark:text-emerald-300">
            <p className="font-medium">{t('Linked to {name}.', { name: outcome.employeeName })}</p>
            <p>{t('{name} must sign out and sign in again to see Self-Service.', { name: outcome.employeeName })}</p>
          </div>
        )}

        {outcome?.kind === 'linked' && outcome.linked.credentialReset && (
          <div role="status" className="space-y-3">
            <div className="space-y-1 rounded-lg bg-emerald-50 p-3 text-sm text-emerald-800 dark:bg-emerald-900/20 dark:text-emerald-300">
              <p className="font-medium">{t('Linked. {name} must set a new password from the invitation.', { name: outcome.employeeName })}</p>
              <p>{t("Someone other than {name} had handled this login's password, so the old password no longer works.", { name: outcome.employeeName })}</p>
            </div>
            {outcome.linked.deliveryMessage && (
              <p className={`text-sm ${outcome.linked.emailSent ? 'text-emerald-700 dark:text-emerald-400' : 'text-amber-700 dark:text-amber-400'}`}>
                {outcome.linked.deliveryMessage}
              </p>
            )}
            {outcome.linked.invitationUrl && !outcome.linked.emailSent && (
              <div>
                <p className="mb-1 text-xs font-medium text-slate-600 dark:text-slate-400">
                  {t('Invitation link — copy it now and send it to them yourself')}
                </p>
                <div className="flex gap-2">
                  <input
                    readOnly
                    aria-label={t('Invitation link')}
                    value={outcome.linked.invitationUrl}
                    onFocus={(e) => e.currentTarget.select()}
                    className="field-ltr w-full rounded-lg border border-slate-200 bg-white px-3 py-2 font-mono text-xs text-slate-800 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-200"
                  />
                  <button
                    type="button"
                    onClick={() => {
                      navigator.clipboard?.writeText(outcome.linked.invitationUrl ?? '')
                        .then(() => setCopied(true))
                        .catch(() => setCopied(false));
                    }}
                    className={`shrink-0 ${btnSecondary}`}
                  >
                    {copied ? t('Link copied') : t('Copy link')}
                  </button>
                </div>
              </div>
            )}
          </div>
        )}

        {outcome?.kind === 'invited' && (
          <div role="status" className="space-y-3">
            <p className={`text-sm ${outcome.invitation.emailSent ? 'text-emerald-700 dark:text-emerald-400' : 'text-amber-700 dark:text-amber-400'}`}>
              {outcome.invitation.deliveryMessage}
            </p>
            {outcome.invitation.invitationUrl && !outcome.invitation.emailSent && (
              <div>
                <p className="mb-1 text-xs font-medium text-slate-600 dark:text-slate-400">
                  {t('Invitation link — copy it now and send it to them yourself')}
                </p>
                <div className="flex gap-2">
                  <input
                    readOnly
                    aria-label={t('Invitation link')}
                    value={outcome.invitation.invitationUrl}
                    onFocus={(e) => e.currentTarget.select()}
                    className="field-ltr w-full rounded-lg border border-slate-200 bg-white px-3 py-2 font-mono text-xs text-slate-800 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-200"
                  />
                  <button
                    type="button"
                    onClick={() => {
                      navigator.clipboard?.writeText(outcome.invitation.invitationUrl)
                        .then(() => setCopied(true))
                        .catch(() => setCopied(false));
                    }}
                    className={`shrink-0 ${btnSecondary}`}
                  >
                    {copied ? t('Link copied') : t('Copy link')}
                  </button>
                </div>
              </div>
            )}
          </div>
        )}
      </div>
    </Modal>
  );
}
