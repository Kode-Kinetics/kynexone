import { Suspense } from 'react';
import { PermissionGate } from '@/src/components/PermissionGate';
import { SetupPage } from '@/src/views/SetupPage';
export default function Page() {
  // establishment.write is independently grantable — its holders must be able to
  // reach Cost Centres & Budget via the blocked-assignment popup's deep link.
  // Suspense is required because SetupPage reads useSearchParams (deep-link focus).
  return (
    <PermissionGate permissions={['organization.write', 'organization.establishment.write']}>
      <Suspense>
        <SetupPage />
      </Suspense>
    </PermissionGate>
  );
}
