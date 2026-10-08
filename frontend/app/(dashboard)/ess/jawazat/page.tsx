import { PermissionGate } from '@/src/components/PermissionGate';
import { JawazatPanel } from '@/src/components/compliance/JawazatPanel';

export default function Page() {
  return <PermissionGate permissions={['ess.read']}><JawazatPanel own /></PermissionGate>;
}
