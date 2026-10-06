import { PermissionGate } from '@/src/components/PermissionGate';
import { MyPackagePage } from '@/src/views/MyPackagePage';
export default function Page() {
  return (
    <PermissionGate permissions={['ess.read']}>
      <MyPackagePage />
    </PermissionGate>
  );
}
