'use client';

import { Suspense, useEffect } from 'react';
import { useRouter, useSearchParams } from 'next/navigation';
import { PermissionGate } from '@/src/components/PermissionGate';
import { EmployeesPage } from '@/src/views/EmployeesPage';
import { legacyEmployeeTarget } from '@/src/lib/peopleRoutes';

function PeopleRouteOwner() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const legacyTarget = legacyEmployeeTarget(new URLSearchParams(searchParams.toString()));

  useEffect(() => {
    if (legacyTarget) router.replace(legacyTarget);
  }, [legacyTarget, router]);

  if (legacyTarget) {
    return <p className="p-6 text-sm text-slate-600 dark:text-slate-300" role="status">Opening employee workspace…</p>;
  }

  return (
    <PermissionGate permissions={['employees.read']}>
      <EmployeesPage />
    </PermissionGate>
  );
}

export default function Page() {
  return (
    <Suspense fallback={<p className="p-6 text-sm text-slate-600 dark:text-slate-300" role="status">Loading People…</p>}>
      <PeopleRouteOwner />
    </Suspense>
  );
}
