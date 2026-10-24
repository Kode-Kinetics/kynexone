'use client';

import { useEffect, useState } from 'react';
import Link from 'next/link';
import { usePathname, useSearchParams } from 'next/navigation';
import { ArrowLeft, X } from 'lucide-react';
import { SETUP_AREAS } from '../../lib/setupAreas';
import { useCompany } from '../../contexts/CompanyContext';
import { useT } from '../../hooks/useT';
import {
  companySetupReturnHref, forgetCompanySetupReturn, isCompanySetupPath, parseCompanySetupReturn, readCompanySetupReturn, rememberCompanySetupReturn,
  type CompanySetupReturn,
} from './companySetupReturn';
import styles from '../companySetup/rules/motion.module.css';

/**
 * The way back to Setup from a page it sent you to. Two allow-listed sources, never an arbitrary URL:
 *  - `?setupArea=` — All setup areas (the destination must be one of that area's);
 *  - `?returnTo=companySetup&draft=&company=&part=` — the company setup wizard (companySetupReturn.ts), with a
 *    per-tab fallback for pages that drop the query as they navigate. Cleared when the wizard loads or on dismiss.
 */
export function SetupWorkspaceReturn() {
  const params = useSearchParams();
  const pathname = usePathname();
  const { selectedCompanyId, companies } = useCompany();
  const t = useT();
  const fromUrl = parseCompanySetupReturn(params);
  const [companySetup, setCompanySetup] = useState<CompanySetupReturn | null>(fromUrl);
  const [dismissed, setDismissed] = useState(false);
  const urlKey = fromUrl ? companySetupReturnHref(fromUrl) : '';

  useEffect(() => {
    if (isCompanySetupPath(pathname)) { forgetCompanySetupReturn(); setCompanySetup(null); return; }
    const value = parseCompanySetupReturn(params) ?? readCompanySetupReturn();
    if (value) rememberCompanySetupReturn(value);
    setCompanySetup(value);
    // Re-read when the page or the return in its URL changes.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [pathname, urlKey]);

  if (companySetup && !dismissed && !isCompanySetupPath(pathname)) {
    return (
      <div className="sticky top-[68px] z-10 mb-4 flex" data-testid="company-setup-return">
        <div className={`${styles.barIn} inline-flex max-w-full items-center gap-0.5 rounded-xl border border-slate-200/90 bg-white/85 p-1 shadow-soft backdrop-blur-md dark:border-white/10 dark:bg-[#0d1225]/85`}>
          <Link href={companySetupReturnHref(companySetup)} data-testid="company-setup-return-link"
            className="inline-flex h-8 min-w-0 items-center gap-1.5 rounded-lg px-2.5 text-sm font-semibold text-sapphire transition-colors duration-150 hover:bg-sapphire/[0.06] active:bg-sapphire/10 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-sapphire dark:text-cyanAccent dark:hover:bg-cyanAccent/10">
            <ArrowLeft className="h-4 w-4 shrink-0" aria-hidden="true" />
            <span className="truncate">{t('Back to company setup')}</span>
          </Link>
          <button type="button" aria-label={t('Hide the way back to company setup')} title={t('Hide')} data-testid="company-setup-return-dismiss"
            onClick={() => { forgetCompanySetupReturn(); setDismissed(true); }}
            className="grid h-8 w-8 shrink-0 place-items-center rounded-lg text-slate-500 transition-colors duration-150 hover:bg-slate-100 hover:text-slate-800 active:bg-slate-200 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-sapphire dark:text-slate-400 dark:hover:bg-white/10 dark:hover:text-white">
            <X className="h-4 w-4" aria-hidden="true" />
          </button>
        </div>
      </div>
    );
  }

  const area = SETUP_AREAS.find(item => item.id === params?.get('setupArea'));
  if (pathname === '/setup' || !area?.destinations.some(item => item.path.split('?')[0] === pathname)) return null;
  const target = new URLSearchParams({ tab: 'aiSetup', path: 'configuration', area: area.id });
  if (selectedCompanyId && companies.some(company => company.id === selectedCompanyId)) target.set('companyId', selectedCompanyId);
  return <Link href={`/setup?${target.toString()}`} className="mb-4 inline-flex rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-sapphire dark:border-white/10 dark:text-cyanAccent">{t('Back to All setup areas')}</Link>;
}
