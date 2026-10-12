'use client';

import { useParams } from 'next/navigation';
import { PermissionGate } from '@/src/components/PermissionGate';
import { LearningCertificatePage } from '@/src/views/LearningCertificatePage';

export default function Page() {
  const { credentialId } = useParams<{ credentialId: string }>();
  return (
    <PermissionGate permissions={['ess.read']}>
      <LearningCertificatePage credentialId={credentialId} />
    </PermissionGate>
  );
}
