import { PublicShipmentTrackingPage } from '@/src/views/PublicShipmentTrackingPage';

export default async function TrackTokenPage({ params }: { params: Promise<{ token: string }> }) {
  const { token } = await params;
  return <PublicShipmentTrackingPage token={token} />;
}
