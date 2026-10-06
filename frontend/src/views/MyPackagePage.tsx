'use client';

import { ReleaseAGate, ReleaseAPlaceholder } from '../components/releaseA/ReleaseAGate';

/**
 * My package — Release A slice R2 owns this view. R0 created it with the route, the permission gate and the release_a
 * gate; the slice replaces the placeholder body. Strings: src/i18n/releaseA (the slice's own file).
 */
export function MyPackagePage() {
  return (
    <ReleaseAGate>
      <ReleaseAPlaceholder title="My package" purpose="Your pay, your contract benefits for this contract year, and the facilities your grade offers." />
    </ReleaseAGate>
  );
}
