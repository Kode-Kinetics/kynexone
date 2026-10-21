import { Suspense } from 'react';
import { PermissionGate } from '@/src/components/PermissionGate';
import { CreateCompanyPage } from '@/src/views/CreateCompanyPage';

export default function Page() {
  // The API decides who may create (Admin or HR Manager, group scope, plan, creation mode) and says
  // why not at step 1; this gate only keeps people without organization write access off the page.
  // Suspense: the wizard reads ?draft= with useSearchParams.
  return (
    <PermissionGate permissions={['organization.write']}>
      <Suspense>
        <CreateCompanyPage />
      </Suspense>
    </PermissionGate>
  );
}
