'use client';

import { Suspense } from 'react';
import { useParams } from 'next/navigation';
import { PermissionGate } from '@/src/components/PermissionGate';
import { CompleteCompanySetupPage } from '@/src/views/CompleteCompanySetupPage';

export default function Page() {
  // Existing mode of the company setup wizard (docs/design/company-setup-wizard.md, Addendum A). The API decides who
  // may complete which company (group admins any active company, a company super admin only their own) and the page
  // says so; this gate only keeps people without organization write access off it.
  // Suspense: the wizard reads ?draft= with useSearchParams.
  const { id } = useParams<{ id: string }>();
  return (
    <PermissionGate permissions={['organization.write']}>
      <Suspense>
        <CompleteCompanySetupPage companyId={id} />
      </Suspense>
    </PermissionGate>
  );
}
