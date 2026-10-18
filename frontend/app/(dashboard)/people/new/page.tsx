import { PermissionGate } from '@/src/components/PermissionGate';
import { EmployeeCreateFlow } from '@/src/views/employees/EmployeeCreateFlow';

export default function Page() {
  return (
    <PermissionGate permissions={['employees.write']}>
      <EmployeeCreateFlow />
    </PermissionGate>
  );
}
