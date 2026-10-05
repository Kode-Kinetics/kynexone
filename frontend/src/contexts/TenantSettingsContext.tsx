'use client';

import { createContext, useContext, useEffect, useRef, useState, useCallback } from 'react';
import client from '../api/client';
import { DEFAULTS, readLocalization, type LocalizationAnswer, type TenantSettings } from '../lib/tenantLocalization';

export type { TenantSettings };

interface TenantSettingsContextValue {
  settings: TenantSettings;
  /**
   * True once the tenant's localization has been fetched successfully AND the API said it is the
   * tenant's own (`stated: true`, not the anonymous pre-sign-in placeholder). Until then `settings` are
   * DEFAULTS, which are placeholders, not the tenant's answer: anything that acts on a tenant
   * value (LocaleContext applying the default language) must wait for this. A failed fetch leaves
   * it as it was.
   */
  loaded: boolean;
  reload: () => Promise<void>;
}

const TenantSettingsContext = createContext<TenantSettingsContextValue>({
  settings: DEFAULTS,
  loaded: false,
  reload: async () => {},
});

export function TenantSettingsProvider({ children }: { children: React.ReactNode }) {
  const [settings, setSettings] = useState<TenantSettings>(DEFAULTS);
  const [loaded, setLoaded] = useState(false);
  const loadedRef = useRef(false);

  const load = useCallback(async () => {
    try {
      const { data } = await client.get<LocalizationAnswer>('/api/tenant-admin/localization');
      const answer = readLocalization(data);
      // Only a real tenant's answer counts as loaded: before sign-in the endpoint returns the
      // anonymous placeholder with stated: false (lib/tenantLocalization.ts). A placeholder never
      // replaces a tenant's answer already loaded.
      if (answer && (answer.stated || !loadedRef.current)) {
        setSettings(answer.settings);
        if (answer.stated) { loadedRef.current = true; setLoaded(true); }
      }
    } catch {
      // Keep what we had (defaults, or the last good load). Happens on first load before the auth
      // token is set; `loaded` stays as it was, so nothing acts on the placeholders.
    }
  }, []);

  useEffect(() => { load(); }, [load]);

  return (
    <TenantSettingsContext.Provider value={{ settings, loaded, reload: load }}>
      {children}
    </TenantSettingsContext.Provider>
  );
}

export function useTenantSettings(): TenantSettings {
  return useContext(TenantSettingsContext).settings;
}

export function useTenantSettingsContext(): TenantSettingsContextValue {
  return useContext(TenantSettingsContext);
}
