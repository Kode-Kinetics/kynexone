'use client';

import { useEffect, useRef } from 'react';
import { useRouter, useSearchParams } from 'next/navigation';
import { useCompany } from '../contexts/CompanyContext';

/** Consume deep links once for the whole Setup page, including library visits. */
export function useSetupCompanyQuery() {
  const params = useSearchParams();
  const router = useRouter();
  const { companies, selectedCompanyId, setSelectedCompany, isGroupScope } = useCompany();
  const requested = params?.get('companyId') ?? null;
  const consumed = useRef<string | null | undefined>(undefined);
  const pendingSelection = useRef<string | null>(null);
  const authorized = selectedCompanyId && companies.some(company => company.id === selectedCompanyId) ? selectedCompanyId : null;
  useEffect(() => {
    // The starter has an explicit invalid-target screen. Preserve that fail-closed
    // route instead of quietly substituting another company.
    if (params?.get('mode') === 'starter' && requested && !companies.some(company => company.id === requested)) return;
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
      router.replace(`/setup?${next.toString()}`, { scroll: false });
    }
  }, [requested, authorized, companies, isGroupScope, params, router, setSelectedCompany]);
  return authorized;
}
