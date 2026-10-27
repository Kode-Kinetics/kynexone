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
import { describeApiError } from '../../lib/apiError';
import { activationFormError, todayCalendarDay } from '../../lib/employeeActivation';

export function EmployeeAccessOnboardingSection({ employee, readiness, returnTo, onEmployeeChanged }: { employee: EmployeeDetail; readiness: EmployeeReadiness | null; returnTo: string; onEmployeeChanged?(): void }) {
  const router = useRouter();
  const { hasPermission, user } = useAuth();
  const { t } = useLocale();
  const canWrite = hasPermission('employees.write');
  const canIssue = hasPermission('employees.access.issue');
  const canReset = hasPermission('employees.access.reset');
  const canActivate = hasPermission('employees.approve') && employee.status !== 'Active';
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
  const activate = async () => {
    const invalid = activationFormError(form);
    if (invalid) { setActivationError(t(invalid)); return; }
    setActivating(true);
    setActivationError('');
    try {
      await employeesApi.activate(employee.id, { effectiveDate: form.effectiveDate, reason: form.reason.trim() });
      setConfirming(false);
      const access = canIssue ? await employeeAccessApi.get(employee.id).catch(() => null) : null;
      setActivated({ emailDelivery: !!access?.emailDelivery });
      setAccessRefresh((current) => current + 1);
    } catch (e) {
      // The server says why (a readiness blocker, a permission, a concurrent change): show it, don't swallow it.
      setActivationError(describeApiError(e, t) || t('The employee could not be activated. Review the readiness items and try again.'));
    } finally {
      setActivating(false);
    }
  };

  return <div className="space-y-5">
    <section className="surface overflow-hidden">
      <header className="flex flex-wrap items-start justify-between gap-3 border-b border-slate-200 p-5 dark:border-white/10">
        <div><h2 className="text-2xl font-semibold text-slate-950 dark:text-white">{t('Access & Onboarding')}</h2><p className="mt-1 text-sm text-slate-500">{t('Work email, sign-in access, employee completion, readiness, and activation.')}</p></div>
        {canWrite && <Link href={editHref} className="btn-secondary">{t('Edit onboarding')}</Link>}
      </header>
      <div className="grid gap-4 p-5 md:grid-cols-2">
        <div className="rounded-xl border border-slate-200 p-5 dark:border-white/10"><Mail className="h-5 w-5 text-blue-600" /><h3 className="mt-3 font-semibold">{t('Work email')}</h3><p className="mt-1 text-sm text-slate-500">{employee.workEmail || t('No work email has been recorded.')}</p></div>
        <EmployeeAccessCard
          employeeId={employee.id}
          employeeName={employee.fullName}
          refreshKey={accessRefresh}
          canIssue={canIssue}
          canReset={canReset}
          isSelf={user?.employeeId != null && user.employeeId === employee.id}
          issuing={welcome.busy}
          onIssue={welcome.issue}
          onAddWorkEmail={() => router.push(editHref)}
        />
      </div>
    </section>
    {canWrite && <EmployeeCompletionHandoff employeeId={employee.id} onChanged={onEmployeeChanged} />}
    <section className="surface p-5">
      <div className="flex items-center gap-3"><ShieldCheck className="h-5 w-5 text-emerald-600" /><h2 className="text-lg font-semibold">{t('Onboarding readiness')}</h2></div>
      <p className="mt-3 text-sm text-slate-500">{readiness ? t('{done} of {total} readiness checks are complete.', { done: readiness.progress.present, total: readiness.progress.requiredTotal }) : t('Readiness is currently unavailable.')}</p>
      {readiness && <p className="mt-1 text-sm font-semibold text-slate-700 dark:text-slate-200">{activationBlockers > 0
        ? (activationBlockers === 1 ? t('1 activation blocker remains.') : t('{count} activation blockers remain.', { count: activationBlockers }))
        : recommendations > 0
          ? (recommendations === 1 ? t('No activation blockers. 1 recommendation remains.') : t('No activation blockers. {count} recommendations remain.', { count: recommendations }))
          : t('No activation blockers remain.')}</p>}
            {canActivate && <button type="button" onClick={openActivation} disabled={activating || !readiness || activationBlockers > 0} className="btn-primary mt-4 disabled:opacity-50">{t('Activate employee')}</button>}
    </section>
    {welcome.view}
    <Modal
      isOpen={confirming}
      title={t('Activate {name}?', { name: employee.fullName })}
      size="md"
      closeLabel={t('Close')}
      closeDisabled={activating}
      onClose={() => { if (!activating) setConfirming(false); }}
      footer={<>
        <button type="button" onClick={() => setConfirming(false)} disabled={activating} className="btn-secondary">{t('Cancel')}</button>
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
