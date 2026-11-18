import { PermissionGate } from '@/src/components/PermissionGate';
import { MyOpenPositionsPage } from '@/src/views/MyOpenPositionsPage';

export default function Page() {
  return (
    <PermissionGate permissions={['ess.read']}>
      <MyOpenPositionsPage />
    </PermissionGate>
  );
}
