'use client';

import Link from 'next/link';
import { useSearchParams } from 'next/navigation';
import { useEmployeeRecord, type EmployeeLoadError } from './useEmployeeRecord';
import { employeeWorkspaceHref, validatePeopleReturnTo, type EmployeeSection } from '../../lib/peopleRoutes';
import { EmployeeWorkspaceShell } from './EmployeeWorkspaceShell';
import { EmployeeEditFlow } from './EmployeeEditFlow';

interface EmployeeWorkspacePageProps {
  employeeIdParam: string;
  mode?: 'view' | 'edit';
}

function isPositiveEmployeeId(value: string): boolean {
  return /^[1-9]\d*$/.test(value) && Number.isSafeInteger(Number(value));
}

const employeeSections = new Set<EmployeeSection>([
  'overview',
  'personal',
  'employment',
  'pay-benefits',
  'compliance-documents',
  'access-onboarding',
  'activity',
]);

function employeeSection(value: string | null): EmployeeSection {
  return value && employeeSections.has(value as EmployeeSection) ? value as EmployeeSection : 'overview';
}

function EmployeeErrorState({ error, returnTo, onRetry }: {
  error: EmployeeLoadError;
  returnTo: string;
  onRetry: () => void;
}) {
  const heading = error.kind === 'not_found'
    ? 'Employee record not found'
    : error.kind === 'forbidden'
      ? 'You do not have access to this employee'
      : error.kind === 'network'
        ? 'Employee record could not be reached'
        : 'Employee record could not be loaded';

  return (
    <main className="mx-auto max-w-4xl space-y-5 px-4 py-10" aria-labelledby="employee-load-error-heading">
      <Link href={returnTo} className="font-semibold text-sapphire hover:underline">Back to People</Link>
      <section className="surface space-y-3 p-6" role="alert">
        <h1 id="employee-load-error-heading" className="text-xl font-semibold text-slate-950 dark:text-white">{heading}</h1>
        <p className="text-sm text-slate-600 dark:text-slate-300">{error.message}</p>
        <button type="button" className="btn-secondary" onClick={onRetry}>Retry employee record</button>
      </section>
    </main>
  );
}

function InvalidEmployeeLink() {
  return (
    <main className="mx-auto max-w-4xl space-y-5 px-4 py-10" aria-labelledby="invalid-employee-link-heading">
      <Link href="/people" className="font-semibold text-sapphire hover:underline">Back to People</Link>
      <section className="surface space-y-2 p-6" role="alert">
        <h1 id="invalid-employee-link-heading" className="text-xl font-semibold text-slate-950 dark:text-white">Invalid employee link</h1>
        <p className="text-sm text-slate-600 dark:text-slate-300">This employee address is not valid. Return to People and open the record again.</p>
      </section>
    </main>
  );
}

function ValidEmployeeWorkspace({ employeeId, mode }: { employeeId: number; mode: 'view' | 'edit' }) {
  const searchParams = useSearchParams();
  const record = useEmployeeRecord(employeeId);
  const browserOrigin = typeof window === 'undefined' ? 'https://people.invalid' : window.location.origin;
  const returnTo = validatePeopleReturnTo(searchParams.get('returnTo'), browserOrigin);
  const section = employeeSection(searchParams.get('section'));
  const panel = searchParams.get('panel') ?? undefined;

  if (record.detail.error) {
    return <EmployeeErrorState error={record.detail.error} returnTo={returnTo} onRetry={() => void record.refreshRecord()} />;
  }

  if (record.detail.loading || !record.detail.data) {
    return <main className="min-h-full bg-slate-50/60 px-4 py-6 dark:bg-slate-950/30" aria-busy="true"><div className="mx-auto max-w-7xl space-y-4"><Link href={returnTo} className="inline-flex min-h-11 items-center font-semibold text-sapphire hover:underline">Back to People</Link><section className="surface p-6" role="status"><p className="font-semibold text-slate-700 dark:text-slate-200">Loading employee record…</p></section></div></main>;
  }

  if (mode === 'edit') {
    return <EmployeeEditFlow
      employee={record.detail.data}
      section={section}
      panel={panel}
      returnTo={returnTo}
      onSaved={async (status) => {
        await record.refreshRecord();
        const target = employeeWorkspaceHref(record.detail.data!.id, { section, panel, returnTo });
        window.location.assign(`${target}&saved=${status === 202 ? 'approval' : 'updated'}`);
      }}
    />;
  }

  return <EmployeeWorkspaceShell
    employee={record.detail.data}
    section={section}
    panel={panel}
    mode={mode}
    returnTo={returnTo}
    readiness={record.readiness}
    savedNotice={searchParams.get('saved') === 'approval'
      ? 'Sensitive changes submitted to Approval Center.'
      : searchParams.get('saved') === 'updated' ? 'Employee details saved.' : undefined}
    onRetryReadiness={() => void record.refreshReadiness()}
    onEmployeeChanged={() => void record.refreshRecord()}
    onCostCentreChanged={() => void record.reloadRecordQuietly()}
  />;
}

export function EmployeeWorkspacePage({ employeeIdParam, mode = 'view' }: EmployeeWorkspacePageProps) {
  if (!isPositiveEmployeeId(employeeIdParam)) return <InvalidEmployeeLink />;
  return <ValidEmployeeWorkspace employeeId={Number(employeeIdParam)} mode={mode} />;
}
