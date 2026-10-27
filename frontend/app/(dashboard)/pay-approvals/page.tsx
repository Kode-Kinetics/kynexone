import { PermissionGate } from '@/src/components/PermissionGate';
import { PayApprovalsPage } from '@/src/views/PayApprovalsPage';
export default function Page() {
  return (
    <PermissionGate permissions={['payroll.approve']}>
      <PayApprovalsPage />
    </PermissionGate>
  );
}
