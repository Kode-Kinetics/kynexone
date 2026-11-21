'use client';

import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { useCallback, useState } from 'react';
import { CheckCircle2, Mail, Printer, ShieldCheck } from 'lucide-react';
import { employeesApi, type EmployeeDetail, type EmployeeReadiness } from '../../api/employees';
import { employeeAccessApi } from '../../api/employeeAccess';
import { useAuth } from '../../contexts/AuthContext';
import { useLocale } from '../../contexts/LocaleContext';
import { employeeWorkspaceHref } from '../../lib/peopleRoutes';
import { EmployeeCompletionHandoff } from '../../components/EmployeeCompletionHandoff';
import { EmployeeAccessCard } from '../../components/employeeAccess/EmployeeAccessCard';
import { useWelcomeCodes } from '../../components/employeeAccess/useWelcomeCodes';
import { Modal } from '../../components/Modal';
import { RecordSection } from '../../components/employee/RecordSection';
import { StatusChip } from '../../components/StatusChip';
import { ActivationBlockers } from '../../components/employeeAccess/ActivationBlockers';
import { describeApiError } from '../../lib/apiError';
import { activationFormError, todayCalendarDay } from '../../lib/employeeActivation';
import { activationRejectionMessage, employeeActivationFailure, settledServiceRefusal } from '../../lib/employeeActivationFailure';

export function EmployeeAccessOnboardingSection({ employee, readiness, returnTo, onEmployeeChanged, onActivated, onRetryReadiness, readinessLoading = false }: { employee: EmployeeDetail; readiness: EmployeeReadiness | null; returnTo: string; onEmployeeChanged?(): void; /** Re-read the record and its readiness in place after a successful Activate (resolves when both are back). */ onActivated?(): Promise<void>; onRetryReadiness?(): void; readinessLoading?: boolean }) {
  const router = useRouter();
  const { hasPermission, user } = useAuth();
  const { t, locale } = useLocale();
  const canWrite = hasPermission('employees.write');
  const canIssue = hasPermission('employees.access.issue');
  const canReset = hasPermission('employees.access.reset');
  const canApprove = hasPermission('employees.approve');
  const activationBlockers = readiness?.blocking.length ?? 0;
  const recommendations = readiness?.recommended.length ?? 0;
  const [accessRefresh, setAccessRefresh] = useState(0);
  const [activating, setActivating] = useState(false);
  const [activationError, setActivationError] = useState('');
  const [confirming, setConfirming] = useState(false);
  const [form, setForm] = useState({ effectiveDate: '', reason: '' });
  const [activated, setActivated] = useState<{ emailDelivery: boolean } | null>(null);
  const welcome = useWelcomeCodes(useCallback(() => {
    setAccessRefresh((current) => current + 1);
    onEmployeeChanged?.();
  }, [onEmployeeChanged]));
  const editHref = employeeWorkspaceHref(employee.id, { edit: true, section: 'access-onboarding', returnTo });
  const openActivation = () => {
    setActivationError('');
    setForm({ effectiveDate: todayCalendarDay(), reason: t('Activated from the employee workspace') });
    setConfirming(true);
  };
  const closeConfirm = () => {
    setConfirming(false);
  };
  const activate = async () => {
    const invalid = activationFormError(form);
    if (invalid) { setActivationError(t(invalid)); return; }
    setActivating(true);
    setActivationError('');
    try {
      await employeesApi.activate(employee.id, { effectiveDate: form.effectiveDate, reason: form.reason.trim() });
      setConfirming(false);
      // The server now says Active: read the record again before anything else, so the page behind the dialog never shows Draft and
      // no second Activate is offered. `activating` stays on (the row's button stays disabled) until the fresh record is back.
      await onActivated?.();
      const access = canIssue ? await employeeAccessApi.get(employee.id).catch(() => null) : null;
      setActivated({ emailDelivery: !!access?.emailDelivery });
      setAccessRefresh((current) => current + 1);
    } catch (e) {
      // The server says why (a readiness blocker, a permission, a concurrent change): show it in the dialog, don't swallow it.
      // A refusal that names the blockers is shown as those blockers; anything else as the server's own sentence.
      // A leaver whose final settlement is approved or paid comes back through Rehire, which starts the new service period.
      const blockers = (e as { response?: { data?: { blocking?: unknown[] } } } | null)?.response?.data?.blocking;
      setActivationError(settledServiceRefusal(e, locale) ?? (Array.isArray(blockers) && blockers.length
        ? employeeActivationFailure(e, t)
        : describeApiError(e, t) || activationRejectionMessage(e, t)));
      // The server is the authority: read readiness and access again (readiness only: a whole-record reload would unmount
      // this section and take the dialog, and its message, away), and let the button be pressed again.
      setAccessRefresh((current) => current + 1);
      onRetryReadiness?.();
    } finally {
      setActivating(false);
    }
  };

  return <div className="space-y-4">
    <RecordSection
      title={t('Access & Onboarding')}
      description={t('Work email, sign-in access, employee completion, readiness, and activation.')}
      actions={canWrite && <Link href={editHref} className="btn-secondary">{t('Edit onboarding')}</Link>}
    >
      <div className="space-y-3">
        <div className="flex flex-wrap items-center gap-x-3 gap-y-1 rounded-lg border border-slate-200 px-3 py-2 dark:border-white/10" data-testid="work-email-row">
          <Mail className="h-4 w-4 shrink-0 text-blue-600" aria-hidden="true" />
          <span className="text-xs text-slate-500 dark:text-slate-400">{t('Work email')}</span>
          <span className="min-w-0 flex-1 break-all text-sm font-medium text-slate-900 dark:text-white" dir="auto">{employee.workEmail || t('No work email has been recorded.')}</span>
          <StatusChip label={employee.workEmail ? t('Email on file') : t('Email missing')} tone={employee.workEmail ? 'emerald' : 'amber'} />
        </div>
        <EmployeeAccessCard
          employeeId={employee.id}
          employeeName={employee.fullName}
          employeeStatus={employee.status}
          workEmail={employee.workEmail}
          readiness={readiness}
          readinessLoading={readinessLoading}
          canApprove={canApprove}
          activating={activating}
          onActivate={openActivation}
          refreshKey={accessRefresh}
          canIssue={canIssue}
          canReset={canReset}
          isSelf={user?.employeeId != null && user.employeeId === employee.id}
          issuing={welcome.busy}
          onIssue={welcome.issue}
          onAddWorkEmail={() => router.push(editHref)}
        />
      </div>
    </RecordSection>
    {canWrite && <EmployeeCompletionHandoff employeeId={employee.id} employeeStatus={employee.status} onChanged={onEmployeeChanged} />}
    <section className="surface p-4" aria-labelledby="onboarding-readiness-title">
      <div className="flex items-center gap-2"><ShieldCheck className="h-4 w-4 text-emerald-600" aria-hidden="true" /><h2 id="onboarding-readiness-title" className="text-base font-semibold">{t('Onboarding readiness')}</h2></div>
      <p className="mt-2 text-sm text-slate-500">{readiness ? t('{done} of {total} readiness checks are complete.', { done: readiness.progress.present, total: readiness.progress.requiredTotal }) : readinessLoading ? t('Loading readiness…') : t('Readiness is currently unavailable.')}</p>
      {readiness && <p className="mt-1 text-sm font-semibold text-slate-700 dark:text-slate-200">{activationBlockers > 0
        ? (activationBlockers === 1 ? t('1 activation blocker remains.') : t('{count} activation blockers remain.', { count: activationBlockers }))
        : recommendations > 0
          ? (recommendations === 1 ? t('No activation blockers. 1 recommendation remains.') : t('No activation blockers. {count} recommendations remain.', { count: recommendations }))
          : t('No activation blockers remain.')}</p>}
      <div className="mt-3">
        <ActivationBlockers
          blockers={readiness ? readiness.blocking : null}
          employeeId={employee.id}
          companyId={employee.companyId}
          loading={readinessLoading && !readiness}
          onRetry={onRetryReadiness}
        />
      </div>
    </section>
    {welcome.view}
    <Modal
      isOpen={confirming}
      title={t('Activate {name}?', { name: employee.fullName })}
      size="md"
      closeLabel={t('Close')}
      closeDisabled={activating}
      onClose={() => { if (!activating) closeConfirm(); }}
      footer={<>
        <button type="button" onClick={closeConfirm} disabled={activating} className="btn-secondary">{t('Cancel')}</button>
        <button type="button" onClick={() => void activate()} disabled={activating} className="btn-primary">{activating ? t('Activating…') : t('Activate')}</button>
      </>}
    >
      <div className="space-y-4">
        <p className="text-sm text-slate-600 dark:text-slate-300">{t('The employee becomes active on the date below, joins headcount and can be paid. Use an earlier date for someone who has already started.')}</p>
        <div>
          <label htmlFor="activate-date" className="mb-1 block text-sm font-medium text-slate-700 dark:text-slate-300">{t('Effective date')}</label>
          <input id="activate-date" type="date" className="input w-full" value={form.effectiveDate} onChange={(e) => setForm((f) => ({ ...f, effectiveDate: e.target.value }))} disabled={activating} />
        </div>
        <div>
          <label htmlFor="activate-reason" className="mb-1 block text-sm font-medium text-slate-700 dark:text-slate-300">{t('Reason')}</label>
          <textarea id="activate-reason" className="input w-full resize-none" rows={2} value={form.reason} onChange={(e) => setForm((f) => ({ ...f, reason: e.target.value }))} disabled={activating} />
        </div>
        {activationError && <p role="alert" data-testid="activation-error" className="rounded-lg bg-rose-50 px-3 py-2 text-sm text-rose-700 dark:bg-rose-500/10 dark:text-rose-300">{activationError}</p>}
      </div>
    </Modal>
    <Modal
      isOpen={activated !== null}
      title={t('Self-service')}
      size="md"
      onClose={() => { setActivated(null); onEmployeeChanged?.(); }}
      footer={activated && <>
        <button type="button" onClick={() => { setActivated(null); onEmployeeChanged?.(); }} className="btn-secondary">{t('Later')}</button>
        <button type="button" onClick={() => { const emailDelivery = activated.emailDelivery; setActivated(null); void welcome.issue([employee.id], { names: { [employee.id]: employee.fullName }, delivery: emailDelivery ? 'print' : undefined, companyEmails: emailDelivery }); }} className="btn-primary"><Printer className="h-4 w-4" aria-hidden="true" />{t('Print sign-in slip')}</button>
      </>}
    >
      <p className="flex items-center gap-2 py-2 text-base font-semibold text-slate-900 dark:text-white" data-testid="employee-activated"><CheckCircle2 className="h-5 w-5 shrink-0 text-emerald-600" aria-hidden="true" />{t('{name} is now active.', { name: employee.fullName })}</p>
    </Modal>
  </div>;
}
