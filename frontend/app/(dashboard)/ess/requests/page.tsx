import { PermissionGate } from '@/src/components/PermissionGate';
import { MyRequestsPage } from '@/src/views/MyRequestsPage';
export default function Page() {
  return (
    <PermissionGate permissions={['ess.read']}>
      <MyRequestsPage />
    </PermissionGate>
  );
}
