'use client';

import { useSearchParams } from 'next/navigation';
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
  // Benefits by grade links here with `?loanType=` and `&companyId=`: Limits by grade opens on that loan type and company. Only the
  // address says so (Setup's remembered company scope does not), and one that does not exist for this viewer is ignored.
  const searchParams = useSearchParams();
  const focusLoanTypeId = searchParams?.get('loanType') ?? undefined;
  const focusCompanyId = searchParams?.get('companyId');
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
      focusLoanTypeId={focusLoanTypeId}
      focusCompanyId={focusCompanyId}
      onGradeLimitedChanged={(id, gradeLimited) => setLoanTypes(list => (list ?? []).map(type => (type.id === id ? { ...type, gradeLimited } : type)))}
    />
  );
}
