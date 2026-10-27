import { PermissionGate } from '@/src/components/PermissionGate';
import { PayApprovalDetailPage } from '@/src/views/PayApprovalDetailPage';
export default function Page() {
  return (
    <PermissionGate permissions={['payroll.approve']}>
      <PayApprovalDetailPage />
    </PermissionGate>
  );
}
