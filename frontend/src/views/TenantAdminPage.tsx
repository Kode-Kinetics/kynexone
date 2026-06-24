'use client';

import { useState, useEffect } from 'react';
import {
  tenantAdminApi,
  type TenantFeatureFlag,
  type TenantLocalizationSetting,
  type TenantBranding,
  type TenantSubscription,
  type TenantInvoiceSummary,
  type TenantAiUsageSummary,
} from '../api/intelligence';
import client from '../api/client';
import { statutoryRulesApi, countryPacksApi } from '../api/countryPacks';
import type { StatutoryRuleDto, CountryPackOption, CreateStatutoryRuleRequest } from '../api/countryPacks';
import { HelpTextManager } from '../components/HelpTextManager';

type Tab = 'subscription' | 'features' | 'invoices' | 'localization' | 'branding' | 'security' | 'country-rules' | 'statutory-rules' | 'help-text';

const FEATURE_KEYS = [
  { key: 'ai_assistant', label: 'Workspace Assistant', description: 'Enable natural-language workspace queries' },
  { key: 'mobile_app', label: 'Mobile App', description: 'Enable mobile API endpoints' },
  { key: 'wps_export', label: 'WPS/SIF Export', description: 'GCC WPS payroll file generation' },
  { key: 'eosb_calc', label: 'EOSB Calculator', description: 'End-of-service benefit computation' },
  { key: 'resume_screening', label: 'Resume Screening', description: 'Automated CV analysis (advisory)' },
  { key: 'payroll_ai_validation', label: 'Payroll Anomaly Checks', description: 'Variance detection for payroll' },
  { key: 'risk_scores', label: 'Employee Risk Scores', description: 'Churn and burnout risk indicators (advisory)' },
  { key: 'hijri_calendar', label: 'Hijri Calendar', description: 'Show Hijri dates alongside Gregorian' },
];

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

interface SecuritySettings {
  passwordMinLength: number;
  passwordRequireUppercase: boolean;
  passwordRequireDigit: boolean;
  passwordRequireSpecialChar: boolean;
  sessionTimeoutMinutes: number;
  maxFailedLoginAttempts: number;
  lockoutDurationMinutes: number;
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

// Simple toggle switch with accessible label
function Toggle({ id, checked, onChange }: { id: string; checked: boolean; onChange: (v: boolean) => void }) {
  return (
    <button
      id={id}
      type="button"
      role="switch"
      aria-checked={checked ? 'true' : 'false'}
      onClick={() => onChange(!checked)}
      className={`relative inline-flex h-6 w-11 shrink-0 items-center rounded-full transition-colors focus:outline-none focus-visible:ring-2 focus-visible:ring-indigo-500 ${
        checked ? 'bg-indigo-600' : 'bg-gray-200'
      }`}
    >
      <span className={`inline-block h-4 w-4 transform rounded-full bg-white shadow transition-transform ${
        checked ? 'translate-x-6' : 'translate-x-1'
      }`} />
    </button>
  );
}

export default function TenantAdminPage() {
  const [tab, setTab] = useState<Tab>('subscription');
  const [subscription, setSubscription] = useState<TenantSubscription | null>(null);
  const [flags, setFlags] = useState<TenantFeatureFlag[]>([]);
  const [localization, setLocalization] = useState<TenantLocalizationSetting | null>(null);
  const [branding, setBranding] = useState<TenantBranding | null>(null);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);

  // Usage
  const [usage, setUsage] = useState<UsageData | null>(null);
  const [subscriptionUsage, setSubscriptionUsage] = useState<SubscriptionUsage | null>(null);

  // Invoices
  const [invoices, setInvoices] = useState<TenantInvoiceSummary[]>([]);
  const [invoicesLoading, setInvoicesLoading] = useState(false);

  // AI Usage
  const [aiUsage, setAiUsage] = useState<TenantAiUsageSummary | null>(null);

  // Security
  const [security, setSecurity] = useState<SecuritySettings | null>(null);
  const [securitySaving, setSecuritySaving] = useState(false);
  const [securitySaved, setSecuritySaved] = useState(false);
  const [securityError, setSecurityError] = useState('');

  // Country rules
  const [rules, setRules] = useState<CountryRule[]>([]);
  const [rulesLoading, setRulesLoading] = useState(false);
  const [newRule, setNewRule] = useState({ countryCode: 'AE', ruleKey: '', ruleValue: '', dataType: 'string' });
  const [addingRule, setAddingRule] = useState(false);
  const [showAddRule, setShowAddRule] = useState(false);

  // Statutory rules (GCC-2 pack engine — effective-dated, tenant-overridable)
  const [statRules, setStatRules] = useState<StatutoryRuleDto[]>([]);
  const [statRulesLoading, setStatRulesLoading] = useState(false);
  const [availablePacks, setAvailablePacks] = useState<CountryPackOption[]>([]);
  const [showAddStatRule, setShowAddStatRule] = useState(false);
  const [addingStatRule, setAddingStatRule] = useState(false);
  const [newStatRule, setNewStatRule] = useState<CreateStatutoryRuleRequest>({
    countryCode: 'SAU', jurisdiction: 'KSA-mainland', ruleKey: '', ruleValue: '',
    dataType: 'decimal', description: '', effectiveFrom: new Date().toISOString().slice(0, 10),
  });

  useEffect(() => {
    if (tab === 'subscription') { loadSubscription(); loadUsage(); loadAiUsage(); loadSubscriptionUsage(); }
    if (tab === 'features') loadFlags();
    if (tab === 'invoices') loadInvoices();
    if (tab === 'localization') loadLocalization();
    if (tab === 'branding') loadBranding();
    if (tab === 'security') loadSecurity();
    if (tab === 'country-rules') loadRules();
    if (tab === 'statutory-rules') { loadStatRules(); countryPacksApi.available().then(setAvailablePacks).catch(() => {}); }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tab]);

  async function loadSubscription() {
    try { setSubscription(await tenantAdminApi.getSubscription()); } catch {}
  }

  async function loadUsage() {
    try {
      const data = await client.get<UsageData>('/api/tenant-admin/usage').then(r => r.data);
      setUsage(data);
    } catch {}
  }

  async function loadSubscriptionUsage() {
    try {
      const data = await client.get<SubscriptionUsage>('/api/tenant-admin/subscription/usage').then(r => r.data);
      setSubscriptionUsage(data);
    } catch {}
  }

  async function loadInvoices() {
    setInvoicesLoading(true);
    try { setInvoices(await tenantAdminApi.listInvoices()); } catch {} finally { setInvoicesLoading(false); }
  }

  async function loadAiUsage() {
    try { setAiUsage(await tenantAdminApi.getAiUsage()); } catch {}
  }

  async function loadFlags() {
    try { setFlags(await tenantAdminApi.listFeatureFlags()); } catch {}
  }

  async function loadLocalization() {
    try { setLocalization(await tenantAdminApi.getLocalization()); } catch {}
  }

  async function loadBranding() {
    try { setBranding(await tenantAdminApi.getBranding()); } catch {}
  }

  async function loadSecurity() {
    try {
      const data = await client.get<SecuritySettings>('/api/access/security-settings').then(r => r.data);
      setSecurity(data);
    } catch {}
  }

  async function loadRules() {
    setRulesLoading(true);
    try {
      const data = await client.get<CountryRule[]>('/api/tenant-admin/country-rules').then(r => r.data);
      setRules(data);
    } catch {} finally {
      setRulesLoading(false);
    }
  }

  async function toggleFlag(key: string, current: boolean) {
    try {
      const updated = await tenantAdminApi.setFeatureFlag(key, !current);
      setFlags(prev => {
        const exists = prev.find(f => f.featureKey === key);
        if (exists) return prev.map(f => f.featureKey === key ? updated : f);
        return [...prev, updated];
      });
    } catch {}
  }

  function isFlagEnabled(key: string) {
    return flags.find(f => f.featureKey === key)?.isEnabled ?? false;
  }

  async function saveLocalization() {
    if (!localization) return;
    setSaving(true);
    try {
      const updated = await tenantAdminApi.upsertLocalization(localization);
      setLocalization(updated);
      setSaved(true);
      setTimeout(() => setSaved(false), 2000);
    } catch {} finally {
      setSaving(false);
    }
  }

  async function saveBranding() {
    if (!branding) return;
    setSaving(true);
    try {
      const updated = await tenantAdminApi.upsertBranding(branding);
      setBranding(updated);
      setSaved(true);
      setTimeout(() => setSaved(false), 2000);
    } catch {} finally {
      setSaving(false);
    }
  }

  async function saveSecurity() {
    if (!security) return;
    setSecuritySaving(true);
    setSecurityError('');
    try {
      await client.put('/api/access/security-settings', security);
      setSecuritySaved(true);
      setTimeout(() => setSecuritySaved(false), 2000);
    } catch {
      setSecurityError('Failed to save security settings. Please try again.');
    } finally {
      setSecuritySaving(false);
    }
  }

  async function deleteRule(id: string) {
    try {
      await client.delete(`/api/tenant-admin/country-rules/${id}`);
      setRules(prev => prev.filter(r => r.id !== id));
    } catch {}
  }

  async function addRule() {
    if (!newRule.ruleKey.trim() || !newRule.ruleValue.trim()) return;
    setAddingRule(true);
    try {
      const created = await client.post<CountryRule>('/api/tenant-admin/country-rules', newRule).then(r => r.data);
      setRules(prev => [...prev, created]);
      setNewRule({ countryCode: 'AE', ruleKey: '', ruleValue: '', dataType: 'string' });
      setShowAddRule(false);
    } catch {} finally {
      setAddingRule(false);
    }
  }

  // Statutory rules (GCC-2 pack engine)
  async function loadStatRules() {
    setStatRulesLoading(true);
    try { setStatRules(await statutoryRulesApi.list()); }
    catch {} finally { setStatRulesLoading(false); }
  }

  async function deleteStatRule(id: string) {
    try {
      await statutoryRulesApi.remove(id);
      setStatRules(prev => prev.filter(r => r.id !== id));
    } catch {}
  }

  async function addStatRule() {
    setAddingStatRule(true);
    try {
      const created = await statutoryRulesApi.create({
        ...newStatRule,
        effectiveFrom: new Date(newStatRule.effectiveFrom).toISOString(),
      });
      setStatRules(prev => [...prev, created]);
      setNewStatRule({ countryCode: 'SAU', jurisdiction: 'KSA-mainland', ruleKey: '', ruleValue: '',
        dataType: 'decimal', description: '', effectiveFrom: new Date().toISOString().slice(0, 10) });
      setShowAddStatRule(false);
    } catch {} finally { setAddingStatRule(false); }
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

  const tabs: { id: Tab; label: string }[] = [
    { id: 'subscription', label: 'Subscription' },
    { id: 'features', label: 'Feature Flags' },
    { id: 'invoices', label: 'Invoices' },
    { id: 'localization', label: 'Localization' },
    { id: 'branding', label: 'Branding' },
    { id: 'security', label: 'Security' },
    { id: 'country-rules', label: 'Country Rules' },
    { id: 'statutory-rules', label: 'Statutory Rules Engine' },
    { id: 'help-text', label: 'Help Text' },
  ];

  return (
    <div className="p-6 max-w-4xl mx-auto space-y-4">
      <div>
        <h1 className="text-2xl font-bold text-gray-900">Tenant Administration</h1>
        <p className="text-sm text-gray-500">Manage subscription, feature flags, localization, and branding</p>
      </div>

      {/* PastDue warning banner */}
      {subscription?.status === 'PastDue' && (
        <div className="flex items-start gap-3 rounded-xl border border-amber-300 bg-amber-50 px-4 py-3">
          <svg className="mt-0.5 h-5 w-5 shrink-0 text-amber-500" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01M10.29 3.86L1.82 18a2 2 0 001.71 3h16.94a2 2 0 001.71-3L13.71 3.86a2 2 0 00-3.42 0z" />
          </svg>
          <div>
            <p className="text-sm font-semibold text-amber-800">Payment overdue — please settle your account</p>
            <p className="text-xs text-amber-700 mt-0.5">
              Your subscription has an outstanding balance. All features remain active for now, but continued non-payment may result in suspension. Contact your account manager or check the Invoices tab.
            </p>
          </div>
        </div>
      )}

      <div className="flex gap-1 border-b border-gray-200 flex-wrap">
        {tabs.map(t => (
          <button
            key={t.id}
            type="button"
            onClick={() => setTab(t.id)}
            className={`px-4 py-2.5 text-sm font-medium border-b-2 transition-colors ${
              tab === t.id ? 'border-indigo-600 text-indigo-600' : 'border-transparent text-gray-500 hover:text-gray-700'
            }`}
          >
            {t.label}
          </button>
        ))}
      </div>

      {/* Subscription */}
      {tab === 'subscription' && (
        <div className="space-y-4">
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

          {/* AI Usage section */}
          {aiUsage && (
            <div className="bg-white rounded-xl border border-gray-200 p-6">
              <h2 className="text-lg font-semibold text-gray-900 mb-4">Assistant Usage</h2>
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
        </div>
      )}

      {/* Feature Flags */}
      {tab === 'features' && (
        <div className="bg-white rounded-xl border border-gray-200 divide-y divide-gray-100">
          {FEATURE_KEYS.map(feat => {
            const enabled = isFlagEnabled(feat.key);
            return (
              <div key={feat.key} className="flex items-center justify-between px-6 py-4">
                <div>
                  <p className="text-sm font-medium text-gray-900">{feat.label}</p>
                  <p className="text-xs text-gray-500 mt-0.5">{feat.description}</p>
                </div>
                <Toggle
                  id={`flag-${feat.key}`}
                  checked={enabled}
                  onChange={() => toggleFlag(feat.key, enabled)}
                />
              </div>
            );
          })}
        </div>
      )}

      {/* Invoices (read-only) */}
      {tab === 'invoices' && (
        <div className="space-y-4">
          <div className="bg-white rounded-xl border border-gray-200 p-6">
            <h2 className="text-lg font-semibold text-gray-900 mb-1">Invoices</h2>
            <p className="text-xs text-gray-500 mb-4">Invoice records are managed by your account team. Contact support to request changes.</p>
            {invoicesLoading ? (
              <p className="text-sm text-gray-400 py-6 text-center">Loading invoices…</p>
            ) : invoices.length === 0 ? (
              <p className="text-sm text-gray-400 py-6 text-center">No invoice records found. Contact your account manager if you believe this is an error.</p>
            ) : (
              <div className="overflow-x-auto">
                <table className="w-full text-sm">
                  <thead>
                    <tr className="border-b border-gray-100 text-xs text-gray-500">
                      <th className="py-2 text-left font-medium">Invoice #</th>
                      <th className="py-2 text-left font-medium">Period</th>
                      <th className="py-2 text-right font-medium">Amount</th>
                      <th className="py-2 text-left font-medium">Status</th>
                      <th className="py-2 text-left font-medium">Due</th>
                      <th className="py-2 text-left font-medium">Paid</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-gray-50">
                    {invoices.map(inv => (
                      <tr key={inv.id} className="hover:bg-gray-50 transition-colors">
                        <td className="py-2.5 font-mono text-xs text-gray-700">{inv.invoiceNumber}</td>
                        <td className="py-2.5 text-gray-600">{inv.periodDescription ?? '—'}</td>
                        <td className="py-2.5 text-right font-medium text-gray-900">
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
        </div>
      )}

      {/* Localization */}
      {tab === 'localization' && localization && (
        <div className="bg-white rounded-xl border border-gray-200 p-6 space-y-4">
          <h2 className="text-lg font-semibold text-gray-900">Localization Settings</h2>
          <div className="grid grid-cols-2 gap-4">
            <div>
              <label htmlFor="loc-language" className="block text-xs font-medium text-gray-700 mb-1">Default Language</label>
              <select
                id="loc-language"
                value={localization.defaultLanguage}
                onChange={e => setLocalization(p => p ? { ...p, defaultLanguage: e.target.value } : p)}
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
                onChange={e => setLocalization(p => p ? { ...p, calendarSystem: e.target.value } : p)}
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
                onChange={e => setLocalization(p => p ? { ...p, countryCode: e.target.value } : p)}
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
                onChange={e => setLocalization(p => p ? { ...p, currencyCode: e.target.value } : p)}
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
                onChange={e => setLocalization(p => p ? { ...p, workWeek: e.target.value } : p)}
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
                onChange={e => setLocalization(p => p ? { ...p, defaultTimezone: e.target.value } : p)}
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
                onChange={v => setLocalization(p => p ? { ...p, rtlEnabled: v } : p)}
              />
              <label htmlFor="loc-rtl" className="text-sm text-gray-700 cursor-pointer">RTL Layout</label>
            </div>
            <div className="flex items-center gap-2">
              <Toggle
                id="loc-hijri"
                checked={localization.hijriDatesEnabled}
                onChange={v => setLocalization(p => p ? { ...p, hijriDatesEnabled: v } : p)}
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
              {saving ? 'Saving...' : 'Save Settings'}
            </button>
            {saved && <span className="text-sm text-green-600 font-medium">Saved!</span>}
          </div>
        </div>
      )}

      {/* Branding */}
      {tab === 'branding' && (
        <div className="bg-white rounded-xl border border-gray-200 p-6 space-y-4">
          <h2 className="text-lg font-semibold text-gray-900">Branding</h2>
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
                      onChange={e => setBranding(p => p ? { ...p, [field.key]: e.target.value } : p)}
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
                      onChange={e => setBranding(p => p ? { ...p, primaryColor: e.target.value } : p)}
                      className="h-9 w-16 border border-gray-200 rounded cursor-pointer"
                    />
                    <input
                      id="brand-primaryColor-text"
                      value={branding.primaryColor}
                      onChange={e => setBranding(p => p ? { ...p, primaryColor: e.target.value } : p)}
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
                  {saving ? 'Saving...' : 'Save Branding'}
                </button>
                {saved && <span className="text-sm text-green-600 font-medium">Saved!</span>}
              </div>
            </>
          )}
          {!branding && (
            <div className="text-center py-8 text-sm text-gray-400">
              <p>No branding configured. System defaults are active.</p>
              <button
                type="button"
                onClick={() => setBranding({ logoUrl: '', primaryColor: '#2563EB', accentColor: '#7C3AED', companyNameEn: '', companyNameAr: '', portalTitle: 'HR Portal', faviconUrl: '' })}
                className="mt-3 px-4 py-2 bg-indigo-600 text-white rounded-lg text-sm"
              >
                Configure Branding
              </button>
            </div>
          )}
        </div>
      )}

      {/* Security */}
      {tab === 'security' && (
        <div className="bg-white rounded-xl border border-gray-200 p-6 space-y-5">
          <h2 className="text-lg font-semibold text-gray-900">Security Settings</h2>
          {!security ? (
            <p className="text-sm text-gray-500">Loading security settings...</p>
          ) : (
            <>
              <div className="space-y-4">
                <h3 className="text-sm font-semibold text-gray-700 border-b border-gray-100 pb-2">Password Policy</h3>
                <div className="grid grid-cols-2 gap-4">
                  <div>
                    <label htmlFor="sec-minlen" className="block text-xs font-medium text-gray-700 mb-1">
                      Minimum Length <span className="text-gray-400">(6–32)</span>
                    </label>
                    <input
                      id="sec-minlen"
                      type="number"
                      min={6}
                      max={32}
                      value={security.passwordMinLength}
                      onChange={e => setSecurity(p => p ? { ...p, passwordMinLength: Number(e.target.value) } : p)}
                      className="w-full border border-gray-200 rounded-lg px-3 py-2 text-sm"
                    />
                  </div>
                </div>

                <div className="flex flex-wrap gap-6">
                  {[
                    { key: 'passwordRequireUppercase', label: 'Require Uppercase Letter', id: 'sec-upper' },
                    { key: 'passwordRequireDigit', label: 'Require Digit', id: 'sec-digit' },
                    { key: 'passwordRequireSpecialChar', label: 'Require Special Character', id: 'sec-special' },
                  ].map(opt => (
                    <div key={opt.key} className="flex items-center gap-2">
                      <Toggle
                        id={opt.id}
                        checked={security[opt.key as keyof SecuritySettings] as boolean}
                        onChange={v => setSecurity(p => p ? { ...p, [opt.key]: v } : p)}
                      />
                      <label htmlFor={opt.id} className="text-sm text-gray-700 cursor-pointer">{opt.label}</label>
                    </div>
                  ))}
                </div>
              </div>

              <div className="space-y-4">
                <h3 className="text-sm font-semibold text-gray-700 border-b border-gray-100 pb-2">Session &amp; Lockout</h3>
                <div className="grid grid-cols-3 gap-4">
                  <div>
                    <label htmlFor="sec-timeout" className="block text-xs font-medium text-gray-700 mb-1">Session Timeout (min)</label>
                    <input
                      id="sec-timeout"
                      type="number"
                      min={1}
                      value={security.sessionTimeoutMinutes}
                      onChange={e => setSecurity(p => p ? { ...p, sessionTimeoutMinutes: Number(e.target.value) } : p)}
                      className="w-full border border-gray-200 rounded-lg px-3 py-2 text-sm"
                    />
                  </div>
                  <div>
                    <label htmlFor="sec-maxfail" className="block text-xs font-medium text-gray-700 mb-1">
                      Max Failed Attempts <span className="text-gray-400">(1–20)</span>
                    </label>
                    <input
                      id="sec-maxfail"
                      type="number"
                      min={1}
                      max={20}
                      value={security.maxFailedLoginAttempts}
                      onChange={e => setSecurity(p => p ? { ...p, maxFailedLoginAttempts: Number(e.target.value) } : p)}
                      className="w-full border border-gray-200 rounded-lg px-3 py-2 text-sm"
                    />
                  </div>
                  <div>
                    <label htmlFor="sec-lockout" className="block text-xs font-medium text-gray-700 mb-1">Lockout Duration (min)</label>
                    <input
                      id="sec-lockout"
                      type="number"
                      min={1}
                      value={security.lockoutDurationMinutes}
                      onChange={e => setSecurity(p => p ? { ...p, lockoutDurationMinutes: Number(e.target.value) } : p)}
                      className="w-full border border-gray-200 rounded-lg px-3 py-2 text-sm"
                    />
                  </div>
                </div>
              </div>

              {securityError && (
                <p className="text-sm text-red-600 bg-red-50 border border-red-100 rounded-lg px-3 py-2">{securityError}</p>
              )}

              <div className="flex items-center gap-3 pt-1">
                <button
                  type="button"
                  onClick={saveSecurity}
                  disabled={securitySaving}
                  className="px-6 py-2.5 bg-indigo-600 text-white rounded-lg text-sm font-medium disabled:opacity-50 hover:bg-indigo-700"
                >
                  {securitySaving ? 'Saving...' : 'Save Settings'}
                </button>
                {securitySaved && <span className="text-sm text-green-600 font-medium">Saved!</span>}
              </div>
            </>
          )}
        </div>
      )}

      {/* Country Rules */}
      {tab === 'country-rules' && (
        <div className="space-y-4">
          <div className="flex items-center justify-between">
            <h2 className="text-lg font-semibold text-gray-900">Country Rules</h2>
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
                      {getCountryName(code)} <span className="text-gray-400 font-normal ml-1">({code})</span>
                    </p>
                  </div>
                  <table className="w-full text-sm">
                    <thead>
                      <tr className="border-b border-gray-100">
                        {['Rule Key', 'Rule Value', 'Type', 'Override', 'Eff. From', 'Eff. To', 'Actions'].map(col => (
                          <th key={col} className="text-left text-xs font-medium text-gray-500 px-4 py-2">{col}</th>
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
        </div>
      )}

      {/* Statutory Rules Engine */}
      {tab === 'statutory-rules' && (
        <div className="space-y-4">
          <div className="flex items-center justify-between">
            <div>
              <h2 className="text-lg font-semibold text-gray-900">Statutory Rules Engine</h2>
              <p className="text-xs text-gray-500 mt-0.5">
                Effective-dated rates for GOSI, GPSSA, GRSIA, EOSB, and WPS thresholds.
                Sample defaults are pre-loaded for reference (marked VERIFY). Create tenant overrides to apply your own certified rates for your establishment.
              </p>
            </div>
            <button
              type="button"
              onClick={() => setShowAddStatRule(v => !v)}
              className="px-4 py-2 bg-indigo-600 text-white rounded-lg text-sm font-medium hover:bg-indigo-700"
            >
              {showAddStatRule ? 'Cancel' : '+ Add Override'}
            </button>
          </div>

          {showAddStatRule && (
            <div className="bg-white rounded-xl border border-gray-200 p-5 space-y-4">
              <h3 className="text-sm font-semibold text-gray-900">New Tenant Override</h3>
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label htmlFor="sr-country" className="block text-xs font-medium text-gray-700 mb-1">Country Pack</label>
                  <select
                    id="sr-country"
                    aria-label="Country Pack"
                    value={newStatRule.countryCode}
                    onChange={e => {
                      const cc = e.target.value;
                      const firstJ = availablePacks.find(p => p.countryCode === cc)?.jurisdictions[0]?.code ?? '';
                      setNewStatRule(p => ({ ...p, countryCode: cc, jurisdiction: firstJ }));
                    }}
                    className="w-full border border-gray-200 rounded-lg px-3 py-2 text-sm"
                  >
                    {availablePacks.map(p => <option key={p.countryCode} value={p.countryCode}>{p.nameEn}</option>)}
                  </select>
                </div>
                <div>
                  <label htmlFor="sr-jurisdiction" className="block text-xs font-medium text-gray-700 mb-1">Jurisdiction</label>
                  <select
                    id="sr-jurisdiction"
                    aria-label="Jurisdiction"
                    value={newStatRule.jurisdiction}
                    onChange={e => setNewStatRule(p => ({ ...p, jurisdiction: e.target.value }))}
                    className="w-full border border-gray-200 rounded-lg px-3 py-2 text-sm"
                  >
                    {(availablePacks.find(p => p.countryCode === newStatRule.countryCode)?.jurisdictions ?? []).map(j => (
                      <option key={j.code} value={j.code}>{j.label}</option>
                    ))}
                  </select>
                </div>
                <div>
                  <label htmlFor="sr-key" className="block text-xs font-medium text-gray-700 mb-1">Rule Key</label>
                  <input
                    id="sr-key"
                    value={newStatRule.ruleKey}
                    onChange={e => setNewStatRule(p => ({ ...p, ruleKey: e.target.value }))}
                    placeholder="e.g. gosi.saudi_employee_rate"
                    aria-label="Rule Key"
                    className="w-full border border-gray-200 rounded-lg px-3 py-2 text-sm"
                  />
                </div>
                <div>
                  <label htmlFor="sr-value" className="block text-xs font-medium text-gray-700 mb-1">Rule Value</label>
                  <input
                    id="sr-value"
                    value={newStatRule.ruleValue}
                    onChange={e => setNewStatRule(p => ({ ...p, ruleValue: e.target.value }))}
                    placeholder="e.g. 0.09"
                    aria-label="Rule Value"
                    className="w-full border border-gray-200 rounded-lg px-3 py-2 text-sm"
                  />
                </div>
                <div>
                  <label htmlFor="sr-datatype" className="block text-xs font-medium text-gray-700 mb-1">Data Type</label>
                  <select
                    id="sr-datatype"
                    aria-label="Data Type"
                    value={newStatRule.dataType}
                    onChange={e => setNewStatRule(p => ({ ...p, dataType: e.target.value }))}
                    className="w-full border border-gray-200 rounded-lg px-3 py-2 text-sm"
                  >
                    <option value="decimal">Decimal</option>
                    <option value="string">String</option>
                    <option value="int">Integer</option>
                    <option value="bool">Boolean</option>
                  </select>
                </div>
                <div>
                  <label htmlFor="sr-effdate" className="block text-xs font-medium text-gray-700 mb-1">Effective From</label>
                  <input
                    id="sr-effdate"
                    type="date"
                    aria-label="Effective From"
                    value={newStatRule.effectiveFrom}
                    onChange={e => setNewStatRule(p => ({ ...p, effectiveFrom: e.target.value }))}
                    className="w-full border border-gray-200 rounded-lg px-3 py-2 text-sm"
                  />
                </div>
                <div className="col-span-2">
                  <label className="block text-xs font-medium text-gray-700 mb-1">Description / Source</label>
                  <input
                    value={newStatRule.description}
                    onChange={e => setNewStatRule(p => ({ ...p, description: e.target.value }))}
                    placeholder="e.g. Source: GOSI Royal Decree M/33 2016 — verified 2026-01"
                    className="w-full border border-gray-200 rounded-lg px-3 py-2 text-sm"
                  />
                </div>
              </div>
              <button
                type="button"
                onClick={addStatRule}
                disabled={addingStatRule || !newStatRule.ruleKey.trim() || !newStatRule.ruleValue.trim()}
                className="px-5 py-2 bg-indigo-600 text-white rounded-lg text-sm font-medium disabled:opacity-50 hover:bg-indigo-700"
              >
                {addingStatRule ? 'Adding…' : 'Add Override'}
              </button>
            </div>
          )}

          {statRulesLoading ? (
            <div className="bg-white rounded-xl border border-gray-200 p-8 text-center text-sm text-gray-400">Loading…</div>
          ) : statRules.length === 0 ? (
            <div className="bg-white rounded-xl border border-gray-200 p-8 text-center text-sm text-gray-400">
              No statutory rules loaded. Sample defaults will appear here — check that the seeder ran on startup.
            </div>
          ) : (
            <div className="overflow-hidden rounded-xl border border-gray-200 bg-white">
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b border-gray-100 bg-gray-50">
                    {['Country', 'Jurisdiction', 'Rule Key', 'Value', 'Effective From', 'Source / Note', 'Override', 'Actions'].map(col => (
                      <th key={col} className="text-left text-xs font-medium text-gray-500 px-4 py-2">{col}</th>
                    ))}
                  </tr>
                </thead>
                <tbody className="divide-y divide-gray-50">
                  {statRules.map(rule => (
                    <tr key={rule.id} className={`hover:bg-gray-50 transition-colors ${!rule.isTenantOverride ? 'opacity-80' : ''}`}>
                      <td className="px-4 py-2.5 text-xs font-medium text-gray-700">{rule.countryCode}</td>
                      <td className="px-4 py-2.5 text-xs text-gray-500">{rule.jurisdiction || '—'}</td>
                      <td className="px-4 py-2.5 font-mono text-xs text-gray-800">{rule.ruleKey}</td>
                      <td className="px-4 py-2.5 font-mono text-xs font-semibold text-indigo-700">{rule.ruleValue}</td>
                      <td className="px-4 py-2.5 text-xs text-gray-400">{rule.effectiveFrom ? new Date(rule.effectiveFrom).toLocaleDateString() : '—'}</td>
                      <td className="px-4 py-2.5 text-xs text-gray-400 max-w-xs truncate" title={rule.description}>{rule.description || '—'}</td>
                      <td className="px-4 py-2.5">
                        {rule.isTenantOverride ? (
                          <span className="inline-block px-1.5 py-0.5 bg-amber-100 text-amber-700 rounded text-xs">Override</span>
                        ) : (
                          <span className="inline-block px-1.5 py-0.5 bg-amber-50 text-amber-700 border border-amber-200 rounded text-xs">Sample default — configure for your establishment</span>
                        )}
                      </td>
                      <td className="px-4 py-2.5">
                        {rule.isTenantOverride ? (
                          <button
                            type="button"
                            onClick={() => deleteStatRule(rule.id)}
                            className="text-xs text-red-500 hover:text-red-700"
                          >
                            Delete
                          </button>
                        ) : (
                          <span className="text-xs text-gray-300">Read-only</span>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}

      {/* Custom Field Help Text */}
      {tab === 'help-text' && <HelpTextManager />}
    </div>
  );
}
