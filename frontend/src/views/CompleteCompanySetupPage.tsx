'use client';

import Link from 'next/link';
import { ArrowLeft } from 'lucide-react';
import { useT } from '../hooks/useT';
import { CreateCompanyWizard } from '../components/companySetup/CreateCompanyWizard';
import { GUIDED_SETUP_HREF } from '../lib/companySetupWizard';

/** Full-page "complete setup for an existing company" journey, reached from Setup → Guided setup. */
export function CompleteCompanySetupPage({ companyId }: { companyId: string }) {
  const t = useT();
  return (
    <div className="space-y-4">
      <header className="min-w-0">
        <Link href={GUIDED_SETUP_HREF} className="inline-flex items-center gap-1 text-sm font-medium text-sapphire hover:underline dark:text-cyanAccent">
          <ArrowLeft className="h-4 w-4 rtl:rotate-180" aria-hidden="true" />{t('Guided setup')}
        </Link>
        <h1 className="mt-1 text-2xl font-bold tracking-tight text-slate-950 dark:text-white">{t('Complete company setup')}</h1>
      </header>
      {/* Keyed by company: a different company is a different draft and a fresh journey. */}
      <CreateCompanyWizard key={companyId} existingCompanyId={companyId} />
    </div>
  );
}
