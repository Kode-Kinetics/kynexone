import { PermissionGate } from '@/src/components/PermissionGate';
import { LearningPage } from '@/src/views/LearningPage';

export default function Page() {
  return (
    <PermissionGate permissions={['learning.read', 'learning.manage', 'learning.assign', 'learning.report', 'learning.records.correct']}>
      <LearningPage />
    </PermissionGate>
  );
}
