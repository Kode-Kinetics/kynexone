'use client';

import { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react';
import { useAuth } from './AuthContext';
import type { CompanyAccess } from '../api/auth';
import { setActiveCompanyId } from '../api/client';

/**
 * Current-company selection for multi-company (Group) users.
 *
 * SECURITY MODEL: this context is a VIEW preference only. The selection travels to the
 * backend as the X-Company-Id header, where it can only NARROW the caller's token scope
 * — an inaccessible selection fails closed server-side. Nothing here hides data for
 * security; the backend query filters are the boundary.
 */
interface CompanyContextValue {
  companies: CompanyAccess[];
  /** null = "All companies" (group-scope users) or the only company (single). */
  selectedCompanyId: string | null;
  setSelectedCompany: (companyId: string | null) => void;
  isGroupScope: boolean;
  accountType: 'SingleCompany' | 'Group';
  /** Switcher renders only for users who can actually switch. */
  showSwitcher: boolean;
  /** Bumped on every switch — key data views on this to refetch. */
  companyVersion: number;
}

const CompanyContext = createContext<CompanyContextValue>({
  companies: [],
  selectedCompanyId: null,
  setSelectedCompany: () => {},
  isGroupScope: false,
  accountType: 'SingleCompany',
  showSwitcher: false,
  companyVersion: 0,
});

const storageKey = (userId: string) => `kynexone-company:${userId}`;

export function CurrentCompanyProvider({ children }: { children: React.ReactNode }) {
  const { user } = useAuth();
  const companies = useMemo(() => user?.companies ?? [], [user?.companies]);
  const isGroupScope = user?.isGroupScope ?? false;
  const accountType = user?.accountType ?? 'SingleCompany';
  const [selectedCompanyId, setSelectedCompanyId] = useState<string | null>(null);
  const [companyVersion, setCompanyVersion] = useState(0);

  // Restore the per-user selection; validate it is still accessible; scoped users
  // without a valid saved selection default to their first accessible company.
  useEffect(() => {
    if (!user) {
      setSelectedCompanyId(null);
      setActiveCompanyId(null);
      return;
    }
    let restored: string | null = null;
    try {
      restored = localStorage.getItem(storageKey(user.id));
    } catch { /* storage unavailable */ }
    const valid = restored && companies.some((c) => c.id === restored) ? restored : null;
    const initial = valid ?? (!isGroupScope && companies.length >= 1 ? companies[0].id : null);
    setSelectedCompanyId(initial);
    setActiveCompanyId(initial);
  }, [user, companies, isGroupScope]);

  const setSelectedCompany = useCallback((companyId: string | null) => {
    setSelectedCompanyId(companyId);
    setActiveCompanyId(companyId);
    setCompanyVersion((v) => v + 1);
    if (user) {
      try {
        if (companyId) localStorage.setItem(storageKey(user.id), companyId);
        else localStorage.removeItem(storageKey(user.id));
      } catch { /* storage unavailable */ }
    }
  }, [user]);

  const value = useMemo<CompanyContextValue>(() => ({
    companies,
    selectedCompanyId,
    setSelectedCompany,
    isGroupScope,
    accountType,
    showSwitcher: companies.length > 1,
    companyVersion,
  }), [companies, selectedCompanyId, setSelectedCompany, isGroupScope, accountType, companyVersion]);

  return <CompanyContext.Provider value={value}>{children}</CompanyContext.Provider>;
}

export function useCompany() {
  return useContext(CompanyContext);
}
