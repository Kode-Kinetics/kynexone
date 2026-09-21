import { PermissionGate } from '@/src/components/PermissionGate';
import { GosiFilingPage } from '@/src/views/GosiFilingPage';
export default function Page() {
  return (
    <PermissionGate permissions={['payroll.read']}>
      <GosiFilingPage />
    </PermissionGate>
  );
}
