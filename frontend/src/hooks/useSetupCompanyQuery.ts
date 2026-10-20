'use client';

import { useEffect, useRef } from 'react';
import { useRouter, useSearchParams } from 'next/navigation';
import { useCompany } from '../contexts/CompanyContext';
import { resolveSetupPath, serializeSetupPath } from '../lib/setupPathState';

/** Consume deep links once for the whole Setup page, including library visits. */
export function useSetupCompanyQuery() {
  const params = useSearchParams();
  const router = useRouter();
  const { companies, selectedCompanyId, setSelectedCompany, isGroupScope } = useCompany();
  const requested = params?.get('companyId') ?? null;
  const consumed = useRef<string | null | undefined>(undefined);
  const pendingSelection = useRef<string | null>(null);
  const pendingClear = useRef(false);
  // The company our own router.replace is writing into the URL. Until that replace
  // commits, `requested` still names the previous company (or none); treating that
  // stale value as a new deep link re-selected the old scope, so a company switch
  // flapped back and forth and a fresh /setup load briefly fell to All companies.
  const pendingUrl = useRef<{ from: string | null; companyId: string | null } | null>(null);
  const authorized = selectedCompanyId && companies.some(company => company.id === selectedCompanyId) ? selectedCompanyId : null;
  const pathResolution = resolveSetupPath(new URLSearchParams(params?.toString()), {
    authorizedCompanyIds: new Set(companies.map(company => company.id)),
    selectedCompanyId: authorized,
  });
  useEffect(() => {
    // The starter has an explicit invalid-target screen. Preserve that fail-closed
    // route instead of quietly substituting another company.
    if (params?.get('mode') === 'starter' && requested && !companies.some(company => company.id === requested)) return;
    if (pendingUrl.current) {
      // Resume once the URL moves (our replace landed, or a real navigation did) or the
      // selection changes; until then the URL is ours and merely stale.
      if (requested !== pendingUrl.current.from || authorized !== pendingUrl.current.companyId) pendingUrl.current = null;
      else return;
    }
    if (pendingClear.current) {
      if (authorized === null) pendingClear.current = false;
      else if (requested === null || !companies.some(company => company.id === requested)) return;
      else pendingClear.current = false;
    }
    if (pendingSelection.current) {
      if (authorized === pendingSelection.current) {
        consumed.current = pendingSelection.current;
        pendingSelection.current = null;
      } else if (requested === pendingSelection.current) {
        // Selecting through CompanyContext schedules a React state update. Do
        // not erase the deep link if this effect is replayed before that state
        // becomes observable (including development Strict Mode).
        return;
      } else {
        pendingSelection.current = null;
      }
    }
    if (pathResolution.notice === 'unauthorized-company') {
      // Keep the forged target visible in the URL so SetupPage can render an
      // explicit recovery state. Silently removing it would broaden a failed
      // company request into the all-company Configuration workspace.
      consumed.current = requested;
      if (authorized !== null) {
        pendingClear.current = true;
        setSelectedCompany(null);
      }
      return;
    }
    if (consumed.current !== requested) {
      const initial = consumed.current === undefined;
      if (requested && companies.some(company => company.id === requested) && requested !== authorized) {
        pendingSelection.current = requested;
        setSelectedCompany(requested);
        return;
      }
      consumed.current = requested;
      if (!initial && requested === null && isGroupScope && authorized !== null) {
        setSelectedCompany(null);
        return;
      }
    }
    if (requested !== authorized) {
      const next = new URLSearchParams(params?.toString());
      if (authorized) next.set('companyId', authorized); else next.delete('companyId');
      // Mark our own reconciliation as consumed: it must not re-select an old scope.
      consumed.current = authorized;
      pendingUrl.current = { from: requested, companyId: authorized };
      router.replace(`/setup?${next.toString()}`, { scroll: false });
      return;
    }
    if (pathResolution.needsReplace) {
      const next = serializeSetupPath(pathResolution, new URLSearchParams(params?.toString()));
      router.replace(`/setup?${next.toString()}`, { scroll: false });
    }
  }, [requested, authorized, companies, isGroupScope, params, pathResolution, router, setSelectedCompany]);
  return authorized;
}
