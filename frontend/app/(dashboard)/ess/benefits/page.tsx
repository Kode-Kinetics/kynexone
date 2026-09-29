import { PermissionGate } from '@/src/components/PermissionGate';
import { MyBenefitsPage } from '@/src/views/MyBenefitsPage';
export default function Page() {
  return (
    <PermissionGate permissions={['ess.read']}>
      <MyBenefitsPage />
    </PermissionGate>
  );
}
