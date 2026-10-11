import { PermissionGate } from '@/src/components/PermissionGate';
import { CompensationPage } from '@/src/views/CompensationPage';

export default function Page() {
  // Any compensation key opens the page; each tab is then shown only to the keys it needs (CompensationPage).
  // The server enforces every rule again, and the compensation opt-in flag gates the API.
  return (
    <PermissionGate permissions={['compensation.read', 'compensation.plan', 'compensation.manage', 'compensation.approve', 'compensation.benchmarks.manage', 'compensation.equity.read']}>
      <CompensationPage />
    </PermissionGate>
  );
}
