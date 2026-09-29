import { PermissionGate } from '@/src/components/PermissionGate';
import { MigrationImportPage } from '@/src/views/MigrationImportPage';

export default function Page() {
  return (
    // The engine itself is gated on the Admin / HR Manager roles; this mirrors that in the UI so the
    // screen is not offered to someone whose first click would 403.
    <PermissionGate permissions={['employees.write', 'payroll.write']}>
      <MigrationImportPage />
    </PermissionGate>
  );
}
