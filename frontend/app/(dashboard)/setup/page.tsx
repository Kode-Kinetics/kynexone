import { Suspense } from 'react';
import { PermissionGate } from '@/src/components/PermissionGate';
import { SetupPage } from '@/src/views/SetupPage';
import { SETUP_ROUTE_PERMISSIONS } from '@/src/lib/setupModuleSections';
export default function Page() {
  // Setup is the one home for configuration, so any permission of any of its sections opens it
  // (lib/setupModuleSections.ts); SetupPage then shows each reader only the sections their permissions,
  // roles and switched-on modules allow. establishment.write must reach Cost Centres & Budget through the
  // blocked-assignment popup's deep link; organization.read reaches the read-only Companies list.
  // Suspense is required because SetupPage reads useSearchParams (deep-link focus).
  return (
    <PermissionGate permissions={[...SETUP_ROUTE_PERMISSIONS]}>
      <Suspense>
        <SetupPage />
      </Suspense>
    </PermissionGate>
  );
}
