'use client';

import { useState, useEffect } from 'react';
import {
  tenantAdminApi,
  tenantModulesApi,
  type TenantModuleList,
  type TenantLocalizationSetting,
  type TenantBranding,
  type TenantAiUsageSummary,
} from '../api/intelligence';
import client from '../api/client';
import { HelpTextManager } from '../components/HelpTextManager';
import { StatutoryRulesPanel } from '../components/StatutoryRulesPanel';
import { useTenantSettingsContext } from '../contexts/TenantSettingsContext';
import { useFeatureFlags } from '../contexts/FeatureFlagContext';
import { useLocale } from '../contexts/LocaleContext';
import { LicencePanel } from '../components/LicencePanel';
import { InstallationPanel } from '../components/InstallationPanel';

type Tab = 'licence' | 'usage' | 'modules' | 'localization' | 'branding' | 'security' | 'country-rules' | 'statutory-rules' | 'help-text';

// The module list is no longer hard-coded here. It used to be an eight-key array that named
// mostly sub-features with no runtime reader (resume_screening, risk_scores, payroll_ai_validation,
// eosb_calc, hijri_calendar), omitted every module a client would actually want to switch off,
// and wrote to an endpoint that returned 403 unconditionally — so none of the toggles did
// anything. The list now comes from the backend catalog, which is the same source the API guard,
// the navigation and the route guard enforce.

/**
 * Opt-in features: OFF until the organisation switches them on (an absent flag means off). Selfie
 * attendance and the punch geofence also need a configuration — the server refuses to switch them on
 * without one and says what is missing.
 */
const OPT_IN_FEATURES: { key: string; label: string }[] = [
  { key: 'learning', label: 'Learning' },
  { key: 'succession', label: 'Succession & development' },
  { key: 'succession_candidate_visibility', label: 'Succession: employees see their pools' },
  { key: 'engagement', label: 'Engagement surveys' },
  { key: 'compensation', label: 'Merit planning & pay equity' },
  { key: 'release_a', label: 'Entitlements & renewals' },
  { key: 'selfie_attendance', label: 'Selfie attendance (needs DPIA sign-off and KSA storage)' },
  { key: 'attendance_geofence', label: 'Punch geofence (needs a geofence configuration)' },
  { key: 'policy_intake', label: 'Policy intake (upload & parse)' },
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

interface SecuritySettings {
  passwordMinLength: number;
  passwordRequireUppercase: boolean;
  passwordRequireDigit: boolean;
  passwordRequireSpecial: boolean;
  /** The idle timeout: a session nobody has used for this many minutes ends. */
  sessionTimeoutMinutes: number;
  /** The hard limit: every session ends this many hours after sign-in, active or not. 1–168; 12 by default. */
  sessionMaxHours?: number;
  maxFailedLoginAttempts: number;
  lockoutDurationMinutes: number;
  /** Two-step sign-in for everyone, on top of the people who can see or change payroll, banking or access. */
  mfaRequired?: boolean;
  /** How long a trusted browser skips the code: 0 (off) to 90 days; 30 by default. */
  mfaTrustedDeviceDays?: number;
}

/** The trusted-browser lifetimes offered (decision 3); 0 = off. A stored value outside these is shown as well. */
const TRUST_DAY_CHOICES = [0, 7, 14, 30, 90] as const;
const DEFAULT_TRUST_DAYS = 30;

/** The maximum session lengths offered, in hours. A stored value outside these is shown as well. */
const SESSION_MAX_HOUR_CHOICES = [4, 8, 12, 24, 72, 168] as const;
const DEFAULT_SESSION_MAX_HOURS = 12;

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
        // Knob travel is a transform, which does not mirror on its own — a switch that
        // slides off the track in Arabic is worse than one that does not mirror at all.
        checked ? 'translate-x-6 rtl:-translate-x-6' : 'translate-x-1 rtl:-translate-x-1'
      }`} />
    </button>
  );
}

/**
 * Two-step sign-in in the Security Settings form: who must use it, and how long a trusted browser
 * skips the code. Writes `mfaRequired` and `mfaTrustedDeviceDays` of PUT /api/access/security-settings
 * with the rest of the form. People who can see or change payroll, banking or access always need it
 * (the server's privileged-role rule), so that line is a fact, not a switch.
 */
function TwoStepPolicyGroup({ security, onChange }: {
  security: SecuritySettings;
  onChange: (patch: Partial<SecuritySettings>) => void;
}) {
  const { t } = useLocale();
  const days = Number.isInteger(security.mfaTrustedDeviceDays) ? (security.mfaTrustedDeviceDays as number) : DEFAULT_TRUST_DAYS;
  const choices: number[] = (TRUST_DAY_CHOICES as readonly number[]).includes(days) ? [...TRUST_DAY_CHOICES] : [...TRUST_DAY_CHOICES, days].sort((a, b) => a - b);
  const label = (d: number) => {
    if (d === 0) return t('Off');
    if (d === 7) return t('7 days');
    if (d === 14) return t('14 days');
    if (d === DEFAULT_TRUST_DAYS) return t('30 days (default)');
    if (d === 90) return t('90 days');
    return t('{days, plural, one {# day (current)} other {# days (current)}}', { days: d });
  };
  return (
    <section className="space-y-4" aria-labelledby="sec-two-step-heading" data-testid="two-step-policy">
      <h3 id="sec-two-step-heading" className="text-sm font-semibold text-gray-700 border-b border-gray-100 pb-2">{t('Two-step sign-in')}</h3>

      <fieldset className="space-y-3">
        <legend className="text-xs font-medium text-gray-700 mb-1 rtl:text-[13px]">{t('Who must use two-step sign-in')}</legend>
        <div className="flex items-start justify-between gap-3 rounded-lg border border-gray-200 bg-gray-50 px-3 py-2.5" data-testid="two-step-always">
          <p className="text-sm text-gray-700">{t('People who can see or change payroll, banking or access')}</p>
          <span className="shrink-0 rounded-full bg-gray-200 px-2 py-0.5 text-xs font-semibold text-gray-700 rtl:text-[13px]">{t('Always required')}</span>
        </div>
        <div className="flex items-start gap-3 px-1">
          <Toggle id="sec-mfa-everyone" checked={security.mfaRequired === true}
            onChange={(v) => onChange({ mfaRequired: v })} />
          <div className="min-w-0">
            <label htmlFor="sec-mfa-everyone" className="text-sm text-gray-700 cursor-pointer">{t('Also require it for everyone else')}</label>
            <p id="sec-mfa-everyone-hint" className="mt-0.5 text-xs leading-5 text-gray-500 rtl:text-[13px]">
              {t("When you turn this on and save, everyone who hasn't set it up yet is signed out, and sets it up the next time they sign in.")}
            </p>
          </div>
        </div>
      </fieldset>

      <div className="max-w-xs">
        <label htmlFor="sec-trust-days" className="block text-xs font-medium text-gray-700 mb-1 rtl:text-[13px]">{t('Trusted browsers')}</label>
        <select id="sec-trust-days" value={days} aria-describedby="sec-trust-days-hint"
          onChange={(e) => onChange({ mfaTrustedDeviceDays: Number(e.target.value) })}
          className="w-full border border-gray-200 rounded-lg px-3 py-2 text-sm">
          {choices.map((d) => <option key={d} value={d}>{label(d)}</option>)}
        </select>
        <p id="sec-trust-days-hint" className="mt-1 text-xs leading-5 text-gray-500 rtl:text-[13px]">
          {t('Shorter is safer. Shortening it also shortens browsers already trusted.')}
        </p>
      </div>
    </section>
  );
}

/**
 * Sessions and lockout in the Security Settings form. Two limits end a session: inactivity
 * (`sessionTimeoutMinutes`, the idle timeout) and the maximum session length (`sessionMaxHours`,
 * counted from sign-in, active or not). Both are written by PUT /api/access/security-settings with
 * the rest of the form; a shorter maximum applies to sessions already open at their next refresh.
 */
function SessionLimitsGroup({ security, onChange }: {
  security: SecuritySettings;
  onChange: (patch: Partial<SecuritySettings>) => void;
}) {
  const { t } = useLocale();
  const hours = Number.isInteger(security.sessionMaxHours) ? (security.sessionMaxHours as number) : DEFAULT_SESSION_MAX_HOURS;
  const choices: number[] = (SESSION_MAX_HOUR_CHOICES as readonly number[]).includes(hours)
    ? [...SESSION_MAX_HOUR_CHOICES]
    : [...SESSION_MAX_HOUR_CHOICES, hours].sort((a, b) => a - b);
  const label = (h: number) => {
    if (h === 4) return t('4 hours');
    if (h === 8) return t('8 hours');
    if (h === DEFAULT_SESSION_MAX_HOURS) return t('12 hours (default)');
    if (h === 24) return t('24 hours');
    if (h === 72) return t('72 hours (3 days)');
    if (h === 168) return t('168 hours (7 days)');
    return t('{hours, plural, one {# hour (current)} other {# hours (current)}}', { hours: h });
  };
  return (
    <section className="space-y-4" aria-labelledby="sec-sessions-heading" data-testid="session-limits">
      <h3 id="sec-sessions-heading" className="text-sm font-semibold text-gray-700 border-b border-gray-100 pb-2">{t('Sessions and lockout')}</h3>
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
        <div>
          <label htmlFor="sec-timeout" className="block text-xs font-medium text-gray-700 mb-1 rtl:text-[13px]">
            {t('Sign out after inactivity')} <span className="text-gray-400">{t('(minutes)')}</span>
          </label>
          <input
            id="sec-timeout"
            type="number"
            min={1}
            value={security.sessionTimeoutMinutes}
            aria-describedby="sec-timeout-hint"
            onChange={e => onChange({ sessionTimeoutMinutes: Number(e.target.value) })}
            className="w-full border border-gray-200 rounded-lg px-3 py-2 text-sm"
          />
          <p id="sec-timeout-hint" className="mt-1 text-xs leading-5 text-gray-500 rtl:text-[13px]">
            {t('Ends a session nobody has used for this long.')}
          </p>
        </div>
        <div>
          <label htmlFor="sec-max-hours" className="block text-xs font-medium text-gray-700 mb-1 rtl:text-[13px]">{t('Maximum session length')}</label>
          <select id="sec-max-hours" value={hours} aria-describedby="sec-max-hours-hint"
            onChange={(e) => onChange({ sessionMaxHours: Number(e.target.value) })}
            className="w-full border border-gray-200 rounded-lg px-3 py-2 text-sm">
            {choices.map((h) => <option key={h} value={h}>{label(h)}</option>)}
          </select>
          <p id="sec-max-hours-hint" className="mt-1 text-xs leading-5 text-gray-500 rtl:text-[13px]">
            {t('Everyone signs in again after this long, even if they are active. Shorter is safer.')}
          </p>
        </div>
        <div>
          <label htmlFor="sec-maxfail" className="block text-xs font-medium text-gray-700 mb-1 rtl:text-[13px]">
            {t('Maximum failed attempts')} <bdi dir="ltr" className="text-gray-400">(1–20)</bdi>
          </label>
          <input
            id="sec-maxfail"
            type="number"
            min={1}
            max={20}
            value={security.maxFailedLoginAttempts}
            onChange={e => onChange({ maxFailedLoginAttempts: Number(e.target.value) })}
            className="w-full border border-gray-200 rounded-lg px-3 py-2 text-sm"
          />
        </div>
        <div>
          <label htmlFor="sec-lockout" className="block text-xs font-medium text-gray-700 mb-1 rtl:text-[13px]">
            {t('Lockout duration')} <span className="text-gray-400">{t('(minutes)')}</span>
          </label>
          <input
            id="sec-lockout"
            type="number"
            min={1}
            value={security.lockoutDurationMinutes}
            onChange={e => onChange({ lockoutDurationMinutes: Number(e.target.value) })}
            className="w-full border border-gray-200 rounded-lg px-3 py-2 text-sm"
          />
        </div>
      </div>
    </section>
  );
}

export default function TenantAdminPage() {
  const { t } = useLocale();
  // Global providers loaded once on mount — re-sync them after a save so the rest of the
  // app (sidebar nav, currency formatting, etc.) reflects the change without a full reload.
  const { reload: reloadTenantSettings } = useTenantSettingsContext();
  const { refresh: refreshFeatureFlags, isFeatureEnabled } = useFeatureFlags();
  const [featureBusy, setFeatureBusy] = useState<string | null>(null);
  const [featureError, setFeatureError] = useState<string | null>(null);
  // ?tab=licence is where a 402 (no usable licence) sends people; the licence is also the first thing shown.
  const [tab, setTab] = useState<Tab>(() => {
    if (typeof window === 'undefined') return 'licence';
    const requested = new URLSearchParams(window.location.search).get('tab');
    return requested === 'usage' || requested === 'modules' ? requested : 'licence';
  });
  const [modules, setModules] = useState<TenantModuleList | null>(null);
  const [modulesLoading, setModulesLoading] = useState(false);
  const [modulesError, setModulesError] = useState<string | null>(null);
  const [moduleBusy, setModuleBusy] = useState<string | null>(null);
  const [localization, setLocalization] = useState<TenantLocalizationSetting | null>(null);
  const [branding, setBranding] = useState<TenantBranding | null>(null);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);

  // Usage
  const [usage, setUsage] = useState<UsageData | null>(null);

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

  useEffect(() => {
    if (tab === 'usage') { loadUsage(); loadAiUsage(); }
    if (tab === 'modules') loadModules();
    if (tab === 'localization') loadLocalization();
    if (tab === 'branding') loadBranding();
    if (tab === 'security') loadSecurity();
    if (tab === 'country-rules') loadRules();
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tab]);

  async function loadUsage() {
    try {
      const data = await client.get<UsageData>('/api/tenant-admin/usage').then(r => r.data);
      setUsage(data);
    } catch {}
  }

  async function loadAiUsage() {
    try { setAiUsage(await tenantAdminApi.getAiUsage()); } catch {}
  }

  async function loadModules() {
    setModulesLoading(true);
    setModulesError(null);
    try {
      setModules(await tenantModulesApi.list());
    } catch {
      setModulesError('Could not load your modules. Refresh to try again.');
    } finally {
      setModulesLoading(false);
    }
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

  async function toggleModule(key: string, current: boolean) {
    setModuleBusy(key);
    setModulesError(null);
    try {
      const updated = await tenantModulesApi.set(key, !current);
      setModules(prev => prev && {
        ...prev,
        modules: prev.modules.map(m => (m.key === key ? updated : m)),
      });
      // The sidebar, the route guard and the dashboard all read module state from
      // FeatureFlagContext — re-sync it so the change lands immediately rather than after a
      // browser refresh. Previously this call was the only visible effect of a toggle, because
      // the write itself always failed and the error was swallowed.
      await refreshFeatureFlags();
    } catch (err: unknown) {
      // The API refuses deliberately and says why; surface that rather than failing silently.
      const detail = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setModulesError(detail ?? 'Could not change that module. Please try again.');
      // Re-read so the UI reflects the server's actual state after a refusal.
      try { setModules(await tenantModulesApi.list()); } catch {}
    } finally {
      setModuleBusy(null);
    }
  }

  async function toggleFeature(key: string, current: boolean) {
    setFeatureBusy(key);
    setFeatureError(null);
    try {
      await tenantAdminApi.setFeature(key, !current);
      await refreshFeatureFlags();
    } catch (err: unknown) {
      // The API refuses deliberately and says why (e.g. a missing DPIA sign-off); surface that.
      const detail = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setFeatureError(detail ?? 'Could not change that feature. Please try again.');
    } finally {
      setFeatureBusy(null);
    }
  }

  async function saveLocalization() {
    if (!localization) return;
    setSaving(true);
    try {
      const updated = await tenantAdminApi.upsertLocalization(localization);
      setLocalization(updated);
      // Currency/date/timezone formatting across the app reads from TenantSettingsContext —
      // re-sync it so the new settings apply immediately instead of after a browser refresh.
      await reloadTenantSettings();
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
    } catch (err) {
      const code = (err as { response?: { data?: { code?: unknown } } } | null)?.response?.data?.code;
      setSecurityError(code === 'ERR_SESSION_MAX_HOURS_RANGE'
        ? t('Maximum session length must be between 1 and 168 hours.')
        : 'Failed to save security settings. Please try again.');
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
    { id: 'licence', label: 'Licence' },
    { id: 'usage', label: 'Usage' },
    { id: 'modules', label: 'Modules' },
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
        <h1 className="text-2xl font-bold text-gray-900">Organisation Administration</h1>
        <p className="text-sm text-gray-500">Manage the licence, modules, localization, and branding</p>
      </div>

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

      {/* Licence */}
      {tab === 'licence' && (<><LicencePanel /><InstallationPanel /></>)}

      {/* Usage */}
      {tab === 'usage' && (
        <div className="space-y-4">
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

              </div>
            </div>
          )}

          {/* AI Usage section */}
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
              </div>
            </div>
          )}
        </div>
      )}

      {/* Modules */}
      {tab === 'modules' && (
        <div className="space-y-4">
          <div className="rounded-xl border border-gray-200 bg-white p-5 dark:border-gray-700 dark:bg-gray-900">
            <h2 className="text-base font-semibold text-gray-900 dark:text-gray-100">Modules</h2>
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
                            {m.licensed === false && (
                              <span className="rounded bg-amber-50 px-1.5 py-0.5 text-[11px] font-medium text-amber-800">
                                Not in your licence
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
                          {m.licensed === false ? (
                            // Unlicensed: off, and switching it on is refused (module_not_licensed).
                            <button
                              type="button"
                              role="switch"
                              aria-checked="false"
                              aria-label={`${m.labelEn} is not in your licence`}
                              disabled
                              title="Not included in this installation's licence"
                              className="relative inline-flex h-6 w-11 shrink-0 cursor-not-allowed items-center rounded-full bg-gray-200 opacity-60"
                            >
                              <span className="inline-block h-4 w-4 translate-x-1 transform rounded-full bg-white shadow rtl:-translate-x-1" />
                            </button>
                          ) : m.canDisable ? (
                            <Toggle
                              id={`module-${m.key}`}
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

          <section>
            <div className="mb-2 px-1">
              <h3 className="text-sm font-semibold text-gray-900 dark:text-gray-100">Optional features</h3>
              <p className="text-xs text-gray-500 dark:text-gray-400">Off until you switch them on.</p>
            </div>
            {featureError && (
              <div
                role="alert"
                className="mb-2 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700 dark:border-red-900 dark:bg-red-950 dark:text-red-300"
              >
                {featureError}
              </div>
            )}
            <div className="divide-y divide-gray-100 rounded-xl border border-gray-200 bg-white dark:divide-gray-800 dark:border-gray-700 dark:bg-gray-900">
              {OPT_IN_FEATURES.map(f => {
                const enabled = isFeatureEnabled(f.key);
                return (
                  <div key={f.key} className="flex items-center justify-between gap-3 px-5 py-4">
                    <p className="text-sm font-medium text-gray-900 dark:text-gray-100">{f.label}</p>
                    <div className="flex shrink-0 items-center gap-2">
                      {featureBusy === f.key && <span className="text-xs text-gray-400">Saving…</span>}
                      <Toggle id={`feature-${f.key}`} checked={enabled} onChange={() => toggleFeature(f.key, enabled)} />
                    </div>
                  </div>
                );
              })}
            </div>
          </section>
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
                    // The API field is passwordRequireSpecial; this form used to send passwordRequireSpecialChar, which the
                    // server ignored, so the setting never saved from here.
                    { key: 'passwordRequireSpecial', label: 'Require Special Character', id: 'sec-special' },
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

              <SessionLimitsGroup security={security} onChange={(patch) => setSecurity(p => p ? { ...p, ...patch } : p)} />

              <TwoStepPolicyGroup security={security} onChange={(patch) => setSecurity(p => p ? { ...p, ...patch } : p)} />

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
        </div>
      )}

      {/* Legacy entry retained as an alias; Setup & Administration is the canonical home. */}
      {tab === 'statutory-rules' && (
        <div className="space-y-4">
          <div className="rounded-xl border border-blue-200 bg-blue-50 px-4 py-3 text-sm text-blue-900">
            Statutory Rules now lives in <a href="/setup?tab=statutoryRules" className="font-semibold underline">Setup &amp; Administration</a>. This alias uses the same governed configuration panel.
          </div>
          <StatutoryRulesPanel />
        </div>
      )}

      {/* Custom Field Help Text */}
      {tab === 'help-text' && <HelpTextManager />}
    </div>
  );
}
