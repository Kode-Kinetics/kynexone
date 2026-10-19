import { PermissionGate } from '@/src/components/PermissionGate';
import { RecruitmentPage } from '@/src/views/RecruitmentPage';

export default function Page() {
  return <PermissionGate permissions={['recruitment.read', 'recruitment.write']}>
    <RecruitmentPage initialTab="offers" initialOfferDesigner />
  </PermissionGate>;
}
