import { PermissionGate } from '@/src/components/PermissionGate';
import { NewHiresPage } from '@/src/views/NewHiresPage';
export default function Page() {
  return (
    <PermissionGate permissions={['employees.write', 'employees.approve']}>
      <NewHiresPage />
    </PermissionGate>
  );
}
