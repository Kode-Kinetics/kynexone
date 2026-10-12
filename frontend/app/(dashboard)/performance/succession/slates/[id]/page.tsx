'use client';

import { useParams } from 'next/navigation';
import { PermissionGate } from '@/src/components/PermissionGate';
import { SuccessionSlatePage } from '@/src/views/SuccessionSlatePage';

// As GET /api/succession/slates/{id}: HR, the accountable executive (approved versions), or the routed approver.
export default function Page() {
  const { id } = useParams<{ id: string }>();
  return (
    <PermissionGate permissions={['succession.slates.read', 'succession.self', 'approvals.decide']}>
      <SuccessionSlatePage id={id} />
    </PermissionGate>
  );
}
