import { PermissionGate } from '@/src/components/PermissionGate';
import { ContractRenewalsPage } from '@/src/views/ContractRenewalsPage';
export default function Page() {
  return (
    <PermissionGate permissions={['contracts.renewal.read']}>
      <ContractRenewalsPage />
    </PermissionGate>
  );
}
