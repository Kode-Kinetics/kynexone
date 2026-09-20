import { PermissionGate } from '@/src/components/PermissionGate';
import { MyExpensesPage } from '@/src/views/expenses/MyExpensesPage';
export default function Page() {
  return (
    <PermissionGate permissions={['ess.read']}>
      <MyExpensesPage />
    </PermissionGate>
  );
}
