import { PermissionGate } from '@/src/components/PermissionGate';
import { CompletionReviewPage } from '@/src/views/people/CompletionReviewPage';

export default function Page() {
  return <PermissionGate permissions={['employees.write']}><CompletionReviewPage /></PermissionGate>;
}
