import { PermissionGate } from '@/src/components/PermissionGate';
import TenantAdminPage from '@/src/views/TenantAdminPage';
export default function Page() {
  return (
    <PermissionGate permissions={['security.manage']}>
      <TenantAdminPage />
    </PermissionGate>
  );
}
