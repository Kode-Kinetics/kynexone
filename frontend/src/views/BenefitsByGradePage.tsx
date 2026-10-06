'use client';

import { ReleaseAGate, ReleaseAPlaceholder } from '../components/releaseA/ReleaseAGate';

/**
 * Benefits by grade — Release A slice R1 owns this view. R0 created it with the route, the permission gate and the release_a
 * gate; the slice replaces the placeholder body. Strings: src/i18n/releaseA (the slice's own file).
 */
export function BenefitsByGradePage() {
  return (
    <ReleaseAGate>
      <ReleaseAPlaceholder title="Benefits by grade" purpose="Set every benefit by grade in one place: housing, transport, tickets, medical cover, education and per diem." />
    </ReleaseAGate>
  );
}
