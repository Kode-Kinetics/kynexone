import { Suspense } from 'react';
import { PermissionGate } from '@/src/components/PermissionGate';
import { SetupPage } from '@/src/views/SetupPage';
import { GL_AND_RATES_PERMISSIONS } from '@/src/lib/setupAccess';
export default function Page() {
  // establishment.write is independently grantable — its holders must be able to
  // reach Cost Centres & Budget via the blocked-assignment popup's deep link.
  // organization.read is included so read-only company viewers (folded in from the
  // retired /companies route) can reach the Companies tab; SetupPage itself restricts
  // those users to the read-only Companies list (no write actions, no config tabs).
  // Any GL or rates permission opens Setup → GL & rates (src/lib/setupAccess.ts).
  // Suspense is required because SetupPage reads useSearchParams (deep-link focus).
  return (
    <PermissionGate permissions={['organization.read', 'organization.write', 'organization.establishment.write', ...GL_AND_RATES_PERMISSIONS]}>
      <Suspense>
        <SetupPage />
      </Suspense>
    </PermissionGate>
  );
}
