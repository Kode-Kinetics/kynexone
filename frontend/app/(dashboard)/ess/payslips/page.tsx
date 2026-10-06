import { PermissionGate } from '@/src/components/PermissionGate';
import { MyPayslipsPage } from '@/src/views/MyPayslipsPage';
export default function Page() {
  return (
    <PermissionGate permissions={['ess.read']}>
      <MyPayslipsPage />
    </PermissionGate>
  );
}
