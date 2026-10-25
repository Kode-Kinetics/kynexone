'use client';

import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import Link from 'next/link';
import { useSearchParams } from 'next/navigation';
import {
  tenantAdminApi,
  tenantModulesApi,
  type TenantModuleList,
  type TenantLocalizationSetting,
  type TenantBranding,
  type TenantSubscription,
  type TenantInvoiceSummary,
  type TenantAiUsageSummary,
} from '../api/intelligence';
import client from '../api/client';
import { Toggle } from '../components/ui/Toggle';
import { useTenantSettingsContext } from '../contexts/TenantSettingsContext';
import { useFeatureFlags } from '../contexts/FeatureFlagContext';
import { useLocale } from '../contexts/LocaleContext';
import { requestFailureReason } from '../lib/requestFailure';

/**
 * The workspace settings that used to be the tabs of Tenant Administration, each now a self-loading panel shown as its
 * own Setup section (components/setup/ModuleSetupSection.tsx): Plan and billing, Modules, Language and region, Branding,
 * and Country payroll keys. Field help text is components/HelpTextManager.tsx. Sign-in security is the one editor in
 * views/UserManagementPage.tsx; the old "Statutory rules" alias is Setup → Statutory rules.
 *
 * The module list is not hard-coded: it comes from the backend catalog, the same source the API guard, the navigation and
 * the route guard enforce.
 */

const GCC_COUNTRIES = [
  { code: 'AE', name: 'United Arab Emirates' },
  { code: 'SA', name: 'Saudi Arabia' },
  { code: 'QA', name: 'Qatar' },
  { code: 'KW', name: 'Kuwait' },
  { code: 'BH', name: 'Bahrain' },
  { code: 'OM', name: 'Oman' },
  { code: 'JO', name: 'Jordan' },
  { code: 'EG', name: 'Egypt' },
  { code: 'PK', name: 'Pakistan' },
  { code: 'IN', name: 'India' },
];

interface UsageData {
  activeEmployees: number;
  maxEmployees: number;
  activeUsers: number;
  maxUsers: number;
  storageUsedMb: number;
}

interface SubscriptionUsage {
  plan: string;
  status: string;
  billingCycle: string;
  monthlyAmount: number;
  currencyCode: string;
  expiresAtUtc?: string;
  limits: { maxEmployees: number; maxUsers: number; maxCompanies: number; maxAdminUsers: number };
  usage: { activeEmployees: number; totalUsers: number; totalCompanies: number; aiTokensThisMonth: number };
  featureFlags: Record<string, boolean>;
}

interface CountryRule {
  id: string;
  countryCode: string;
  ruleKey: string;
  ruleValue: string;
  dataType: string;
  isOverride: boolean;
  effectiveFrom: string | null;
  effectiveTo: string | null;
}

/** What a Setup panel passes down: unsaved edits make Setup's leave guard ask before the section is left. */
interface PanelProps {
  onDirtyChange?: (dirty: boolean) => void;
}

const httpStatus = (error: unknown) => (error as { response?: { status?: number } } | null)?.response?.status;

/** A load that failed says so, with its reason and a Retry, instead of showing the form or list empty. */
function LoadFailed({ message, error, onRetry }: { message: string; error: unknown; onRetry: () => void }) {
  const { t } = useLocale();
  return (
    <div role="alert" className="rounded-xl border border-rose-200 bg-rose-50 p-5 text-sm text-rose-700 dark:border-rose-900/60 dark:bg-rose-950/30 dark:text-rose-300">
      <p>{message}</p>
      <p className="mt-1 text-rose-600 dark:text-rose-400">{t(requestFailureReason(error))}</p>
      <button type="button" onClick={onRetry} className="btn-secondary mt-3 h-8 px-3 text-sm">{t('Retry')}</button>
    </div>
  );
}

type Loaded<T> = { status: 'loading' } | { status: 'error'; error: unknown } | { status: 'ready'; data: T };

/** Loads once on open; `reload` runs it again (Retry). */
function useLoaded<T>(load: () => Promise<T>) {
  const [state, setState] = useState<Loaded<T>>({ status: 'loading' });
  const loadRef = useRef(load);
  loadRef.current = load;
  const reload = useCallback(async () => {
    setState({ status: 'loading' });
    try { setState({ status: 'ready', data: await loadRef.current() }); }
    catch (error) { setState({ status: 'error', error }); }
  }, []);
  useEffect(() => { void reload(); }, [reload]);
  const setData = useCallback((data: T) => setState({ status: 'ready', data }), []);
  return { state, reload, setData };
}

// Reusable progress bar using a <meter> element — semantic, no inline styles needed.
function UsageProgressBar({ pct, label }: { pct: number; label: string }) {
  const clamped = Math.min(Math.max(pct, 0), 100);
  // Tailwind can't express arbitrary dynamic widths, so we use <meter> which renders
  // a native progress indicator and carries its own accessible semantics.
  const barColor = pct > 95 ? 'accent-red-500' : pct > 80 ? 'accent-orange-400' : 'accent-green-500';
  return (
    <meter
      className={`w-full h-2 rounded-full ${barColor}`}
      value={clamped}
      min={0}
      max={100}
      aria-label={`${label}: ${Math.round(clamped)}%`}
    />
  );
}

// Upgrade request button with toast feedback
function UpgradeButton() {
  const [sent, setSent] = useState(false);

  const handleClick = () => {
    setSent(true);
    setTimeout(() => setSent(false), 4000);
  };

  return (
    <div className="flex flex-col items-start gap-2">
      <button
        type="button"
        onClick={handleClick}
        disabled={sent}
        className="px-5 py-2.5 bg-indigo-600 text-white rounded-lg text-sm font-medium disabled:opacity-60 hover:bg-indigo-700 transition-colors"
      >
        {sent ? 'Request Sent!' : 'Request Upgrade'}
      </button>
      {sent && (
        <p className="text-xs text-green-600 font-medium">
          Upgrade request sent! Our team will contact you shortly.
        </p>
      )}
    </div>
  );
}

/** Plan and billing: the subscription, its limits and usage, Kody usage and the invoices, on one screen. */
export function PlanBillingPanel() {
  const { t } = useLocale();
  const searchParams = useSearchParams();
  // The 402 handler (api/client.ts) sends someone whose subscription is inactive here with ?alert=subscription.
  const subscriptionAlert = searchParams?.get('alert') === 'subscription';
  const [loading, setLoading] = useState(true);
  const [subscription, setSubscription] = useState<TenantSubscription | null>(null);
  const [usage, setUsage] = useState<UsageData | null>(null);
  const [subscriptionUsage, setSubscriptionUsage] = useState<SubscriptionUsage | null>(null);
  const [aiUsage, setAiUsage] = useState<TenantAiUsageSummary | null>(null);
  const [invoices, setInvoices] = useState<TenantInvoiceSummary[]>([]);
  const [failed, setFailed] = useState<{ subscription: unknown; usage: unknown; invoices: unknown }>({ subscription: null, usage: null, invoices: null });

  const load = useCallback(async () => {
    setLoading(true);
    const [sub, use, subUse, ai, inv] = await Promise.allSettled([
      tenantAdminApi.getSubscription(),
      client.get<UsageData>('/api/tenant-admin/usage').then(r => r.data),
      client.get<SubscriptionUsage>('/api/tenant-admin/subscription/usage').then(r => r.data),
      tenantAdminApi.getAiUsage(),
      tenantAdminApi.listInvoices(),
    ]);
    setSubscription(sub.status === 'fulfilled' ? sub.value : null);
    setUsage(use.status === 'fulfilled' ? use.value : null);
    setSubscriptionUsage(subUse.status === 'fulfilled' ? subUse.value : null);
    setAiUsage(ai.status === 'fulfilled' ? ai.value : null);
    setInvoices(inv.status === 'fulfilled' ? inv.value : []);
    // The usage figures are three calls; the first failure stands for them.
    const usageFailure = [use, subUse, ai].find(result => result.status === 'rejected');
    setFailed({
      subscription: sub.status === 'rejected' ? sub.reason ?? true : null,
      usage: usageFailure?.status === 'rejected' ? usageFailure.reason ?? true : null,
      invoices: inv.status === 'rejected' ? inv.reason ?? true : null,
    });
    setLoading(false);
  }, []);
  useEffect(() => { void load(); }, [load]);
  // One cause usually fails all three (an inactive subscription answers 402 to each): say it once.
  const everythingFailed = !loading && Boolean(failed.subscription) && failed.usage !== null && Boolean(failed.invoices);

  return (
    <div className="space-y-4">
      {subscriptionAlert && (
        <div role="alert" className="rounded-xl border border-amber-300 bg-amber-50 px-4 py-3 text-sm font-medium text-amber-800">
          {t('Your subscription is inactive or has expired, so some actions are refused. Contact your account manager or support to renew it.')}
        </div>
      )}

      {/* PastDue warning banner */}
      {subscription?.status === 'PastDue' && (
        <div className="flex items-start gap-3 rounded-xl border border-amber-300 bg-amber-50 px-4 py-3">
          <svg className="mt-0.5 h-5 w-5 shrink-0 text-amber-500" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01M10.29 3.86L1.82 18a2 2 0 001.71 3h16.94a2 2 0 001.71-3L13.71 3.86a2 2 0 00-3.42 0z" />
          </svg>
          <div>
            <p className="text-sm font-semibold text-amber-800">Payment overdue — please settle your account</p>
            <p className="text-xs text-amber-700 mt-0.5">
              Your subscription has an outstanding balance. All features remain active for now, but continued non-payment may result in suspension. Contact your account manager or check the invoices below.
            </p>
          </div>
        </div>
      )}

      {loading && <p role="status" className="text-sm text-gray-500">{t('Loading…')}</p>}

      {/* Subscription */}
      {!loading && (failed.subscription ? (
        <LoadFailed message={t('The subscription could not be loaded.')} error={failed.subscription} onRetry={() => void load()} />
      ) : (
        <div className="bg-white rounded-xl border border-gray-200 p-6">
          <h2 className="text-lg font-semibold text-gray-900 mb-4">Current Subscription</h2>
          {!subscription ? (
            <p className="text-sm text-gray-500">No subscription found. Contact your account manager or support team.</p>
          ) : (
            <dl className="grid grid-cols-2 gap-x-6 gap-y-4">
              {[
                { label: 'Plan', value: subscription.plan },
                { label: 'Monthly Amount', value: `${subscription.currencyCode} ${subscription.monthlyAmount}` },
                { label: 'Status', value: subscription.status },
                { label: 'Billing Email', value: subscription.billingEmail },
                { label: 'Max Employees', value: subscription.maxEmployees === 0 ? 'Unlimited' : subscription.maxEmployees },
                { label: 'Max Companies', value: (subscription as { maxCompanies?: number }).maxCompanies === 0 ? 'Unlimited' : ((subscription as { maxCompanies?: number }).maxCompanies ?? 1) },
                { label: 'Max Admin Users', value: (subscription as { maxAdminUsers?: number }).maxAdminUsers === 0 ? 'Unlimited' : ((subscription as { maxAdminUsers?: number }).maxAdminUsers ?? 10) },
                { label: 'Started', value: new Date(subscription.startedAtUtc).toLocaleDateString() },
                { label: 'Billing Cycle', value: subscription.billingCycle },
                { label: 'Expires', value: subscription.expiresAtUtc ? new Date(subscription.expiresAtUtc).toLocaleDateString() : 'Never' },
              ].map(item => (
                <div key={item.label}>
                  <dt className="text-xs font-medium text-gray-500">{item.label}</dt>
                  <dd className="text-sm font-semibold text-gray-900 mt-0.5">{String(item.value)}</dd>
                </div>
              ))}
            </dl>
          )}
        </div>
      ))}

      {!loading && !everythingFailed && failed.usage !== null && (
        <LoadFailed message={t('The usage figures could not be loaded.')} error={failed.usage} onRetry={() => void load()} />
      )}

      {/* Comprehensive usage from new endpoint */}
      {subscriptionUsage && (
        <div className="bg-white rounded-xl border border-gray-200 p-6">
          <h2 className="text-lg font-semibold text-gray-900 mb-4">Plan Limits & Usage</h2>
          <div className="space-y-4">
            {[
              { label: 'Active Employees', current: subscriptionUsage.usage.activeEmployees, max: subscriptionUsage.limits.maxEmployees },
              { label: 'Total Users',      current: subscriptionUsage.usage.totalUsers,      max: subscriptionUsage.limits.maxUsers },
              { label: 'Legal Companies',  current: subscriptionUsage.usage.totalCompanies,  max: subscriptionUsage.limits.maxCompanies },
              { label: 'Admin Users',      current: subscriptionUsage.usage.totalUsers,      max: subscriptionUsage.limits.maxAdminUsers },
            ].map(({ label, current, max }) => {
              const isUnlimited = max === 0;
              const pct = isUnlimited ? 0 : Math.min(100, (current / max) * 100);
              const isAtLimit = !isUnlimited && current >= max;
              const isWarn = !isUnlimited && pct >= 80;
              return (
                <div key={label}>
                  <div className="flex items-center justify-between mb-1.5 text-sm">
                    <span className="font-medium text-gray-700">{label}</span>
                    <span className={isAtLimit ? 'text-red-600 font-semibold' : isWarn ? 'text-orange-500' : 'text-gray-500'}>
                      {current.toLocaleString()} / {isUnlimited ? '∞' : max.toLocaleString()}
                      {isAtLimit && ' — limit reached!'}
                    </span>
                  </div>
                  {!isUnlimited && (
                    <UsageProgressBar label={label} pct={pct} />
                  )}
                </div>
              );
            })}
          </div>
          <div className="mt-4 pt-4 border-t border-gray-100">
            <p className="text-xs font-medium text-gray-500 mb-2">Enabled Modules</p>
            <div className="flex flex-wrap gap-1.5">
              {Object.entries(subscriptionUsage.featureFlags)
                .filter(([, v]) => v)
                .map(([k]) => (
                  <span key={k} className="text-xs px-2 py-0.5 rounded-full bg-green-50 text-green-700 border border-green-200">{k}</span>
                ))}
              {Object.entries(subscriptionUsage.featureFlags).every(([, v]) => !v) && (
                <span className="text-xs text-gray-400">No optional modules enabled</span>
              )}
            </div>
          </div>
          <div className="mt-3">
            <UpgradeButton />
          </div>
        </div>
      )}

      {/* Usage section */}
      {usage && (
        <div className="bg-white rounded-xl border border-gray-200 p-6">
          <h2 className="text-lg font-semibold text-gray-900 mb-4">Usage</h2>
          <div className="space-y-5">
            {/* Employees progress */}
            <div>
              <div className="flex items-center justify-between mb-1.5">
                <span className="text-sm font-medium text-gray-700">Active Employees</span>
                <span className="text-sm text-gray-500">
                  {usage.maxEmployees === 0
                    ? `${usage.activeEmployees} / Unlimited`
                    : `${usage.activeEmployees} / ${usage.maxEmployees}`}
                </span>
              </div>
              <UsageProgressBar
                label="Employee usage"
                pct={usage.maxEmployees > 0 ? (usage.activeEmployees / usage.maxEmployees) * 100 : 0}
              />
              {usage.maxEmployees > 0 && usage.activeEmployees / usage.maxEmployees > 0.8 && (
                <p className={`text-xs mt-1 ${usage.activeEmployees / usage.maxEmployees > 0.95 ? 'text-red-500' : 'text-orange-500'}`}>
                  {usage.activeEmployees / usage.maxEmployees > 0.95
                    ? 'Critical: approaching employee limit'
                    : 'Warning: employee limit approaching'}
                </p>
              )}
            </div>

            {/* Users progress */}
            <div>
              <div className="flex items-center justify-between mb-1.5">
                <span className="text-sm font-medium text-gray-700">Active Users</span>
                <span className="text-sm text-gray-500">
                  {usage.maxUsers === 0
                    ? `${usage.activeUsers} / Unlimited`
                    : `${usage.activeUsers} / ${usage.maxUsers}`}
                </span>
              </div>
              <UsageProgressBar
                label="User usage"
                pct={usage.maxUsers > 0 ? (usage.activeUsers / usage.maxUsers) * 100 : 0}
              />
              {usage.maxUsers > 0 && usage.activeUsers / usage.maxUsers > 0.8 && (
                <p className={`text-xs mt-1 ${usage.activeUsers / usage.maxUsers > 0.95 ? 'text-red-500' : 'text-orange-500'}`}>
                  {usage.activeUsers / usage.maxUsers > 0.95
                    ? 'Critical: approaching user limit'
                    : 'Warning: user limit approaching'}
                </p>
              )}
            </div>

            <dl className="bg-gray-50 rounded-lg px-4 py-3">
              <dt className="text-xs font-medium text-gray-500">Storage Used</dt>
              <dd className="text-xl font-bold text-gray-900 mt-0.5">{usage.storageUsedMb} MB</dd>
            </dl>

            {/* Request Upgrade button */}
            <UpgradeButton />
          </div>
        </div>
      )}

      {/* AI usage section */}
      {aiUsage && (
        <div className="bg-white rounded-xl border border-gray-200 p-6">
          <h2 className="text-lg font-semibold text-gray-900 mb-4">Kody Usage</h2>
          <div className="space-y-4">
            <div>
              <div className="flex items-center justify-between mb-1.5">
                <span className="text-sm font-medium text-gray-700">Monthly Tokens Used</span>
                <span className="text-sm text-gray-500">
                  {aiUsage.isUnlimited
                    ? `${aiUsage.tokensUsed.toLocaleString()} / Unlimited`
                    : `${aiUsage.tokensUsed.toLocaleString()} / ${aiUsage.monthlyTokenLimit.toLocaleString()}`}
                </span>
              </div>
              {!aiUsage.isUnlimited && (
                <UsageProgressBar label="Token usage" pct={aiUsage.usagePct} />
              )}
              {!aiUsage.isUnlimited && aiUsage.usagePct > 80 && (
                <p className={`text-xs mt-1 ${aiUsage.usagePct > 95 ? 'text-red-500' : 'text-orange-500'}`}>
                  {aiUsage.usagePct > 95 ? 'Critical: Token limit nearly reached' : 'Warning: Token usage is high'}
                </p>
              )}
            </div>
            <dl className="grid grid-cols-3 gap-4 bg-gray-50 rounded-lg px-4 py-3">
              <div>
                <dt className="text-xs font-medium text-gray-500">Requests</dt>
                <dd className="text-lg font-bold text-gray-900">{aiUsage.requestCount.toLocaleString()}</dd>
              </div>
              <div>
                <dt className="text-xs font-medium text-gray-500">Blocked</dt>
                <dd className="text-lg font-bold text-gray-900">{aiUsage.blockedCount.toLocaleString()}</dd>
              </div>
              <div>
                <dt className="text-xs font-medium text-gray-500">Plan</dt>
                <dd className="text-lg font-bold text-gray-900">{aiUsage.plan}</dd>
              </div>
            </dl>
            {!aiUsage.isUnlimited && aiUsage.usagePct > 90 && (
              <div className="pt-1">
                <UpgradeButton />
              </div>
            )}
          </div>
        </div>
      )}

      {/* Invoices (read-only) */}
      {!loading && !everythingFailed && (failed.invoices ? (
        <LoadFailed message={t('The invoices could not be loaded.')} error={failed.invoices} onRetry={() => void load()} />
      ) : (
        <div className="bg-white rounded-xl border border-gray-200 p-6">
          <h2 className="text-lg font-semibold text-gray-900 mb-1">Invoices</h2>
          <p className="text-xs text-gray-500 mb-4">Invoice records are managed by your account team. Contact support to request changes.</p>
          {invoices.length === 0 ? (
            <p className="text-sm text-gray-400 py-6 text-center">No invoice records found. Contact your account manager if you believe this is an error.</p>
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b border-gray-100 text-xs text-gray-500">
                    <th className="py-2 text-start font-medium">Invoice #</th>
                    <th className="py-2 text-start font-medium">Period</th>
                    <th className="py-2 text-end font-medium">Amount</th>
                    <th className="py-2 text-start font-medium">Status</th>
                    <th className="py-2 text-start font-medium">Due</th>
                    <th className="py-2 text-start font-medium">Paid</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-gray-50">
                  {invoices.map(inv => (
                    <tr key={inv.id} className="hover:bg-gray-50 transition-colors">
                      <td className="py-2.5 font-mono text-xs text-gray-700">{inv.invoiceNumber}</td>
                      <td className="py-2.5 text-gray-600">{inv.periodDescription ?? '—'}</td>
                      <td className="py-2.5 text-end font-medium text-gray-900">
                        {inv.amount.toLocaleString('en-US', { minimumFractionDigits: 2 })} {inv.currencyCode}
                      </td>
                      <td className="py-2.5">
                        <span className={`px-2 py-0.5 rounded text-xs font-medium ${
                          inv.status === 'Paid' ? 'bg-green-100 text-green-700' :
                          inv.status === 'Overdue' ? 'bg-red-100 text-red-700' :
                          inv.status === 'Sent' ? 'bg-blue-100 text-blue-700' :
                          inv.status === 'Cancelled' ? 'bg-gray-100 text-gray-500' :
                          'bg-amber-100 text-amber-700'
                        }`}>{inv.status}</span>
                      </td>
                      <td className="py-2.5 text-xs text-gray-500">{inv.dueDate}</td>
                      <td className="py-2.5 text-xs text-gray-500">{inv.paidDate ?? '—'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      ))}
    </div>
  );
}

/** Modules: switch optional modules on or off; the list and each lock come from the backend catalog. */
export function ModulesPanel() {
  const { t } = useLocale();
  // Re-sync the module state the sidebar, the route guard and the dashboard read after a change.
  const { refresh: refreshFeatureFlags } = useFeatureFlags();
  const { state, reload, setData } = useLoaded<TenantModuleList>(() => tenantModulesApi.list());
  const modules = state.status === 'ready' ? state.data : null;
  const [modulesError, setModulesError] = useState<string | null>(null);
  const [moduleBusy, setModuleBusy] = useState<string | null>(null);
  const modulesLoading = state.status === 'loading';

  async function toggleModule(key: string, current: boolean) {
    setModuleBusy(key);
    setModulesError(null);
    try {
      const updated = await tenantModulesApi.set(key, !current);
      if (modules) setData({ ...modules, modules: modules.modules.map(m => (m.key === key ? updated : m)) });
      // The sidebar, the route guard and the dashboard all read module state from
      // FeatureFlagContext, so re-sync it and the change lands immediately rather than after a browser refresh.
      await refreshFeatureFlags();
    } catch (err: unknown) {
      // The API refuses deliberately and says why; surface that rather than failing silently.
      const detail = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setModulesError(detail ?? 'Could not change that module. Please try again.');
      // Re-read so the UI reflects the server's actual state after a refusal.
      try { setData(await tenantModulesApi.list()); } catch {}
    } finally {
      setModuleBusy(null);
    }
  }

  if (state.status === 'error') return <LoadFailed message={t('Could not load your modules. Refresh to try again.')} error={state.error} onRetry={() => void reload()} />;

  return (
    <div className="space-y-4">
      <div className="rounded-xl border border-gray-200 bg-white p-5 dark:border-gray-700 dark:bg-gray-900">
        <p className="mt-1 text-sm text-gray-500 dark:text-gray-400">
          Switching a module off removes it everywhere — its navigation, its pages, its API and
          its notifications. Modules your organisation is legally required to run, and those the
          rest of the product depends on, are locked and explain why.
        </p>
      </div>

      {modulesError && (
        <div
          role="alert"
          className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700 dark:border-red-900 dark:bg-red-950 dark:text-red-300"
        >
          {modulesError}
        </div>
      )}

      {modulesLoading && !modules ? (
        <div className="rounded-xl border border-gray-200 bg-white p-10 text-center dark:border-gray-700 dark:bg-gray-900">
          <p className="text-sm text-gray-400">Loading modules…</p>
        </div>
      ) : !modules || modules.modules.length === 0 ? (
        <div className="rounded-xl border border-gray-200 bg-white p-10 text-center dark:border-gray-700 dark:bg-gray-900">
          <p className="text-sm text-gray-400">No modules to show.</p>
        </div>
      ) : (
        (['Optional', 'Statutory', 'Core'] as const).map(group => {
          const rows = modules.modules.filter(m => m.lockClass === group);
          if (rows.length === 0) return null;

          const heading =
            group === 'Optional' ? 'Your choice'
              : group === 'Statutory' ? 'Required by law'
                : 'Required by the product';
          const blurb =
            group === 'Optional' ? 'Switch these on and off freely.'
              : group === 'Statutory'
                ? `Locked because of where your organisation operates${modules.countryCode ? ` (${modules.countryCode})` : ''}.`
                : 'Other modules depend on these, so they cannot be switched off.';

          return (
            <section key={group}>
              <div className="mb-2 px-1">
                <h3 className="text-sm font-semibold text-gray-900 dark:text-gray-100">{heading}</h3>
                <p className="text-xs text-gray-500 dark:text-gray-400">{blurb}</p>
              </div>

              <div className="divide-y divide-gray-100 rounded-xl border border-gray-200 bg-white dark:divide-gray-800 dark:border-gray-700 dark:bg-gray-900">
                {rows.map(m => (
                  <div
                    key={m.key}
                    className="flex flex-col gap-3 px-5 py-4 sm:flex-row sm:items-center sm:justify-between"
                  >
                    <div className="min-w-0 pe-0 sm:pe-6">
                      <div className="flex flex-wrap items-center gap-2">
                        <p className="text-sm font-medium text-gray-900 dark:text-gray-100">{m.labelEn}</p>
                        {!m.canDisable && (
                          <span className="rounded bg-gray-100 px-1.5 py-0.5 text-[11px] font-medium text-gray-600 dark:bg-gray-800 dark:text-gray-300">
                            Locked on
                          </span>
                        )}
                      </div>
                      <p className="mt-0.5 text-xs text-gray-500 dark:text-gray-400">{m.description}</p>
                      {!m.canDisable && m.lockReason && (
                        <p className="mt-1.5 text-xs text-gray-400 dark:text-gray-500">{m.lockReason}</p>
                      )}
                    </div>

                    <div className="flex shrink-0 items-center gap-2 self-start sm:self-center">
                      {moduleBusy === m.key && (
                        <span className="text-xs text-gray-400">Saving…</span>
                      )}
                      {m.canDisable ? (
                        <Toggle
                          id={`module-${m.key}`}
                          ariaLabel={m.labelEn}
                          checked={m.enabled}
                          onChange={() => toggleModule(m.key, m.enabled)}
                        />
                      ) : (
                        // Rendered as a disabled switch rather than hidden: an administrator
                        // looking for "where do I turn this off" needs to see that it exists
                        // and is deliberately locked, not wonder if it is missing.
                        <button
                          type="button"
                          role="switch"
                          aria-checked="true"
                          aria-label={`${m.labelEn} is locked on`}
                          disabled
                          title={m.lockReason ?? undefined}
                          className="relative inline-flex h-6 w-11 shrink-0 cursor-not-allowed items-center rounded-full bg-gray-300 opacity-60 dark:bg-gray-600"
                        >
                          <span className="inline-block h-4 w-4 translate-x-6 transform rounded-full bg-white shadow rtl:-translate-x-6" />
                        </button>
                      )}
                    </div>
                  </div>
                ))}
              </div>
            </section>
          );
        })
      )}
    </div>
  );
}

/** Language and region: the default language, calendar, formats, time zone, currency and work week. */
export function LocalizationPanel({ onDirtyChange }: PanelProps = {}) {
  const { t } = useLocale();
  // Global providers loaded once on mount: re-sync them after a save so the rest of the app (sidebar nav,
  // currency formatting, etc.) reflects the change without a full reload.
  const { reload: reloadTenantSettings } = useTenantSettingsContext();
  const { state, reload } = useLoaded<TenantLocalizationSetting>(() => tenantAdminApi.getLocalization());
  const [localization, setLocalization] = useState<TenantLocalizationSetting | null>(null);
  const [savedLocalization, setSavedLocalization] = useState<TenantLocalizationSetting | null>(null);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [saveError, setSaveError] = useState('');
  useEffect(() => {
    if (state.status === 'ready') { setLocalization(state.data); setSavedLocalization(state.data); }
  }, [state]);

  const dirty = localization !== null && JSON.stringify(localization) !== JSON.stringify(savedLocalization);
  useLayoutEffect(() => { onDirtyChange?.(dirty); return () => onDirtyChange?.(false); }, [dirty, onDirtyChange]);
  const edit = (patch: Partial<TenantLocalizationSetting>) => { setLocalization(p => (p ? { ...p, ...patch } : p)); setSaved(false); setSaveError(''); };

  async function saveLocalization() {
    if (!localization) return;
    setSaving(true);
    setSaveError('');
    try {
      const updated = await tenantAdminApi.upsertLocalization(localization);
      setLocalization(updated);
      setSavedLocalization(updated);
      setSaved(true);
      // The save worked; a failed re-sync only means the rest of the app catches up on its next load.
      try { await reloadTenantSettings(); } catch { /* ignore */ }
    } catch (error) {
      setSaveError(httpStatus(error) === 403
        ? t('Only an administrator with access to all companies can change settings for the whole organisation.')
        : t('The settings could not be saved. Please try again.'));
    } finally {
      setSaving(false);
    }
  }

  if (state.status === 'error') return <LoadFailed message={t('The language and region settings could not be loaded.')} error={state.error} onRetry={() => void reload()} />;
  if (!localization) return <p role="status" className="text-sm text-gray-500">{t('Loading…')}</p>;

  return (
    <div className="bg-white rounded-xl border border-gray-200 p-6 space-y-4">
      <div className="grid grid-cols-2 gap-4">
        <div>
          <label htmlFor="loc-language" className="block text-xs font-medium text-gray-700 mb-1">Default Language</label>
          <select
            id="loc-language"
            value={localization.defaultLanguage}
            onChange={e => edit({ defaultLanguage: e.target.value })}
            className="w-full border border-gray-200 rounded-lg px-3 py-2 text-sm"
          >
            <option value="en">English</option>
            <option value="ar">Arabic</option>
          </select>
        </div>
        <div>
          <label htmlFor="loc-calendar" className="block text-xs font-medium text-gray-700 mb-1">Calendar System</label>
          <select
            id="loc-calendar"
            value={localization.calendarSystem}
            onChange={e => edit({ calendarSystem: e.target.value })}
            className="w-full border border-gray-200 rounded-lg px-3 py-2 text-sm"
          >
            <option value="Gregorian">Gregorian</option>
            <option value="Hijri">Hijri</option>
          </select>
        </div>
        <div>
          <label htmlFor="loc-country" className="block text-xs font-medium text-gray-700 mb-1">Country Code</label>
          <select
            id="loc-country"
            value={localization.countryCode}
            onChange={e => edit({ countryCode: e.target.value })}
            className="w-full border border-gray-200 rounded-lg px-3 py-2 text-sm"
          >
            {['US', 'GB', 'CA', 'AU', 'DE', 'FR', 'AE', 'SA', 'QA', 'KW', 'BH', 'OM', 'JO', 'EG', 'PK', 'IN', 'SG', 'PH'].map(c => <option key={c}>{c}</option>)}
          </select>
        </div>
        <div>
          <label htmlFor="loc-currency" className="block text-xs font-medium text-gray-700 mb-1">Currency</label>
          <select
            id="loc-currency"
            value={localization.currencyCode}
            onChange={e => edit({ currencyCode: e.target.value })}
            className="w-full border border-gray-200 rounded-lg px-3 py-2 text-sm"
          >
            {['USD', 'GBP', 'EUR', 'CAD', 'AUD', 'SGD', 'AED', 'SAR', 'QAR', 'KWD', 'BHD', 'OMR', 'JOD', 'EGP', 'PKR', 'INR', 'PHP'].map(c => <option key={c}>{c}</option>)}
          </select>
        </div>
        <div>
          <label htmlFor="loc-workweek" className="block text-xs font-medium text-gray-700 mb-1">Work Week</label>
          <select
            id="loc-workweek"
            value={localization.workWeek}
            onChange={e => edit({ workWeek: e.target.value })}
            className="w-full border border-gray-200 rounded-lg px-3 py-2 text-sm"
          >
            <option value="Sun-Thu">Sun–Thu (GCC)</option>
            <option value="Mon-Fri">Mon–Fri</option>
            <option value="Mon-Sat">Mon–Sat</option>
          </select>
        </div>
        <div>
          <label htmlFor="loc-timezone" className="block text-xs font-medium text-gray-700 mb-1">Timezone</label>
          <select
            id="loc-timezone"
            value={localization.defaultTimezone}
            onChange={e => edit({ defaultTimezone: e.target.value })}
            className="w-full border border-gray-200 rounded-lg px-3 py-2 text-sm"
          >
            {[
              'America/New_York', 'America/Chicago', 'America/Denver', 'America/Los_Angeles',
              'America/Toronto', 'Europe/London', 'Europe/Berlin', 'Europe/Paris',
              'Australia/Sydney', 'Asia/Singapore', 'Asia/Dubai', 'Asia/Riyadh',
              'Asia/Kuwait', 'Asia/Bahrain', 'Asia/Muscat', 'Asia/Doha',
              'Africa/Cairo', 'Asia/Karachi', 'Asia/Kolkata', 'UTC',
            ].map(tz => <option key={tz}>{tz}</option>)}
          </select>
        </div>
      </div>

      <div className="flex items-center gap-6">
        <div className="flex items-center gap-2">
          <Toggle
            id="loc-rtl"
            checked={localization.rtlEnabled}
            onChange={v => edit({ rtlEnabled: v })}
          />
          <label htmlFor="loc-rtl" className="text-sm text-gray-700 cursor-pointer">RTL Layout</label>
        </div>
        <div className="flex items-center gap-2">
          <Toggle
            id="loc-hijri"
            checked={localization.hijriDatesEnabled}
            onChange={v => edit({ hijriDatesEnabled: v })}
          />
          <label htmlFor="loc-hijri" className="text-sm text-gray-700 cursor-pointer">Show Hijri Dates</label>
        </div>
      </div>

      <div className="flex items-center gap-3">
        <button
          type="button"
          onClick={saveLocalization}
          disabled={saving}
          className="px-6 py-2.5 bg-indigo-600 text-white rounded-lg text-sm font-medium disabled:opacity-50 hover:bg-indigo-700"
        >
          {saving ? t('Saving…') : t('Save Settings')}
        </button>
        {saved && <span role="status" className="text-sm text-green-600 font-medium">{t('Saved!')}</span>}
      </div>
      {saveError && <p role="alert" className="text-sm text-red-600">{saveError}</p>}
    </div>
  );
}

/** Branding: the logo, names, portal title and colour. */
export function BrandingPanel({ onDirtyChange }: PanelProps = {}) {
  const { t } = useLocale();
  const { state, reload } = useLoaded<TenantBranding | null>(() => tenantAdminApi.getBranding());
  const [branding, setBranding] = useState<TenantBranding | null>(null);
  const [savedBranding, setSavedBranding] = useState<TenantBranding | null>(null);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [saveError, setSaveError] = useState('');
  useEffect(() => {
    if (state.status === 'ready') { setBranding(state.data); setSavedBranding(state.data); }
  }, [state]);

  const dirty = JSON.stringify(branding) !== JSON.stringify(savedBranding);
  useLayoutEffect(() => { onDirtyChange?.(dirty); return () => onDirtyChange?.(false); }, [dirty, onDirtyChange]);
  const edit = (patch: Partial<TenantBranding>) => { setBranding(p => (p ? { ...p, ...patch } : p)); setSaved(false); setSaveError(''); };

  async function saveBranding() {
    if (!branding) return;
    setSaving(true);
    setSaveError('');
    try {
      const updated = await tenantAdminApi.upsertBranding(branding);
      setBranding(updated);
      setSavedBranding(updated);
      setSaved(true);
    } catch (error) {
      setSaveError(httpStatus(error) === 403
        ? t('Only an administrator with access to all companies can change settings for the whole organisation.')
        : t('The settings could not be saved. Please try again.'));
    } finally {
      setSaving(false);
    }
  }

  if (state.status === 'error') return <LoadFailed message={t('The branding could not be loaded.')} error={state.error} onRetry={() => void reload()} />;
  if (state.status === 'loading') return <p role="status" className="text-sm text-gray-500">{t('Loading…')}</p>;

  return (
    <div className="bg-white rounded-xl border border-gray-200 p-6 space-y-4">
      {branding && (
        <>
          <div className="grid grid-cols-2 gap-4">
            {[
              { key: 'companyNameEn', label: 'Company Name (English)' },
              { key: 'companyNameAr', label: 'Company Name (Arabic)' },
              { key: 'portalTitle', label: 'Portal Title' },
              { key: 'logoUrl', label: 'Logo URL' },
              { key: 'faviconUrl', label: 'Favicon URL' },
            ].map(field => (
              <div key={field.key}>
                <label htmlFor={`brand-${field.key}`} className="block text-xs font-medium text-gray-700 mb-1">{field.label}</label>
                <input
                  id={`brand-${field.key}`}
                  value={branding[field.key as keyof TenantBranding] as string}
                  onChange={e => edit({ [field.key]: e.target.value })}
                  className="w-full border border-gray-200 rounded-lg px-3 py-2 text-sm"
                />
              </div>
            ))}
            <div>
              <label htmlFor="brand-primaryColor-text" className="block text-xs font-medium text-gray-700 mb-1">Primary Color</label>
              <div className="flex items-center gap-2">
                <input
                  id="brand-primaryColor-picker"
                  type="color"
                  aria-label="Primary color picker"
                  value={branding.primaryColor}
                  onChange={e => edit({ primaryColor: e.target.value })}
                  className="h-9 w-16 border border-gray-200 rounded cursor-pointer"
                />
                <input
                  id="brand-primaryColor-text"
                  value={branding.primaryColor}
                  onChange={e => edit({ primaryColor: e.target.value })}
                  className="flex-1 border border-gray-200 rounded-lg px-3 py-2 text-sm font-mono"
                />
              </div>
            </div>
          </div>
          <div className="flex items-center gap-3 pt-2">
            <button
              type="button"
              onClick={saveBranding}
              disabled={saving}
              className="px-6 py-2.5 bg-indigo-600 text-white rounded-lg text-sm font-medium disabled:opacity-50 hover:bg-indigo-700"
            >
              {saving ? t('Saving…') : t('Save Branding')}
            </button>
            {saved && <span role="status" className="text-sm text-green-600 font-medium">{t('Saved!')}</span>}
          </div>
          {saveError && <p role="alert" className="text-sm text-red-600">{saveError}</p>}
        </>
      )}
      {!branding && (
        <div className="text-center py-8 text-sm text-gray-400">
          <p>{t('No branding configured. System defaults are active.')}</p>
          <button
            type="button"
            onClick={() => setBranding({ logoUrl: '', primaryColor: '#2563EB', accentColor: '#7C3AED', companyNameEn: '', companyNameAr: '', portalTitle: 'HR Portal', faviconUrl: '' })}
            className="mt-3 px-4 py-2 bg-indigo-600 text-white rounded-lg text-sm"
          >
            {t('Configure Branding')}
          </button>
        </div>
      )}
    </div>
  );
}

/**
 * Country payroll keys: extra key/value rules per country. Only `weekend_days` is read by the backend today; the other
 * keys are stored for reporting and integrations.
 */
export function CountryPayrollKeysPanel() {
  const { t } = useLocale();
  const { state, reload, setData } = useLoaded<CountryRule[]>(() => client.get<CountryRule[]>('/api/tenant-admin/country-rules').then(r => r.data));
  const rules = state.status === 'ready' ? state.data : [];
  const rulesLoading = state.status === 'loading';
  const [newRule, setNewRule] = useState({ countryCode: 'AE', ruleKey: '', ruleValue: '', dataType: 'string' });
  const [addingRule, setAddingRule] = useState(false);
  const [showAddRule, setShowAddRule] = useState(false);
  const [ruleError, setRuleError] = useState('');

  async function deleteRule(id: string) {
    setRuleError('');
    try {
      await client.delete(`/api/tenant-admin/country-rules/${id}`);
      setData(rules.filter(r => r.id !== id));
    } catch { setRuleError(t('The rule could not be deleted. Please try again.')); }
  }

  async function addRule() {
    if (!newRule.ruleKey.trim() || !newRule.ruleValue.trim()) return;
    setAddingRule(true);
    setRuleError('');
    try {
      const created = await client.post<CountryRule>('/api/tenant-admin/country-rules', newRule).then(r => r.data);
      setData([...rules, created]);
      setNewRule({ countryCode: 'AE', ruleKey: '', ruleValue: '', dataType: 'string' });
      setShowAddRule(false);
    } catch { setRuleError(t('The rule could not be added. Please try again.')); } finally {
      setAddingRule(false);
    }
  }

  function getCountryName(code: string) {
    return GCC_COUNTRIES.find(c => c.code === code)?.name ?? code;
  }

  // Group rules by country
  const rulesByCountry = rules.reduce<Record<string, CountryRule[]>>((acc, r) => {
    if (!acc[r.countryCode]) acc[r.countryCode] = [];
    acc[r.countryCode].push(r);
    return acc;
  }, {});

  if (state.status === 'error') return <LoadFailed message={t('The country payroll keys could not be loaded.')} error={state.error} onRetry={() => void reload()} />;

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-end">
        <button
          type="button"
          onClick={() => setShowAddRule(v => !v)}
          className="px-4 py-2 bg-indigo-600 text-white rounded-lg text-sm font-medium hover:bg-indigo-700"
        >
          {showAddRule ? 'Cancel' : '+ Add Rule'}
        </button>
      </div>

      {showAddRule && (
        <div className="bg-white rounded-xl border border-gray-200 p-5 space-y-4">
          <h3 className="text-sm font-semibold text-gray-900">New Rule</h3>
          <div className="grid grid-cols-2 gap-3">
            <div>
              <label htmlFor="rule-country" className="block text-xs font-medium text-gray-700 mb-1">Country</label>
              <select
                id="rule-country"
                value={newRule.countryCode}
                onChange={e => setNewRule(p => ({ ...p, countryCode: e.target.value }))}
                className="w-full border border-gray-200 rounded-lg px-3 py-2 text-sm"
              >
                {GCC_COUNTRIES.map(c => (
                  <option key={c.code} value={c.code}>{c.name} ({c.code})</option>
                ))}
              </select>
            </div>
            <div>
              <label htmlFor="rule-datatype" className="block text-xs font-medium text-gray-700 mb-1">Data Type</label>
              <select
                id="rule-datatype"
                value={newRule.dataType}
                onChange={e => setNewRule(p => ({ ...p, dataType: e.target.value }))}
                className="w-full border border-gray-200 rounded-lg px-3 py-2 text-sm"
              >
                <option value="string">String</option>
                <option value="number">Number</option>
                <option value="bool">Boolean</option>
              </select>
            </div>
            <div>
              <label htmlFor="rule-key" className="block text-xs font-medium text-gray-700 mb-1">Rule Key</label>
              <input
                id="rule-key"
                value={newRule.ruleKey}
                onChange={e => setNewRule(p => ({ ...p, ruleKey: e.target.value }))}
                placeholder="e.g. min_wage"
                className="w-full border border-gray-200 rounded-lg px-3 py-2 text-sm"
              />
            </div>
            <div>
              <label htmlFor="rule-value" className="block text-xs font-medium text-gray-700 mb-1">Rule Value</label>
              <input
                id="rule-value"
                value={newRule.ruleValue}
                onChange={e => setNewRule(p => ({ ...p, ruleValue: e.target.value }))}
                placeholder="e.g. 4000"
                className="w-full border border-gray-200 rounded-lg px-3 py-2 text-sm"
              />
            </div>
          </div>
          <button
            type="button"
            onClick={addRule}
            disabled={addingRule || !newRule.ruleKey.trim() || !newRule.ruleValue.trim()}
            className="px-5 py-2 bg-indigo-600 text-white rounded-lg text-sm font-medium disabled:opacity-50 hover:bg-indigo-700"
          >
            {addingRule ? 'Adding...' : 'Add Rule'}
          </button>
        </div>
      )}

      {rulesLoading ? (
        <div className="bg-white rounded-xl border border-gray-200 p-8 text-center text-sm text-gray-400">Loading...</div>
      ) : rules.length === 0 ? (
        <div className="bg-white rounded-xl border border-gray-200 p-8 text-center text-sm text-gray-400">
          No country rules configured.
        </div>
      ) : (
        <div className="space-y-3">
          {Object.entries(rulesByCountry).map(([code, countryRules]) => (
            <div key={code} className="bg-white rounded-xl border border-gray-200 overflow-hidden">
              <div className="px-5 py-3 bg-gray-50 border-b border-gray-100">
                <p className="text-sm font-semibold text-gray-800">
                  {getCountryName(code)} <span className="text-gray-400 font-normal ms-1">({code})</span>
                </p>
              </div>
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b border-gray-100">
                    {['Rule Key', 'Rule Value', 'Type', 'Override', 'Eff. From', 'Eff. To', 'Actions'].map(col => (
                      <th key={col} className="text-start text-xs font-medium text-gray-500 px-4 py-2">{col}</th>
                    ))}
                  </tr>
                </thead>
                <tbody className="divide-y divide-gray-50">
                  {countryRules.map(rule => (
                    <tr key={rule.id} className="hover:bg-gray-50 transition-colors">
                      <td className="px-4 py-2.5 font-mono text-xs text-gray-700">{rule.ruleKey}</td>
                      <td className="px-4 py-2.5 text-gray-900">{rule.ruleValue}</td>
                      <td className="px-4 py-2.5 text-gray-500">{rule.dataType}</td>
                      <td className="px-4 py-2.5">
                        {rule.isOverride ? (
                          <span className="inline-block px-1.5 py-0.5 bg-amber-100 text-amber-700 rounded text-xs">Override</span>
                        ) : (
                          <span className="text-gray-400">—</span>
                        )}
                      </td>
                      <td className="px-4 py-2.5 text-xs text-gray-400">{rule.effectiveFrom ? new Date(rule.effectiveFrom).toLocaleDateString() : '—'}</td>
                      <td className="px-4 py-2.5 text-xs text-gray-400">{rule.effectiveTo ? new Date(rule.effectiveTo).toLocaleDateString() : '—'}</td>
                      <td className="px-4 py-2.5">
                        <button
                          type="button"
                          onClick={() => deleteRule(rule.id)}
                          aria-label={`Delete rule ${rule.ruleKey}`}
                          className="text-xs text-red-500 hover:text-red-700 transition-colors"
                        >
                          Delete
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ))}
        </div>
      )}

      {ruleError && <p role="alert" className="text-sm text-red-600">{ruleError}</p>}

      <p className="text-sm text-gray-600">
        {t('Weekend days are also set in Setup → Country rules.')}{' '}
        <Link href="/setup?tab=gccSettings" className="font-medium text-indigo-600 underline hover:text-indigo-800">{t('Open Country rules')}</Link>
      </p>
    </div>
  );
}
