import { PermissionGate } from '@/src/components/PermissionGate';
import { OffboardingPage } from '@/src/views/OffboardingPage';

export default function Page() {
  return (
    <PermissionGate permissions={['employees.write', 'payroll.approve']}>
      <OffboardingPage />
    </PermissionGate>
  );
}
