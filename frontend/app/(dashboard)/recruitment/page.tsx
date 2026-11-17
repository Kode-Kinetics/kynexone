import { PermissionGate } from '@/src/components/PermissionGate';
import { RecruitmentPage } from '@/src/views/RecruitmentPage';
export default function Page() {
  return (
    <PermissionGate permissions={['recruitment.read','recruitment.write','recruitment.requisition.raise','recruitment.interview.feedback']}>
      <RecruitmentPage />
    </PermissionGate>
  );
}
