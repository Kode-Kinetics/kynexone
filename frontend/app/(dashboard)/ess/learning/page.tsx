import { PermissionGate } from '@/src/components/PermissionGate';
import { MyLearningPage } from '@/src/views/MyLearningPage';

export default function Page() {
  return (
    <PermissionGate permissions={['ess.read']}>
      <MyLearningPage />
    </PermissionGate>
  );
}
