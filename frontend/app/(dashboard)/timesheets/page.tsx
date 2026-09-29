import { PermissionGate } from '@/src/components/PermissionGate';
import { TimesheetsPage } from '@/src/views/TimesheetsPage';

export default function Page() {
  // Any-of. An employee reaches this page on ess.read (their own week); a manager or HR officer
  // reaches it on attendance.read (the register, the queue, the variance report). The page itself
  // then shows only the tabs the caller's permissions actually cover.
  return (
    <PermissionGate permissions={['ess.read', 'attendance.read', 'manager.read']}>
      <TimesheetsPage />
    </PermissionGate>
  );
}
