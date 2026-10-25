'use client';

import Link from 'next/link';
import { Settings2 } from 'lucide-react';
import { useAuth } from '../../contexts/AuthContext';
import { useFeatureFlags } from '../../contexts/FeatureFlagContext';
import { useT } from '../../hooks/useT';
import { SETUP_MODULE_SECTIONS, moduleSectionAvailable, type SetupModuleSectionId } from '../../lib/setupModuleSections';

/**
 * A module's door to its settings in Setup ("Leave settings"), placed in the module's header. It opens the first of the
 * given sections this user may open, and is not shown at all when they may open none, so it never leads to a refusal.
 */
export function ModuleSettingsLink({ sections, label, className = '' }: {
  /** The module's Setup sections, in the order the door should prefer. */
  sections: readonly SetupModuleSectionId[];
  /** Translated by the caller's dictionary key, e.g. 'Leave settings'. */
  label: string;
  className?: string;
}) {
  const t = useT();
  const { hasPermission, hasRole } = useAuth();
  const flags = useFeatureFlags();
  const target = sections
    .map(id => SETUP_MODULE_SECTIONS.find(section => section.id === id))
    .find(section => section && moduleSectionAvailable(section, {
      hasPermission, hasRole, isFeatureEnabled: flags.isFeatureEnabled, verdictForPath: flags.verdictForPath, flagStatus: flags.status,
    }));
  if (!target) return null;
  return (
    <Link
      href={`/setup?tab=${target.id}`}
      data-module-settings-link={target.id}
      className={`inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-3 py-1.5 text-sm font-medium text-slate-600 transition hover:bg-slate-50 hover:text-slate-900 dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5 dark:hover:text-white ${className}`}
    >
      <Settings2 className="h-4 w-4" aria-hidden="true" />
      {t(label)}
    </Link>
  );
}
