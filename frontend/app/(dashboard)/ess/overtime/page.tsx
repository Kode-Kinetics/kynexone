import { PermissionGate } from '@/src/components/PermissionGate';
import { MyOvertimePage } from '@/src/views/MyOvertimePage';
export default function Page() {
  return (
    <PermissionGate permissions={['ess.read']}>
      <MyOvertimePage />
    </PermissionGate>
  );
}
