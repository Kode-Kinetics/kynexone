'use client';

import { useEffect, useState } from 'react';
import Link from 'next/link';
import { Building2 } from 'lucide-react';
import { companySetupApi, type ManageableCompany } from '../../api/companySetup';
import { useAuth } from '../../contexts/AuthContext';
import { useT } from '../../hooks/useT';

/** The permission the create-company wizard needs (CompanySetupController.ApplyPermission). */
const SETUP_PERMISSION = 'organization.setup.apply';

/**
 * Asks the administrator to finish a company whose legal entity details are missing. The first company of an
 * installation is created by `--setup-organisation` with only its English name, so this is the first thing a new
 * organisation admin sees. Shown only to callers who may complete it; a failed read shows nothing.
 */
export function CompanySetupPrompt() {
  const t = useT();
  const { hasPermission } = useAuth();
  const allowed = hasPermission(SETUP_PERMISSION);
  const [companies, setCompanies] = useState<ManageableCompany[]>([]);

  useEffect(() => {
    if (!allowed) return;
    let live = true;
    companySetupApi.context()
      .then(context => { if (live) setCompanies((context.manageableCompanies ?? []).filter(c => c.needsLegalDetails)); })
      .catch(() => undefined);
    return () => { live = false; };
  }, [allowed]);

  if (!allowed || companies.length === 0) return null;
  return (
    <section data-testid="company-setup-prompt" aria-label={t('Finish setting up your company')}
      className="rounded-2xl border border-amber-300 bg-amber-50 px-4 py-3 text-sm text-amber-950 dark:border-amber-400/30 dark:bg-amber-500/10 dark:text-amber-100">
      <ul className="flex flex-col gap-2">
        {companies.map(company => (
          <li key={company.id} className="flex flex-wrap items-center gap-x-3 gap-y-1">
            <Building2 className="h-4 w-4 shrink-0" aria-hidden="true" />
            <span className="min-w-0 flex-1">
              <strong>{t('Finish setting up {company}.', { company: company.legalNameEn })}</strong>{' '}
              {company.countryCode?.toUpperCase() === 'SA'
                ? t('Add its Arabic legal name, CR number and MOL establishment ID before the first payroll.')
                : t('Add its Arabic legal name and CR number before the first payroll.')}
            </span>
            <Link href={`/setup/company/${company.id}/complete`}
              className="font-semibold text-sapphire underline-offset-2 hover:underline dark:text-cyanAccent">
              {t('Complete company setup')}
            </Link>
          </li>
        ))}
      </ul>
    </section>
  );
}
