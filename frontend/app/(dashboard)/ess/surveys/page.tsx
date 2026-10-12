import { PermissionGate } from '@/src/components/PermissionGate';
import { MySurveysPage } from '@/src/views/MySurveysPage';
export default function Page() {
  return (
    <PermissionGate permissions={['ess.read']}>
      <MySurveysPage />
    </PermissionGate>
  );
}
