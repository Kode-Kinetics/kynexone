'use client';

import { useParams } from 'next/navigation';
import { PermissionGate } from '@/src/components/PermissionGate';
import { SuccessionPositionPage } from '@/src/views/SuccessionPositionPage';

// succession.self: the accountable executive reaches their own position read-only (the API decides which ones).
export default function Page() {
  const { id } = useParams<{ id: string }>();
  return (
    <PermissionGate permissions={['succession.read', 'succession.self']}>
      <SuccessionPositionPage id={id} />
    </PermissionGate>
  );
}
