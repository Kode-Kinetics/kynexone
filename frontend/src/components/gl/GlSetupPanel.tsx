'use client';

import { useCallback, useEffect, useMemo, useState, type KeyboardEvent } from 'react';
import { AlertTriangle, BookOpen, Building2, CalendarCheck, CheckCircle2, Coins, HandCoins, KeyRound, Landmark, ShieldCheck } from 'lucide-react';
import { useAuth } from '../../contexts/AuthContext';
import { useCompany } from '../../contexts/CompanyContext';
import { useLocale } from '../../contexts/LocaleContext';
import { useT } from '../../hooks/useT';
import { financeGlApi, type GlControlAccountHealth, type GlResolveResponse } from '../../api/financeGl';
import type { CompanyDto, CostCenterDto } from '../../api/organization';
import { AccountsMappingsPanel } from './AccountsMappingsPanel';
import { DriverManagerPanel } from './DriverManagerPanel';
import { CompanyRatesPanel } from './CompanyRatesPanel';
import { StatutoryRatesPanel, type StatutoryCountry } from './StatutoryRatesPanel';
import { GlPeriodsPanel } from './GlPeriodsPanel';
import { ErpApiKeysPanel } from './ErpApiKeysPanel';
import { EosbProvisionPanel } from './EosbProvisionPanel';
import { problemLabel, problemRank, routeRowId, useScopedResource } from './glUi';
import type { SetupNavigationGuard } from '../../hooks/useSetupNavigationGuard';

type SubTab = 'coa' | 'drivers' | 'companyRates' | 'statutory' | 'periods' | 'eosb' | 'erpApi';

interface Props {
  companies: CompanyDto[];
  costCenters: CostCenterDto[];
  confirmLeave?: SetupNavigationGuard;
  /**
   * The tab a link asked for (Setup's `?glTab=`, e.g. from "Find a setting"). Opened when this reader may see it;
   * otherwise the first tab they may see, as without a request.
   */
  requestedTab?: string | null;
}

export function GlSetupPanel({ companies, costCenters, confirmLeave, requestedTab = null }: Props) {
  const t = useT();
  const { locale, dir } = useLocale();
  const { hasPermission, hasRole } = useAuth();
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
  // Activating a statutory override needs the same key as raising one (the API enforces it; approvals.decide is not enough).
  // Per request the server also says whether THIS viewer may approve (never the requester).
  const canApprove = statutoryOverride;
  // ERP API keys: Admin only (the server also requires two-step sign-in and says so with a 403 the panel explains).
  const erpApiManage = hasRole('Admin');

  // ── Scope selector: group default (group-scoped callers only) or a company ──
  const [scope, setScope] = useState<string | null>(isGroupScope ? null : companies[0]?.id ?? null);
  // Companies arrive after mount: a company-scoped user must land on a company they can edit, never
  // on the group scope they cannot write; a company that disappears from the list is not kept.
  useEffect(() => {
    if (scope !== null && companies.length > 0 && !companies.some((c) => c.id === scope)) setScope(isGroupScope ? null : companies[0].id);
    else if (scope === null && !isGroupScope && companies.length > 0) setScope(companies[0].id);
  }, [companies, isGroupScope, scope]);
  const selectedCompany = useMemo(() => companies.find((c) => c.id === scope) ?? null, [companies, scope]);
  const companyName = (c: CompanyDto) => (locale === 'ar' && c.legalNameAr ? c.legalNameAr : c.legalNameEn);
  const scopeName = scope === null ? t('Group defaults') : selectedCompany ? companyName(selectedCompany) : t('This company');

  // Countries the group operates in, for the group-level statutory view.
  const countries = useMemo<StatutoryCountry[]>(() => {
    const seen = new Map<string, StatutoryCountry>();
    for (const c of companies) {
      if (c.countryCode && !seen.has(c.countryCode)) seen.set(c.countryCode, { code: c.countryCode, jurisdiction: c.jurisdiction ?? '' });
    }
    return [...seen.values()];
  }, [companies]);

  const tabs = useMemo(
    () =>
      (
        [
          { id: 'coa' as const, label: t('Accounts & Mapping'), icon: BookOpen, show: glRead },
          { id: 'drivers' as const, label: t('Posting Drivers'), icon: Landmark, show: driversRead },
          { id: 'companyRates' as const, label: t('Company Rates'), icon: Coins, show: ratesRead },
          { id: 'statutory' as const, label: t('Statutory Rates'), icon: ShieldCheck, show: statutoryRead },
          { id: 'periods' as const, label: t('Accounting periods'), icon: CalendarCheck, show: glRead },
          { id: 'eosb' as const, label: t('End-of-service provision'), icon: HandCoins, show: glRead },
          { id: 'erpApi' as const, label: t('ERP API access'), icon: KeyRound, show: erpApiManage },
        ] as const
      ).filter((x) => x.show),
    [t, glRead, driversRead, ratesRead, statutoryRead, erpApiManage],
  );

  const permittedRequest = (tabs as readonly { id: SubTab }[]).find((x) => x.id === requestedTab)?.id;
  const [active, setActive] = useState<SubTab>(() => permittedRequest ?? tabs[0]?.id ?? 'coa');
  // A new request while the panel is open (a second search result) moves to it. The link already passed Setup's
  // unsaved-changes guard, so it is not asked again here.
  useEffect(() => {
    if (permittedRequest) setActive(permittedRequest);
  }, [permittedRequest]);
  const activeTab = tabs.some((x) => x.id === active) ? active : tabs[0]?.id;
  const selectTab = (tab: SubTab, afterSelect?: () => void) => {
    if (tab === activeTab) { afterSelect?.(); return true; }
    const select = () => { setActive(tab); afterSelect?.(); };
    if (confirmLeave) return confirmLeave(`setup-gl-tab:${tab}`, select);
    select();
    return true;
  };
  const selectScope = (next: string | null) => {
    if (next === scope) return;
    if (confirmLeave) confirmLeave(`setup-gl-scope:${next ?? 'group'}`, () => setScope(next));
    else setScope(next);
  };

  // ── Effective routing + control-account health, shared by the banner, the
  //    "Where does it post?" view and the drivers table ──
  const scopeKey = scope ?? 'group';
  const routingEnabled = glRead && (isGroupScope || scope !== null);
  const resolve = useScopedResource<GlResolveResponse>(`resolve:${scopeKey}`, () => financeGlApi.resolve(scope), routingEnabled);
  const health = useScopedResource<GlControlAccountHealth>(`health:${scopeKey}`, () => financeGlApi.controlAccountHealth(scope), routingEnabled);
  const reloadResolve = resolve.reload;
  const reloadHealth = health.reload;
  const refreshRouting = useCallback(() => {
    void reloadResolve();
    void reloadHealth();
  }, [reloadResolve, reloadHealth]);

  // Banner links: switch to the mapping tab, then scroll to and focus the driver's row.
  const [focusDriver, setFocusDriver] = useState<string | null>(null);
  useEffect(() => {
    if (!focusDriver || activeTab !== 'coa') return;
    // A control account the routing view does not list (or a view still loading) falls back to the
    // view's heading, so a click always lands somewhere and never fires again on a later reload.
    const el = document.getElementById(routeRowId(focusDriver)) ?? document.getElementById('gl-where-heading');
    setFocusDriver(null);
    if (!el) return;
    el.scrollIntoView({ block: 'center', behavior: 'smooth' });
    if (el.id !== 'gl-where-heading') el.focus({ preventScroll: true });
  }, [focusDriver, activeTab, resolve.data]);

  const goToDriver = (driverKey: string) => {
    selectTab('coa', () => setFocusDriver(driverKey));
  };

  const onTabKey = (e: KeyboardEvent<HTMLButtonElement>, index: number) => {
    const forward = dir === 'rtl' ? 'ArrowLeft' : 'ArrowRight';
    const back = dir === 'rtl' ? 'ArrowRight' : 'ArrowLeft';
    let next = -1;
    if (e.key === forward) next = (index + 1) % tabs.length;
    else if (e.key === back) next = (index - 1 + tabs.length) % tabs.length;
    else if (e.key === 'Home') next = 0;
    else if (e.key === 'End') next = tabs.length - 1;
    if (next < 0) return;
    e.preventDefault();
    selectTab(tabs[next].id, () => requestAnimationFrame(() => document.getElementById(`gl-tab-${tabs[next].id}`)?.focus()));
  };

  if (tabs.length === 0) {
    return <div className="surface p-6 text-center text-sm text-slate-500">{t('You do not have permission to view GL configuration.')}</div>;
  }

  if (!isGroupScope && companies.length === 0) {
    return <div className="surface p-6 text-center text-sm text-slate-500">{t('No companies are available in your scope.')}</div>;
  }

  // Exception-first: every routing problem and every unhealthy control account, worst first.
  // An older server (or a proxy error page) may not send these shapes: show nothing rather than "all clear".
  const resolveValid = Array.isArray(resolve.data?.rows);
  const healthValid = Array.isArray(health.data?.accounts);
  // "All clear" is only claimed for something that was actually checked: no drivers is not healthy routing.
  const routingChecked = resolveValid && resolve.data!.rows.length > 0;
  const routeProblems = (resolveValid ? resolve.data!.rows : [])
    .filter((r) => !!r.problem)
    .sort((a, b) => problemRank(a.problem) - problemRank(b.problem) || a.driverKey.localeCompare(b.driverKey));
  const controlViolations = (healthValid ? health.data!.accounts : []).filter((a) => !a.isHealthy);
  const issueCount = routeProblems.length + controlViolations.length;

  return (
    <div className="space-y-4">
      {/* Scope selector */}
      <div className="surface flex flex-wrap items-center justify-between gap-3 p-3">
        <div className="flex items-center gap-2.5">
          <div className="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-sapphire/10 text-sapphire dark:bg-sapphire/15 dark:text-cyanAccent">
            <Building2 className="h-[18px] w-[18px]" aria-hidden />
          </div>
          <div>
            <label htmlFor="gl-scope" className="block text-[11px] font-semibold uppercase tracking-wide text-slate-500">
              {t('Configuration scope')}
            </label>
            <select
              id="gl-scope"
              value={scope ?? ''}
              onChange={(e) => selectScope(e.target.value || null)}
              data-setup-filter
              className="select mt-0.5 h-8 min-w-[220px] text-sm font-medium"
            >
              {isGroupScope && <option value="">{t('All companies (group defaults)')}</option>}
              {companies.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.countryCode ? t('{company} · {country}', { company: companyName(c), country: c.countryCode }) : companyName(c)}
                </option>
              ))}
            </select>
          </div>
        </div>
        <p className="max-w-md text-[11px] leading-snug text-slate-500 dark:text-slate-400">
          {scope === null
            ? t('Editing group-wide defaults. Every company inherits these unless it sets its own override.')
            : t('Editing this company. It inherits the group defaults; anything you set here overrides them for this legal entity only.')}
        </p>
      </div>

      {/* Health banner: routing problems + control-account health, each linking to its row */}
      {glRead && (routingChecked || healthValid) && (
        <section
          aria-label={t('Posting health')}
          className={`rounded-xl border px-4 py-3 text-sm ${
            issueCount > 0
              ? 'border-amber-300 bg-amber-50 text-amber-900 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-200'
              : 'border-emerald-200 bg-emerald-50 text-emerald-800 dark:border-emerald-500/30 dark:bg-emerald-500/10 dark:text-emerald-200'
          }`}
        >
          {issueCount > 0 ? (
            <>
              <p className="flex items-center gap-1.5 font-semibold">
                <AlertTriangle className="h-4 w-4 shrink-0" aria-hidden />
                {t('{count, plural, one {# posting problem needs attention} other {# posting problems need attention}}', { count: issueCount })}
              </p>
              <ul className="mt-2 space-y-1 text-xs">
                {routeProblems.map((r) => (
                  <li key={`route-${r.driverKey}`}>
                    <button type="button" onClick={() => goToDriver(r.driverKey)} className="text-start underline decoration-dotted underline-offset-2 hover:decoration-solid">
                      {t('{driver}: {problem}', { driver: r.label || r.driverKey, problem: problemLabel(t, r.problem) })}
                    </button>
                  </li>
                ))}
                {controlViolations.map((a) => (
                  <li key={`ctl-${a.driver}`}>
                    <button type="button" onClick={() => goToDriver(a.driver)} className="text-start underline decoration-dotted underline-offset-2 hover:decoration-solid">
                      {t('Control account {account}: {violation}', { account: a.account, violation: a.violation ?? '' })}
                    </button>
                  </li>
                ))}
              </ul>
            </>
          ) : (
            <p className="flex items-center gap-1.5">
              <CheckCircle2 className="h-4 w-4 shrink-0" aria-hidden />
              {routingChecked && healthValid
                ? t('Every posting driver resolves to an active account in the chart, and control-account balances have the right sign.')
                : routingChecked
                  ? t('Every posting driver resolves to an active account in the chart.')
                  : t('Control-account balances have the right sign.')}
            </p>
          )}
          {health.error && !healthValid && (
            <p className="mt-1 text-[11px] opacity-80">
              {t('Control-account health could not be checked.')}{' '}
              <button type="button" onClick={() => void health.reload()} className="font-medium underline">
                {t('Retry')}
              </button>
            </p>
          )}
        </section>
      )}

      {/* Sub-tab nav */}
      <div role="tablist" aria-label={t('Payroll accounting sections')} className="flex flex-wrap gap-1.5">
        {tabs.map(({ id, label, icon: Icon }, index) => (
          <button
            key={id}
            id={`gl-tab-${id}`}
            type="button"
            role="tab"
            aria-selected={activeTab === id}
            aria-controls={`gl-panel-${id}`}
            tabIndex={activeTab === id ? 0 : -1}
            onClick={() => selectTab(id)}
            onKeyDown={(e) => onTabKey(e, index)}
            className={`flex items-center gap-1.5 rounded-lg px-3 py-1.5 text-xs font-medium transition-shadow ${
              activeTab === id
                ? 'bg-sapphire text-white shadow-sm'
                : 'bg-slate-100 text-slate-600 hover:bg-slate-200 dark:bg-white/[0.06] dark:text-slate-300 dark:hover:bg-white/[0.10] dark:hover:text-slate-200'
            }`}
          >
            <Icon className="h-3.5 w-3.5 shrink-0" aria-hidden />
            {label}
          </button>
        ))}
      </div>

      {/* Panels */}
      <div key={scopeKey} role="tabpanel" id={`gl-panel-${activeTab}`} aria-labelledby={`gl-tab-${activeTab}`}>
        {activeTab === 'coa' && (
          <AccountsMappingsPanel
            scope={scope}
            scopeName={scopeName}
            canManage={glManage}
            costCenters={costCenters}
            resolve={resolve}
            onRoutingChanged={refreshRouting}
          />
        )}
        {activeTab === 'drivers' && (
          <DriverManagerPanel
            scope={scope}
            scopeName={scopeName}
            canManage={driversManage}
            canAuthorPredicates={driversAuthor}
            resolveRows={resolveValid ? resolve.data!.rows : null}
            onRoutingChanged={refreshRouting}
          />
        )}
        {activeTab === 'companyRates' && (
          <CompanyRatesPanel scope={scope} scopeName={scopeName} canManage={ratesManage} currency={selectedCompany?.defaultCurrency || null} />
        )}
        {activeTab === 'statutory' && (
          <StatutoryRatesPanel
            scope={scope}
            scopeName={scopeName}
            countryCode={selectedCompany?.countryCode ?? ''}
            jurisdiction={selectedCompany?.jurisdiction ?? ''}
            countries={countries}
            canOverride={statutoryOverride}
            canApprove={canApprove}
          />
        )}
        {activeTab === 'periods' && <GlPeriodsPanel companyId={scope} />}
        {activeTab === 'eosb' && (
          <EosbProvisionPanel
            companyId={scope}
            companyName={scopeName}
            currency={selectedCompany?.defaultCurrency || null}
            canManage={glManage}
            canManageRates={ratesManage}
          />
        )}
        {activeTab === 'erpApi' && <ErpApiKeysPanel companies={companies} />}
      </div>
    </div>
  );
}
