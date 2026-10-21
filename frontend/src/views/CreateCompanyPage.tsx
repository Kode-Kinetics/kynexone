'use client';

import Link from 'next/link';
import { ArrowLeft } from 'lucide-react';
import { useT } from '../hooks/useT';
import { CreateCompanyWizard } from '../components/companySetup/CreateCompanyWizard';

/** Full-page create-company journey, reached from Setup → Companies and Group Overview. */
export function CreateCompanyPage() {
  const t = useT();
  return (
    <div className="space-y-4">
      <header className="min-w-0">
        <Link href="/setup?tab=companies" className="inline-flex items-center gap-1 text-sm font-medium text-sapphire hover:underline dark:text-cyanAccent">
          <ArrowLeft className="h-4 w-4 rtl:rotate-180" aria-hidden="true" />{t('Companies')}
        </Link>
        <h1 className="mt-1 text-2xl font-bold tracking-tight text-slate-950 dark:text-white">{t('Create company')}</h1>
      </header>
      <CreateCompanyWizard />
    </div>
  );
}
