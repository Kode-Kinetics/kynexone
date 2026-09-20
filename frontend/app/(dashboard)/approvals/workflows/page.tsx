import { PermissionGate } from '@/src/components/PermissionGate';
import { ApprovalWorkflowsPage } from '@/src/views/ApprovalWorkflowsPage';
export default function Page() {
  return (
    <PermissionGate permissions={['approvals.manage']}>
      <ApprovalWorkflowsPage />
    </PermissionGate>
  );
}
