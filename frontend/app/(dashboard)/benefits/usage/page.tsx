import { PermissionGate } from '@/src/components/PermissionGate';
import { BenefitUsagePage } from '@/src/views/BenefitUsagePage';
// HR reads with entitlements.read, Finance with benefits.usage.approve; the API re-checks every action and the company scope.
export default function Page() {
  return (
    <PermissionGate permissions={['entitlements.read', 'benefits.usage.approve']}>
      <BenefitUsagePage />
    </PermissionGate>
  );
}
