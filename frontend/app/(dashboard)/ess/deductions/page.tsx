import { PermissionGate } from '@/src/components/PermissionGate';
import { MyDeductionsPage } from '@/src/views/MyDeductionsPage';
export default function Page() {
  return (
    <PermissionGate permissions={['ess.read']}>
      <MyDeductionsPage />
    </PermissionGate>
  );
}
