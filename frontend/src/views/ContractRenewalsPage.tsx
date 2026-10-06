'use client';

import { ReleaseAGate, ReleaseAPlaceholder } from '../components/releaseA/ReleaseAGate';
import { msg } from '../i18n/translations';

/**
 * Contract renewals — Release A slice R4 owns this view. R0 created it with the route, the permission gate and the release_a
 * gate; the slice replaces the placeholder body. Strings: src/i18n/releaseA (the slice's own file).
 */
export function ContractRenewalsPage() {
  return (
    <ReleaseAGate>
      <ReleaseAPlaceholder title={msg('Contract renewals')} purpose={msg('Every contract ending in the next 120 days, what is due next, and what happens if it is missed.')} />
    </ReleaseAGate>
  );
}
