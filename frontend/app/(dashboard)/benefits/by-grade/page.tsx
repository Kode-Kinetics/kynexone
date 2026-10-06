import { PermissionGate } from '@/src/components/PermissionGate';
import { BenefitsByGradePage } from '@/src/views/BenefitsByGradePage';
export default function Page() {
  return (
    <PermissionGate permissions={['entitlements.read']}>
      <BenefitsByGradePage />
    </PermissionGate>
  );
}
