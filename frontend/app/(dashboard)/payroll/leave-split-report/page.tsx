import { PermissionGate } from '@/src/components/PermissionGate';
import { LeaveSliceSplitReportPage } from '@/src/views/LeaveSliceSplitReportPage';

export default function Page() {
  return (
    // The endpoint is gated on payroll.read and writes nothing: the owner signs off before any row is split.
    <PermissionGate permissions={['payroll.read']}>
      <LeaveSliceSplitReportPage />
    </PermissionGate>
  );
}
