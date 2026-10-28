import { PermissionGate } from '@/src/components/PermissionGate';
import { SignDocumentPage } from '@/src/views/ess/sign/SignDocumentPage';

export default async function Page({ params }: { params: Promise<{ documentId: string }> }) {
  const { documentId } = await params;
  return <PermissionGate permissions={['ess.read']}><SignDocumentPage documentId={documentId} /></PermissionGate>;
}
