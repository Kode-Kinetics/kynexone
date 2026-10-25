'use client';

import { useCallback, useEffect, useState } from 'react';
import { loanTypesApi } from '../../api/loans';
import type { LoanType } from '../../api/loans';
import { useCompany } from '../../contexts/CompanyContext';
import { LoadFailedNotice } from '../ui/LoadFailedRow';
import { LoanPoliciesTab } from './LoanPoliciesTab';

/**
 * Loan policies as a Setup section. The editor needs the loan types (to pick one and to switch grade limits on or off),
 * which the Loans page used to hand it; here the panel loads them itself, and again when the company changes.
 */
export function LoanPoliciesPanel() {
  const { companyVersion } = useCompany();
  const [loanTypes, setLoanTypes] = useState<LoanType[] | null>(null);
  const [loadError, setLoadError] = useState<unknown>(null);

  const load = useCallback(async () => {
    setLoanTypes(null);
    setLoadError(null);
    try { setLoanTypes(await loanTypesApi.list()); }
    catch (error) { setLoadError(error); }
  }, []);
  useEffect(() => { void load(); }, [load, companyVersion]);

  if (loadError != null) return <div className="surface"><LoadFailedNotice error={loadError} onRetry={() => { void load(); }} /></div>;
  if (loanTypes === null) {
    return <div className="flex justify-center py-16" aria-busy="true"><div className="h-6 w-6 animate-spin rounded-full border-2 border-sapphire border-t-transparent" /></div>;
  }
  return (
    <LoanPoliciesTab
      key={companyVersion}
      loanTypes={loanTypes}
      onGradeLimitedChanged={(id, gradeLimited) => setLoanTypes(list => (list ?? []).map(type => (type.id === id ? { ...type, gradeLimited } : type)))}
    />
  );
}
