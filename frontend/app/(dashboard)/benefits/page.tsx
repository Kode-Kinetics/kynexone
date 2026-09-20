import { PermissionGate } from '@/src/components/PermissionGate';
import { BenefitsPage } from '@/src/views/BenefitsPage';
export default function Page() {
  return (
    <PermissionGate permissions={['employees.write']}>
      <BenefitsPage />
    </PermissionGate>
  );
}
