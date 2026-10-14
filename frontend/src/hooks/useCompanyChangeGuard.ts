'use client';

import { useEffect } from 'react';

/** Cancel before CompanyContext updates the selection, request header or view key. */
export function useCompanyChangeGuard(confirmLeave?: () => boolean) {
  useEffect(() => {
    if (!confirmLeave) return;
    const beforeCompany = (event: Event) => { if (!confirmLeave()) event.preventDefault(); };
    window.addEventListener('kynexone:before-company-change', beforeCompany);
    return () => window.removeEventListener('kynexone:before-company-change', beforeCompany);
  }, [confirmLeave]);
}
