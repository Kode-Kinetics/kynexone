import { PermissionGate } from '@/src/components/PermissionGate';
import { ExpensesPage } from '@/src/views/expenses/ExpensesPage';
export default function Page() {
  return (
    <PermissionGate permissions={['approvals.decide', 'payroll.read', 'approvals.read']}>
      <ExpensesPage />
    </PermissionGate>
  );
}
