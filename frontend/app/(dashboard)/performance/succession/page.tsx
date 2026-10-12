import { PermissionGate } from '@/src/components/PermissionGate';
import { SuccessionPage } from '@/src/views/SuccessionPage';

// The same keys as the navigation entry (NavigationPageGateParityTests): every succession key except the employee's own.
export default function Page() {
  return (
    <PermissionGate permissions={['succession.read', 'succession.positions.manage', 'succession.pools.manage', 'succession.nominate', 'succession.assess', 'succession.calibrate', 'succession.slates.read', 'succession.slates.manage', 'succession.devplans.read', 'succession.devplans.manage', 'succession.reports.read']}>
      <SuccessionPage />
    </PermissionGate>
  );
}
