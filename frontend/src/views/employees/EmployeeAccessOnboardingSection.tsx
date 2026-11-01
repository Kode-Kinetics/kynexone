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
import { ActivationBlockers } from '../../components/employeeAccess/ActivationBlockers';
import { activationRejectionMessage } from '../../lib/employeeActivationFailure';

export function EmployeeAccessOnboardingSection({ employee, readiness, returnTo, onEmployeeChanged, onRetryReadiness, readinessLoading = false }: { employee: EmployeeDetail; readiness: EmployeeReadiness | null; returnTo: string; onEmployeeChanged?(): void; onRetryReadiness?(): void; readinessLoading?: boolean }) {
  const router = useRouter();
  const { hasPermission, user } = useAuth();
  const { t } = useLocale();
  const canWrite = hasPermission('employees.write');
  const canIssue = hasPermission('employees.access.issue');
  const canReset = hasPermission('employees.access.reset');
  const canApprove = hasPermission('employees.approve');
  const activationBlockers = readiness?.blocking.length ?? 0;
  const recommendations = readiness?.recommended.length ?? 0;
  const [accessRefresh, setAccessRefresh] = useState(0);
  const [activating, setActivating] = useState(false);
  const [activationError, setActivationError] = useState('');
  const [activated, setActivated] = useState<{ emailDelivery: boolean } | null>(null);
  const welcome = useWelcomeCodes(useCallback(() => {
    setAccessRefresh((current) => current + 1);
    onEmployeeChanged?.();
  }, [onEmployeeChanged]));
  const editHref = employeeWorkspaceHref(employee.id, { edit: true, section: 'access-onboarding', returnTo });
  const activate = async () => {
    setActivating(true);
    setActivationError('');
    try {
      await employeesApi.activate(employee.id, {
        effectiveDate: new Date().toISOString().slice(0, 10),
        reason: 'Activated from employee workspace',
      });
      const access = canIssue ? await employeeAccessApi.get(employee.id).catch(() => null) : null;
      setActivated({ emailDelivery: !!access?.emailDelivery });
      setAccessRefresh((current) => current + 1);
    } catch (error) {
      setActivationError(activationRejectionMessage(error, t));
      // The server is the authority: read readiness and access again, and let the button be pressed again.
      // Readiness only: a whole-record reload would unmount this section and lose the message.
      setAccessRefresh((current) => current + 1);
      onRetryReadiness?.();
    } finally {
      setActivating(false);
    }
  };

  return <div className="space-y-5">
    <section className="surface overflow-hidden">
      <header className="flex flex-wrap items-start justify-between gap-3 border-b border-slate-200 p-5 dark:border-white/10">
        <div><h2 className="text-2xl font-extrabold text-slate-950 dark:text-white">{t('Access & Onboarding')}</h2><p className="mt-1 text-sm text-slate-500">{t('Work email, sign-in access, employee completion, readiness, and activation.')}</p></div>
        {canWrite && <Link href={editHref} className="btn-secondary">{t('Edit onboarding')}</Link>}
      </header>
      <div className="grid gap-4 p-5 md:grid-cols-2">
        <div className="rounded-xl border border-slate-200 p-5 dark:border-white/10"><Mail className="h-5 w-5 text-blue-600" /><h3 className="mt-3 font-extrabold">{t('Work email')}</h3><p className="mt-1 break-words text-sm text-slate-500" dir="auto">{employee.workEmail || t('No work email has been recorded.')}</p></div>
        <EmployeeAccessCard
          employeeId={employee.id}
          employeeName={employee.fullName}
          employeeStatus={employee.status}
          workEmail={employee.workEmail}
          readiness={readiness}
          readinessLoading={readinessLoading}
          canApprove={canApprove}
          activating={activating}
          onActivate={() => void activate()}
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
    <section className="surface p-5" aria-labelledby="onboarding-readiness-title">
      <div className="flex items-center gap-3"><ShieldCheck className="h-5 w-5 text-emerald-600" aria-hidden="true" /><h2 id="onboarding-readiness-title" className="text-lg font-extrabold">{t('Onboarding readiness')}</h2></div>
      <p className="mt-3 text-sm text-slate-500">{readiness ? t('{done} of {total} readiness checks are complete.', { done: readiness.progress.present, total: readiness.progress.requiredTotal }) : readinessLoading ? t('Loading readiness…') : t('Readiness is currently unavailable.')}</p>
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
          error={activationError}
        />
      </div>
    </section>
    {welcome.view}
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
      <p className="flex items-center gap-2 py-2 text-base font-bold text-slate-900 dark:text-white" data-testid="employee-activated"><CheckCircle2 className="h-5 w-5 shrink-0 text-emerald-600" aria-hidden="true" />{t('{name} is now active.', { name: employee.fullName })}</p>
    </Modal>
  </div>;
}
