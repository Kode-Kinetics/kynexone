'use client';

import { useMemo } from 'react';
import { EntitlementMatrix } from '../components/entitlements/EntitlementMatrix';
import { ReleaseAGate } from '../components/releaseA/ReleaseAGate';
import { useCompany } from '../contexts/CompanyContext';

/**
 * Benefits by grade (Release A slice R1): every benefit, grade by grade, for the group and per company. Behind the
 * release_a flag (ReleaseAGate here, 403 feature_not_enabled at the API) and entitlements.read (the route's gate).
 */
export function BenefitsByGradePage() {
  const { companies } = useCompany();
  const active = useMemo(() => companies.filter(c => c.isActive).map(c => ({ id: c.id, name: c.name })), [companies]);
  return (
    <ReleaseAGate>
      <EntitlementMatrix companies={active} />
    </ReleaseAGate>
  );
}
