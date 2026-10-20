'use client';

import { useEffect } from 'react';

/** Cancel before CompanyContext updates the selection, request header or view key. */
export function useCompanyChangeGuard(confirmLeave?: (companyId?: string | null) => boolean) {
  useEffect(() => {
    if (!confirmLeave) return;
    const beforeCompany = (event: Event) => { if (!confirmLeave((event as CustomEvent<{ companyId: string | null }>).detail?.companyId)) event.preventDefault(); };
    window.addEventListener('kynexone:before-company-change', beforeCompany);
    return () => window.removeEventListener('kynexone:before-company-change', beforeCompany);
  }, [confirmLeave]);
}
