import { PermissionGate } from '@/src/components/PermissionGate';
import HrLettersPage from '@/src/views/HrLettersPage';

export default function Page() {
  return (
    <PermissionGate permissions={['employees.read', 'employees.write']}>
      <HrLettersPage />
    </PermissionGate>
  );
}
