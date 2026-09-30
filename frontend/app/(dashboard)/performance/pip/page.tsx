import { PermissionGate } from '@/src/components/PermissionGate';
import { PERFORMANCE_TAB_PERMISSIONS } from '@/src/lib/performanceAccess';
import { PerformancePage } from '@/src/views/PerformancePage';
export default function Page() {
  return (
    <PermissionGate permissions={[...PERFORMANCE_TAB_PERMISSIONS.pip]}>
      <PerformancePage initialTab="pip" />
    </PermissionGate>
  );
}
