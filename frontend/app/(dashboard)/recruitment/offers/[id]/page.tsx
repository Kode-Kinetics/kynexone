'use client';
import { useParams } from 'next/navigation';
import { PermissionGate } from '@/src/components/PermissionGate';
import { OfferWorkspace } from '@/src/components/recruitment/OfferWorkspace';
export default function Page() { const { id } = useParams<{ id: string }>(); return <PermissionGate permissions={['recruitment.read']}><OfferWorkspace id={id} /></PermissionGate>; }
