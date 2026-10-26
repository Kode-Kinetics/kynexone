'use client';

import { useFeatureFlags } from '../../contexts/FeatureFlagContext';
import { EmployeesPage } from '../EmployeesPage';
import { HireWizard } from './hire/HireWizard';

/**
 * Canonical owner for direct employee creation.
 *
 * `hire_wizard_v2` (opt-in, default OFF, switched per tenant by the platform) selects the new Add Employee wizard.
 * Anything else, including a flag fetch that failed, renders the legacy create modal untouched. While the flags are
 * still loading nothing is rendered, so a flag-ON tenant never flashes the legacy modal (and its data loads).
 */
export function EmployeeCreateFlow() {
  const { status, isFeatureEnabled } = useFeatureFlags();
  if (status === 'loading') return null;
  if (status === 'ready' && isFeatureEnabled('hire_wizard_v2')) return <HireWizard />;
  return <EmployeesPage createOnly />;
}
