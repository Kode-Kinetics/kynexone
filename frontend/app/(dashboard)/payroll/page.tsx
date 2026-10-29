import { Suspense } from 'react';
import { PermissionGate } from '@/src/components/PermissionGate';
import { PayrollPage } from '@/src/views/PayrollPage';
export default function Page() {
  // Suspense is required because PayrollPage reads useSearchParams (the ?tab=&run= deep link).
  return (
    <PermissionGate permissions={['payroll.read']}>
      <Suspense>
        <PayrollPage />
      </Suspense>
    </PermissionGate>
  );
}
