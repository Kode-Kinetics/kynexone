import { PermissionGate } from '@/src/components/PermissionGate';
import { MyLeavePage } from '@/src/views/MyLeavePage';
export default function Page() {
  return (
    <PermissionGate permissions={['ess.read']}>
      <MyLeavePage />
    </PermissionGate>
  );
}
