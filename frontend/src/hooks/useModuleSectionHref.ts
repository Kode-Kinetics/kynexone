'use client';

import { useAuth } from '../contexts/AuthContext';
import { useFeatureFlags } from '../contexts/FeatureFlagContext';
import { SETUP_MODULE_SECTIONS, moduleSectionAvailable, type SetupModuleSectionId } from '../lib/setupModuleSections';

/**
 * Where a module's Setup section lives for THIS user: the first of the given sections they may open, or null when they
 * may open none. The same rule `ModuleSettingsLink` applies, for a screen that must decide between "Add a device" (a link)
 * and "ask your administrator" (no link) so it never offers a door that ends in a refusal.
 */
export function useModuleSectionHref(sections: readonly SetupModuleSectionId[]): string | null {
  const { hasPermission, hasRole } = useAuth();
  const flags = useFeatureFlags();
  const target = sections
    .map(id => SETUP_MODULE_SECTIONS.find(section => section.id === id))
    .find(section => section && moduleSectionAvailable(section, {
      hasPermission, hasRole, isFeatureEnabled: flags.isFeatureEnabled, verdictForPath: flags.verdictForPath, flagStatus: flags.status,
    }));
  return target ? `/setup?tab=${target.id}` : null;
}
