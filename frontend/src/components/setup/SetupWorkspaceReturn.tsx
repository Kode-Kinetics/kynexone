'use client';

import Link from 'next/link';
import { usePathname, useSearchParams } from 'next/navigation';
import { SETUP_AREAS } from '../../lib/setupAreas';
import { useCompany } from '../../contexts/CompanyContext';
import { useT } from '../../hooks/useT';

/** Only known destinations can offer a return; never consume an arbitrary URL. */
export function SetupWorkspaceReturn() {
  const params = useSearchParams();
  const pathname = usePathname();
  const { selectedCompanyId, companies } = useCompany();
  const t = useT();
  const area = SETUP_AREAS.find(item => item.id === params?.get('setupArea'));
  if (pathname === '/setup' || !area?.destinations.some(item => item.path.split('?')[0] === pathname)) return null;
  const target = new URLSearchParams({ tab: 'aiSetup', area: area.id });
  if (selectedCompanyId && companies.some(company => company.id === selectedCompanyId)) target.set('companyId', selectedCompanyId);
  return <Link href={`/setup?${target.toString()}`} className="mb-4 inline-flex rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-sapphire dark:border-white/10 dark:text-cyanAccent">{t('Back to Setup Studio')}</Link>;
}
