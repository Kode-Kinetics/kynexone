import { PermissionGate } from '@/src/components/PermissionGate';
import { LoansPage } from '@/src/views/LoansPage';
export default function Page() {
  return (
    <PermissionGate permissions={['loans.self','loans.read','loans.write']}>
      <LoansPage />
    </PermissionGate>
  );
}
