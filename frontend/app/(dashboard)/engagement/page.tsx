import { PermissionGate } from '@/src/components/PermissionGate';
import { EngagementPage } from '@/src/views/EngagementPage';
export default function Page() {
  return (
    <PermissionGate permissions={['engagement.read', 'engagement.manage', 'engagement.actions.manage']}>
      <EngagementPage />
    </PermissionGate>
  );
}
