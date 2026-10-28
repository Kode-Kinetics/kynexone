import { PermissionGate } from '@/src/components/PermissionGate';
import { DocumentsToSignPage } from '@/src/views/ess/sign/DocumentsToSignPage';

export default function Page() {
  return <PermissionGate permissions={['ess.read']}><DocumentsToSignPage /></PermissionGate>;
}
