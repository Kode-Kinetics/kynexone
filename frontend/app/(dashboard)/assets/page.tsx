import { PermissionGate } from '@/src/components/PermissionGate';
import { AssetsPage } from '@/src/views/AssetsPage';

export default function Page() {
  return (
    <PermissionGate permissions={['employees.read', 'employees.write']}>
      <AssetsPage />
    </PermissionGate>
  );
}
