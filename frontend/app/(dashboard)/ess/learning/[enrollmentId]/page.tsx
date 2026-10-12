'use client';

import { useParams } from 'next/navigation';
import { PermissionGate } from '@/src/components/PermissionGate';
import { LearningCoursePage } from '@/src/views/LearningCoursePage';

export default function Page() {
  const { enrollmentId } = useParams<{ enrollmentId: string }>();
  return (
    <PermissionGate permissions={['ess.read']}>
      <LearningCoursePage enrollmentId={enrollmentId} />
    </PermissionGate>
  );
}
