'use client';

import { useParams } from 'next/navigation';
import { PermissionGate } from '@/src/components/PermissionGate';
import { EmployeeWorkspacePage } from '@/src/views/employees/EmployeeWorkspacePage';

export default function Page() {
  const { employeeId } = useParams<{ employeeId: string }>();
  return (
    <PermissionGate permissions={['employees.write']}>
      <EmployeeWorkspacePage employeeIdParam={employeeId} mode="edit" />
    </PermissionGate>
  );
}
