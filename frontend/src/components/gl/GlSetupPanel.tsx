'use client';

import { useMemo, useState } from 'react';
import { BookOpen, Building2, Coins, Landmark, ShieldCheck } from 'lucide-react';
import { useAuth } from '../../contexts/AuthContext';
import { useCompany } from '../../contexts/CompanyContext';
import type { CompanyDto, CostCenterDto } from '../../api/organization';
import { AccountsMappingsPanel } from './AccountsMappingsPanel';
import { DriverManagerPanel } from './DriverManagerPanel';
import { CompanyRatesPanel } from './CompanyRatesPanel';
import { StatutoryRatesPanel } from './StatutoryRatesPanel';

type SubTab = 'coa' | 'drivers' | 'companyRates' | 'statutory';

interface Props {
  companies: CompanyDto[];
  costCenters: CostCenterDto[];
}

export function GlSetupPanel({ companies, costCenters }: Props) {
  const { hasPermission } = useAuth();
  const { isGroupScope } = useCompany();

  // ── Permission map (server is authoritative; UI hides what the caller can't do) ──
  const glRead = hasPermission('finance.gl.read') || hasPermission('finance.gl.manage');
  const glManage = hasPermission('finance.gl.manage');
  const driversRead = glRead || hasPermission('finance.gl.drivers.manage');
  const driversManage = hasPermission('finance.gl.drivers.manage');
  const driversAuthor = hasPermission('finance.gl.drivers.author_predicates');
  const ratesRead = hasPermission('payroll.rates.read') || hasPermission('payroll.rates.manage');
  const ratesManage = hasPermission('payroll.rates.manage');
  const statutoryOverride = hasPermission('payroll.rates.statutory_override');
  const statutoryRead = ratesRead || statutoryOverride;
  const canApprove = hasPermission('approvals.decide');

  // ── Scope selector: group default (group-scoped callers only) or a company ──
  const [scope, setScope] = useState<string | null>(isGroupScope ? null : companies[0]?.id ?? null);
  const selectedCompany = useMemo(() => companies.find((c) => c.id === scope) ?? null, [companies, scope]);
  const scopeLabel = scope === null ? 'the group (defaults)' : selectedCompany?.legalNameEn ?? 'this company';

  const tabs = useMemo(
    () =>
      (
        [
          { id: 'coa' as const, label: 'Accounts & Mapping', icon: BookOpen, show: glRead },
          { id: 'drivers' as const, label: 'Posting Drivers', icon: Landmark, show: driversRead },
          { id: 'companyRates' as const, label: 'Company Rates', icon: Coins, show: ratesRead },
          { id: 'statutory' as const, label: 'Statutory Rates', icon: ShieldCheck, show: statutoryRead },
        ] as const
      ).filter((t) => t.show),
    [glRead, driversRead, ratesRead, statutoryRead],
  );

  const [active, setActive] = useState<SubTab>(() => tabs[0]?.id ?? 'coa');
  const activeTab = tabs.some((t) => t.id === active) ? active : tabs[0]?.id;

  if (tabs.length === 0) {
    return (
      <div className="surface p-6 text-center text-sm text-slate-400">
        You do not have permission to view GL configuration.
      </div>
    );
  }

  if (!isGroupScope && companies.length === 0) {
    return (
      <div className="surface p-6 text-center text-sm text-slate-400">
        No companies are available in your scope.
      </div>
    );
  }

  return (
    <div className="space-y-4">
      {/* Scope selector */}
      <div className="surface flex flex-wrap items-center justify-between gap-3 p-3">
        <div className="flex items-center gap-2.5">
          <div className="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-sapphire/10 text-sapphire dark:bg-sapphire/15 dark:text-cyanAccent">
            <Building2 className="h-[18px] w-[18px]" />
          </div>
          <div>
            <label htmlFor="gl-scope" className="block text-[11px] font-semibold uppercase tracking-wide text-slate-400">
              Configuration scope
            </label>
            <select
              id="gl-scope"
              value={scope ?? ''}
              onChange={(e) => setScope(e.target.value || null)}
              className="select mt-0.5 h-8 min-w-[220px] text-sm font-medium"
            >
              {isGroupScope && <option value="">All group (defaults)</option>}
              {companies.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.legalNameEn}
                  {c.countryCode ? ` · ${c.countryCode}` : ''}
                </option>
              ))}
            </select>
          </div>
        </div>
        <p className="max-w-md text-[11px] leading-snug text-slate-400 dark:text-slate-500">
          {scope === null
            ? 'Editing group-wide defaults. Every company inherits these unless it sets its own override.'
            : 'Editing this company. It inherits the group defaults; anything you set here overrides them for this legal entity only.'}
        </p>
      </div>

      {/* Sub-tab nav */}
      <div className="flex flex-wrap gap-1.5">
        {tabs.map(({ id, label, icon: Icon }) => (
          <button
            key={id}
            type="button"
            onClick={() => setActive(id)}
            className={`flex items-center gap-1.5 rounded-lg px-3 py-1.5 text-xs font-medium transition-all ${
              activeTab === id
                ? 'bg-sapphire text-white shadow-sm'
                : 'bg-slate-100 text-slate-600 hover:bg-slate-200 dark:bg-white/[0.06] dark:text-slate-400 dark:hover:bg-white/[0.10] dark:hover:text-slate-200'
            }`}
          >
            <Icon className="h-3.5 w-3.5 shrink-0" />
            {label}
          </button>
        ))}
      </div>

      {/* Panels */}
      {activeTab === 'coa' && (
        <AccountsMappingsPanel scope={scope} scopeLabel={scopeLabel} isGroupScope={isGroupScope} canManage={glManage} costCenters={costCenters} />
      )}
      {activeTab === 'drivers' && (
        <DriverManagerPanel scope={scope} scopeLabel={scopeLabel} canManage={driversManage} canAuthorPredicates={driversAuthor} />
      )}
      {activeTab === 'companyRates' && <CompanyRatesPanel scope={scope} scopeLabel={scopeLabel} canManage={ratesManage} />}
      {activeTab === 'statutory' && (
        <StatutoryRatesPanel
          scope={scope}
          scopeLabel={scopeLabel}
          countryCode={selectedCompany?.countryCode ?? ''}
          jurisdiction={selectedCompany?.jurisdiction ?? ''}
          canOverride={statutoryOverride}
          canApprove={canApprove}
        />
      )}
    </div>
  );
}
