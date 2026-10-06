'use client';

import { ReleaseAGate, ReleaseAPlaceholder } from '../components/releaseA/ReleaseAGate';
import { msg } from '../i18n/translations';

/**
 * My deductions — Release A slice R3 owns this view. R0 created it with the route, the permission gate and the release_a
 * gate; the slice replaces the placeholder body. Strings: src/i18n/releaseA (the slice's own file).
 */
export function MyDeductionsPage() {
  return (
    <ReleaseAGate>
      <ReleaseAPlaceholder title={msg('My deductions')} purpose={msg('Every deduction from your pay, why it is made, and what is left to repay.')} />
    </ReleaseAGate>
  );
}
