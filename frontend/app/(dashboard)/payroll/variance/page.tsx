import { PermissionGate } from '@/src/components/PermissionGate';
import { ParallelRunVariancePage } from '@/src/views/ParallelRunVariancePage';

export default function Page() {
  return (
    // The endpoint is gated on payroll.read — it writes nothing and only reads payroll output.
    <PermissionGate permissions={['payroll.read']}>
      <ParallelRunVariancePage />
    </PermissionGate>
  );
}
