'use client';

import { useEffect, useRef, useState } from 'react';
import { Building2 } from 'lucide-react';
import { useCompany } from '../contexts/CompanyContext';
import { useLocale } from '../contexts/LocaleContext';

/**
 * TopBar company switcher (mirrors the LanguageSwitcher interaction pattern).
 * Renders nothing for single-company users. "All companies" is offered ONLY to
 * explicit group-scope users. This is a view preference — the backend narrows the
 * request scope from the X-Company-Id header and fails closed on tampering.
 */
export function CompanySwitcher() {
  const { t } = useLocale();
  const { companies, selectedCompanyId, setSelectedCompany, isGroupScope, showSwitcher } = useCompany();
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    if (!open) return;
    const handler = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, [open]);

  if (!showSwitcher) return null;

  const current = companies.find((c) => c.id === selectedCompanyId);
  const label = current ? current.code : t('All companies');

  return (
    <div ref={ref} className="relative" data-testid="company-switcher">
      <button
        ref={triggerRef}
        type="button"
        onClick={() => setOpen((o) => !o)}
        aria-label={t('Switch company')}
        aria-expanded={open}
        className="flex h-control-md max-w-[220px] items-center gap-1.5 rounded-lg border border-line bg-surface px-2.5 text-xs font-semibold text-slate-600 transition hover:border-line-strong hover:bg-surface-sunken dark:text-slate-300"
      >
        <Building2 className="h-3.5 w-3.5 shrink-0 text-slate-400 dark:text-slate-500" />
        <span className="truncate">{label}</span>
      </button>

      {open && (
        <div role="menu" aria-label={t('Select company')} className="absolute start-0 top-full z-50 mt-2 max-h-80 w-64 overflow-y-auto rounded-xl border border-line bg-surface-overlay shadow-e2">
          {isGroupScope && (
            <button
              type="button"
              role="menuitem"
              onClick={() => { setSelectedCompany(null, triggerRef.current); setOpen(false); }}
              className={`flex w-full items-center justify-between px-4 py-2.5 text-sm transition hover:bg-slate-50 dark:hover:bg-white/[0.04] ${selectedCompanyId === null ? 'bg-sapphire/[0.06] font-semibold text-sapphire dark:text-cyanAccent' : 'text-slate-700 dark:text-slate-300'}`}
            >
              <span>{t('All companies')}</span>
              {selectedCompanyId === null && <span className="h-1.5 w-1.5 rounded-full bg-sapphire dark:bg-cyanAccent" />}
            </button>
          )}
          {companies.map((company) => (
            <button
              key={company.id}
              type="button"
              role="menuitem"
              onClick={() => { setSelectedCompany(company.id, triggerRef.current); setOpen(false); }}
              className={`flex w-full items-center justify-between px-4 py-2.5 text-sm transition hover:bg-slate-50 dark:hover:bg-white/[0.04] ${selectedCompanyId === company.id ? 'bg-sapphire/[0.06] font-semibold text-sapphire dark:text-cyanAccent' : 'text-slate-700 dark:text-slate-300'}`}
            >
              <span className="min-w-0 flex-1 truncate text-start">
                {company.name}
                <span className="ms-1.5 text-xs text-slate-400 dark:text-slate-500">{company.code} · {company.countryCode}</span>
              </span>
              {selectedCompanyId === company.id && <span className="ms-2 h-1.5 w-1.5 shrink-0 rounded-full bg-sapphire dark:bg-cyanAccent" />}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
