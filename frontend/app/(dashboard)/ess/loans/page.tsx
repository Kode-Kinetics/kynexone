import { PermissionGate } from '@/src/components/PermissionGate';
import { MyLoansPage } from '@/src/views/LoansPage';
export default function Page() {
  return (
    <PermissionGate permissions={['ess.read']}>
      <PermissionGate permissions={['loans.self']}>
        <MyLoansPage />
      </PermissionGate>
    </PermissionGate>
  );
}
