'use client';

import { useEffect, useState } from 'react';
import { useFeatureFlags } from '../../contexts/FeatureFlagContext';
import { useLocale } from '@/src/contexts/LocaleContext';
import { createFlowMode, FLAGS_WAIT_MS } from '../../lib/hire/createFlowMode';
import { EmployeesPage } from '../EmployeesPage';
import { HireWizard } from './hire/HireWizard';

/**
 * Canonical owner for direct employee creation.
 *
 * `hire_wizard_v2` (opt-in, default OFF, switched per tenant by the platform) selects the new Add Employee wizard.
 * The legacy create modal is the default (CD57): a flag fetch that failed, or has not answered within
 * FLAGS_WAIT_MS, renders it. While the flags load a status message is shown instead of a blank screen, so a
 * flag-ON tenant never flashes the legacy modal (and its data loads).
 */
export function EmployeeCreateFlow() {
  const { status, isFeatureEnabled } = useFeatureFlags();
  const { t } = useLocale();
  const [waitedOut, setWaitedOut] = useState(false);
  useEffect(() => {
    if (status !== 'loading') return;
    const timer = setTimeout(() => setWaitedOut(true), FLAGS_WAIT_MS);
    return () => clearTimeout(timer);
  }, [status]);

  const mode = createFlowMode({ status, wizardOn: isFeatureEnabled('hire_wizard_v2'), waitedOut });
  if (mode === 'loading') return <div role="status" aria-busy="true" aria-live="polite" className="p-8 text-sm text-slate-500">{t('Loading…')}</div>;
  if (mode === 'wizard') return <HireWizard />;
  return <EmployeesPage createOnly />;
}
