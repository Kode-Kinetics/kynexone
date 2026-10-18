'use client';

import Link from 'next/link';
import { useSearchParams } from 'next/navigation';
import { useEmployeeRecord, type EmployeeLoadError } from './useEmployeeRecord';
import { validatePeopleReturnTo } from '../../lib/peopleRoutes';

interface EmployeeWorkspacePageProps {
  employeeIdParam: string;
  mode?: 'view' | 'edit';
}

function isPositiveEmployeeId(value: string): boolean {
  return /^[1-9]\d*$/.test(value) && Number.isSafeInteger(Number(value));
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
        <h1 id="employee-load-error-heading" className="text-xl font-bold text-slate-950 dark:text-white">{heading}</h1>
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
        <h1 id="invalid-employee-link-heading" className="text-xl font-bold text-slate-950 dark:text-white">Invalid employee link</h1>
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

  if (record.detail.error) {
    return <EmployeeErrorState error={record.detail.error} returnTo={returnTo} onRetry={() => void record.refreshRecord()} />;
  }

  return (
    <main className="min-h-full bg-slate-50/60 px-4 py-6 dark:bg-slate-950/30" aria-busy={record.detail.loading}>
      <div className="mx-auto max-w-7xl space-y-4">
        <Link href={returnTo} className="inline-flex min-h-11 items-center font-semibold text-sapphire hover:underline">Back to People</Link>

        {record.detail.loading || !record.detail.data ? (
          <section className="surface p-6" role="status">
            <p className="font-semibold text-slate-700 dark:text-slate-200">Loading employee record…</p>
          </section>
        ) : (
          <>
            <header className="surface flex flex-wrap items-center justify-between gap-4 p-5">
              <div className="min-w-0">
                <div className="flex flex-wrap items-center gap-3">
                  <h1 className="text-2xl font-bold text-slate-950 dark:text-white">{record.detail.data.fullName}</h1>
                  <span className="rounded-full bg-amber-100 px-3 py-1 text-xs font-semibold text-amber-800 dark:bg-amber-500/15 dark:text-amber-200">
                    {record.detail.data.status}
                  </span>
                </div>
                <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
                  {record.detail.data.employeeCode} · {record.detail.data.department} · {record.detail.data.designation}
                </p>
              </div>
              <span className="text-sm font-semibold text-slate-600 dark:text-slate-300">
                {mode === 'edit' ? 'Employee edit workspace' : 'Employee record'}
              </span>
            </header>

            <section className="surface p-5" aria-label="Employee readiness">
              {record.readiness.loading ? (
                <p role="status" className="text-sm text-slate-600 dark:text-slate-300">Loading readiness…</p>
              ) : record.readiness.error ? (
                <div role="alert" className="flex flex-wrap items-center justify-between gap-3">
                  <p className="text-sm text-rose-700 dark:text-rose-300">Readiness could not be loaded.</p>
                  <button type="button" className="btn-secondary" onClick={() => void record.refreshReadiness()}>Retry readiness</button>
                </div>
              ) : record.readiness.data ? (
                <div className="space-y-2">
                  <p className="font-semibold text-slate-900 dark:text-white">
                    {record.readiness.data.progress.present} of {record.readiness.data.progress.requiredTotal} complete
                  </p>
                  <progress
                    className="h-2 w-full max-w-sm"
                    value={record.readiness.data.progress.present}
                    max={Math.max(record.readiness.data.progress.requiredTotal, 1)}
                    aria-label="Employee readiness progress"
                  />
                </div>
              ) : null}
            </section>
          </>
        )}
      </div>
    </main>
  );
}

export function EmployeeWorkspacePage({ employeeIdParam, mode = 'view' }: EmployeeWorkspacePageProps) {
  if (!isPositiveEmployeeId(employeeIdParam)) return <InvalidEmployeeLink />;
  return <ValidEmployeeWorkspace employeeId={Number(employeeIdParam)} mode={mode} />;
}
