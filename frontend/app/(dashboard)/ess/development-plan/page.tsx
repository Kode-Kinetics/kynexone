import { PermissionGate } from '@/src/components/PermissionGate';
import { MyDevelopmentPlanPage } from '@/src/views/MyDevelopmentPlanPage';

// Every self-service page is gated on ess.read; the page itself needs succession.self and says so plainly without it.
export default function Page() {
  return (
    <PermissionGate permissions={['ess.read']}>
      <MyDevelopmentPlanPage />
    </PermissionGate>
  );
}
