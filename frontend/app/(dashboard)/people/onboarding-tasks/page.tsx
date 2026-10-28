import { PermissionGate } from '@/src/components/PermissionGate';
import { OnboardingWorklistPage } from '@/src/views/people/OnboardingWorklistPage';

export default function Page() {
  return (
    <PermissionGate permissions={['employees.read']}>
      <OnboardingWorklistPage />
    </PermissionGate>
  );
}
