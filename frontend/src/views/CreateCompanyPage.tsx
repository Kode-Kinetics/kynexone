'use client';

import Link from 'next/link';
import { ArrowLeft } from 'lucide-react';
import { useT } from '../hooks/useT';
import { CreateCompanyWizard } from '../components/companySetup/CreateCompanyWizard';
import { GUIDED_SETUP_HREF } from '../lib/companySetupWizard';

/** Full-page create-company journey, reached from Setup → Guided setup (Setup's default), Companies and Group Overview.
 *  The way back is Guided setup, the hub that lists drafts and companies, as on the complete-setup page. */
export function CreateCompanyPage() {
  const t = useT();
  return (
    <div className="space-y-4">
      <header className="min-w-0">
        <Link href={GUIDED_SETUP_HREF} className="inline-flex items-center gap-1 text-sm font-medium text-sapphire hover:underline dark:text-cyanAccent">
          <ArrowLeft className="h-4 w-4" aria-hidden="true" />{t('Guided setup')}
        </Link>
        <h1 className="mt-1 text-2xl font-semibold tracking-tight text-slate-950 dark:text-white">{t('Create company')}</h1>
      </header>
      <CreateCompanyWizard />
    </div>
  );
}
