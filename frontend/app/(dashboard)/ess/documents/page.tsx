import { PermissionGate } from '@/src/components/PermissionGate';
import { MyDocumentsPage } from '@/src/views/MyDocumentsPage';
export default function Page() {
  return (
    <PermissionGate permissions={['ess.read']}>
      <MyDocumentsPage />
    </PermissionGate>
  );
}
