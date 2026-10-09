import { PermissionGate } from '@/src/components/PermissionGate';
import { MyOnboardingPage } from '@/src/views/MyOnboardingPage';

export default function Page() {
  return <PermissionGate permissions={['ess.read']}><MyOnboardingPage /></PermissionGate>;
}
