import { PermissionGate } from '@/src/components/PermissionGate';
import { JawazatPanel } from '@/src/components/compliance/JawazatPanel';

export default function Page() {
  return <PermissionGate permissions={['ess.read']}><div className="p-4 sm:p-6"><JawazatPanel own /></div></PermissionGate>;
}
