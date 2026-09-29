'use client';

import { useState, useCallback, useEffect } from 'react';
import Link from 'next/link';
import {
  Building2, Users, Globe, Languages, ChevronRight, ChevronLeft,
  Check, Sparkles, ArrowRight, RefreshCw, AlertCircle, Info,
} from 'lucide-react';
import { pricingApi, type PricingModule, type PricingEstimate, type OrgType } from '@/src/api/pricing';

// ── Constants ─────────────────────────────────────────────────────────────────

const ORG_TYPES: { value: OrgType; label: string; description: string; icon: typeof Building2 }[] = [
  { value: 'single',             label: 'Single Company',        description: 'One legal entity, one or more branches',                         icon: Building2 },
  { value: 'group',              label: 'Group of Companies',    description: 'Multiple legal entities under common management',                  icon: Users },
  { value: 'enterprise_holding', label: 'Enterprise Holding',    description: 'Holding company with subsidiaries across multiple countries',       icon: Globe },
];

const PLAN_BADGE: Record<string, string> = {
  Trial:      'bg-slate-700/60 text-slate-300',
  Starter:    'bg-blue-900/60 text-blue-300',
  Growth:     'bg-purple-900/60 text-purple-300',
  Enterprise: 'bg-amber-900/60 text-amber-300',
};

const fmt = (n: number) => n.toLocaleString('en-US', { style: 'currency', currency: 'USD', maximumFractionDigits: 0 });

// ── Wizard state ──────────────────────────────────────────────────────────────

interface WizardState {
  orgType: OrgType;
  numCompanies: number;
  numBranches: number;
  numEmployees: number;
  numAdminUsers: number;
  numCountries: number;
  needsArabic: boolean;
  selectedModules: string[];
}

const DEFAULT_STATE: WizardState = {
  orgType: 'single',
  numCompanies: 1,
  numBranches: 1,
  numEmployees: 25,
  numAdminUsers: 3,
  numCountries: 1,
  needsArabic: false,
  selectedModules: ['core_hr', 'leave_attendance'],
};

// ── Step components ───────────────────────────────────────────────────────────

function Step1OrgType({ state, update }: { state: WizardState; update: (p: Partial<WizardState>) => void }) {
  return (
    <div className="space-y-4">
      <div>
        <h2 className="text-lg font-semibold text-white">What describes your organization?</h2>
        <p className="text-sm text-slate-400 mt-1">This helps us recommend the right plan tier.</p>
      </div>
      <div className="grid gap-3">
        {ORG_TYPES.map(opt => {
          const Icon = opt.icon;
          const active = state.orgType === opt.value;
          return (
            <button
              key={opt.value}
              type="button"
              onClick={() => {
                update({ orgType: opt.value });
                if (opt.value === 'single') update({ numCompanies: 1 });
                if (opt.value === 'enterprise_holding') update({ numCompanies: Math.max(state.numCompanies, 5) });
              }}
              className={`flex items-start gap-4 px-5 py-4 rounded-xl border text-start transition-all ${
                active
                  ? 'bg-sapphire/10 border-sapphire/50 ring-1 ring-sapphire/30'
                  : 'bg-white/[0.03] border-white/10 hover:border-white/20'
              }`}
            >
              <div className={`mt-0.5 flex h-9 w-9 shrink-0 items-center justify-center rounded-lg ${active ? 'bg-sapphire/20 text-sapphire' : 'bg-white/5 text-slate-400'}`}>
                <Icon className="h-4.5 w-4.5" />
              </div>
              <div className="flex-1">
                <div className="flex items-center gap-2">
                  <span className="text-sm font-medium text-white">{opt.label}</span>
                  {active && <Check className="h-3.5 w-3.5 text-sapphire" />}
                </div>
                <p className="text-xs text-slate-400 mt-0.5">{opt.description}</p>
              </div>
            </button>
          );
        })}
      </div>
    </div>
  );
}

function NumberInput({ label, value, min, max, onChange, help }: {
  label: string; value: number; min: number; max: number;
  onChange: (v: number) => void; help?: string;
}) {
  return (
    <div className="space-y-1.5">
      <label className="text-xs font-medium text-slate-400 uppercase tracking-wider">{label}</label>
      {help && <p className="text-xs text-slate-500">{help}</p>}
      <div className="flex items-center gap-2">
        <button
          type="button"
          onClick={() => onChange(Math.max(min, value - 1))}
          className="h-8 w-8 flex items-center justify-center rounded-lg bg-white/5 border border-white/10 text-slate-400 hover:text-white hover:border-white/20 transition-colors text-lg font-light"
        >−</button>
        <input
          type="number"
          value={value}
          min={min}
          max={max}
          onChange={e => onChange(Math.max(min, Math.min(max, parseInt(e.target.value) || min)))}
          className="w-24 bg-white/[0.05] border border-white/10 rounded-lg px-3 py-1.5 text-sm text-white text-center focus:outline-none focus:border-sapphire/50"
        />
        <button
          type="button"
          onClick={() => onChange(Math.min(max, value + 1))}
          className="h-8 w-8 flex items-center justify-center rounded-lg bg-white/5 border border-white/10 text-slate-400 hover:text-white hover:border-white/20 transition-colors text-lg font-light"
        >+</button>
      </div>
    </div>
  );
}

function Step2Structure({ state, update }: { state: WizardState; update: (p: Partial<WizardState>) => void }) {
  const isGroup = state.orgType !== 'single';
  return (
    <div className="space-y-6">
      <div>
        <h2 className="text-lg font-semibold text-white">Tell us about your company structure</h2>
        <p className="text-sm text-slate-400 mt-1">Used to calculate your estimated total cost.</p>
      </div>

      <div className="grid grid-cols-1 sm:grid-cols-2 gap-5">
        {isGroup && (
          <NumberInput
            label="Legal Companies / Entities"
            value={state.numCompanies}
            min={2} max={500}
            onChange={v => update({ numCompanies: v })}
            help="Each registered legal entity requiring separate payroll/tax"
          />
        )}
        <NumberInput
          label="Branches / Locations"
          value={state.numBranches}
          min={1} max={500}
          onChange={v => update({ numBranches: v })}
        />
        <NumberInput
          label="Active Employees"
          value={state.numEmployees}
          min={1} max={50000}
          onChange={v => update({ numEmployees: v })}
          help="Employees who will be managed in the system"
        />
        <NumberInput
          label="Admin / System Users"
          value={state.numAdminUsers}
          min={1} max={500}
          onChange={v => update({ numAdminUsers: v })}
          help="HR managers, admins, approvers (not employees)"
        />
        <NumberInput
          label="Countries of Operation"
          value={state.numCountries}
          min={1} max={50}
          onChange={v => update({ numCountries: v })}
        />
      </div>

      <div className="flex items-center gap-3 p-4 rounded-xl border border-white/10 bg-white/[0.03]">
        <button
          type="button"
          onClick={() => update({ needsArabic: !state.needsArabic })}
          className={`h-5 w-9 rounded-full transition-colors relative ${state.needsArabic ? 'bg-sapphire' : 'bg-white/10'}`}
        >
          <span className={`absolute top-0.5 h-4 w-4 rounded-full bg-white shadow transition-all ${state.needsArabic ? 'start-4' : 'start-0.5'}`} />
        </button>
        <div>
          <div className="text-sm font-medium text-white flex items-center gap-1.5">
            <Languages className="h-4 w-4 text-slate-400" />
            Arabic / Bilingual Interface Required
          </div>
          <p className="text-xs text-slate-500 mt-0.5">Full RTL support, Arabic labels, Hijri calendar</p>
        </div>
      </div>
    </div>
  );
}

function Step3Modules({
  state, update, modules,
}: {
  state: WizardState;
  update: (p: Partial<WizardState>) => void;
  modules: PricingModule[];
}) {
  const toggle = (key: string) => {
    const selected = state.selectedModules.includes(key)
      ? state.selectedModules.filter(k => k !== key)
      : [...state.selectedModules, key];
    update({ selectedModules: selected });
  };

  const planIncludes = (m: PricingModule) =>
    m.includedInGrowth || m.includedInEnterprise ? 'Growth+' :
    m.includedInStarter ? 'Starter+' :
    m.includedInTrial   ? 'All plans' : 'Add-on only';

  return (
    <div className="space-y-4">
      <div>
        <h2 className="text-lg font-semibold text-white">Select the modules you need</h2>
        <p className="text-sm text-slate-400 mt-1">Core HR is always included. Choose optional modules.</p>
      </div>
      <div className="grid gap-2">
        {modules.map(m => {
          const isSelected = state.selectedModules.includes(m.key);
          const isCore = m.key === 'core_hr';
          return (
            <button
              key={m.key}
              type="button"
              disabled={isCore}
              onClick={() => !isCore && toggle(m.key)}
              className={`flex items-center gap-3 px-4 py-3 rounded-lg border text-start transition-all ${
                isSelected
                  ? 'bg-sapphire/10 border-sapphire/40'
                  : 'bg-white/[0.02] border-white/[0.07] hover:border-white/15'
              } ${isCore ? 'opacity-60 cursor-default' : ''}`}
            >
              <div className={`h-5 w-5 shrink-0 rounded flex items-center justify-center border transition-colors ${
                isSelected ? 'bg-sapphire border-sapphire' : 'border-white/20'
              }`}>
                {isSelected && <Check className="h-3 w-3 text-white" />}
              </div>
              <div className="flex-1 min-w-0">
                <div className="flex items-center gap-2 flex-wrap">
                  <span className="text-sm text-white">{m.name}</span>
                  {m.isEnterpriseOnly && (
                    <span className="text-[10px] px-1.5 py-0.5 rounded bg-amber-900/40 text-amber-300 font-medium">Enterprise</span>
                  )}
                </div>
                <p className="text-xs text-slate-500 mt-0.5">
                  {m.addonPriceMonthly > 0 ? `+${fmt(m.addonPriceMonthly)}/mo add-on · ` : ''}
                  Included in {planIncludes(m)}
                </p>
              </div>
            </button>
          );
        })}
      </div>
    </div>
  );
}

function UsageBar({ label, value, max }: { label: string; value: number; max: number | null }) {
  const pct = max ? Math.min(100, (value / max) * 100) : 0;
  return (
    <div className="space-y-1">
      <div className="flex justify-between text-xs text-slate-400">
        <span>{label}</span>
        <span className="text-slate-300">{value.toLocaleString()} / {max === null ? '∞' : max.toLocaleString()}</span>
      </div>
      {max !== null && (
        <div className="h-1.5 bg-white/[0.06] rounded-full overflow-hidden">
          <div className="h-full bg-sapphire rounded-full" style={{ width: `${pct}%` }} />
        </div>
      )}
    </div>
  );
}

function Step4Summary({ state, estimate, billing, setBilling }: {
  state: WizardState;
  estimate: PricingEstimate;
  billing: 'monthly' | 'annual';
  setBilling: (b: 'monthly' | 'annual') => void;
}) {
  const plan = estimate.recommendedPlan;
  const total = billing === 'annual' ? estimate.annualTotal : estimate.monthlyTotal * 12;
  const monthly = billing === 'annual' ? estimate.annualTotal / 12 : estimate.monthlyTotal;

  return (
    <div className="space-y-5">
      <div className="flex items-start justify-between gap-3 flex-wrap">
        <div>
          <h2 className="text-lg font-semibold text-white">Your Estimated Package</h2>
          <p className="text-xs text-slate-400 mt-0.5">Based on your selections — adjust inputs to see changes.</p>
        </div>
        {/* Billing toggle */}
        <div className="flex rounded-lg border border-white/10 overflow-hidden text-xs shrink-0">
          {(['monthly', 'annual'] as const).map(b => (
            <button
              key={b}
              type="button"
              onClick={() => setBilling(b)}
              className={`px-3 py-1.5 font-medium capitalize transition-colors ${billing === b ? 'bg-sapphire text-white' : 'text-slate-400 hover:text-white'}`}
            >
              {b}{b === 'annual' && <span className="ms-1 text-emerald-400">-{estimate.annualDiscountPct}%</span>}
            </button>
          ))}
        </div>
      </div>

      {/* Recommended plan badge */}
      <div className={`flex items-center gap-2 px-3 py-2 rounded-lg text-sm font-semibold ${PLAN_BADGE[plan] ?? PLAN_BADGE.Trial}`}>
        Recommended: {plan}
        {estimate.isEnterpriseRequired && <span className="text-xs font-normal ms-1 opacity-70">· Custom quote required</span>}
      </div>

      {/* Price headline */}
      <div className="bg-white/[0.03] border border-white/[0.07] rounded-xl p-5">
        <div className="flex items-baseline gap-2">
          <span className="text-3xl font-bold text-white">{fmt(monthly)}</span>
          <span className="text-slate-500 text-sm">/mo</span>
          {billing === 'annual' && (
            <span className="ms-2 text-xs text-slate-500">billed {fmt(total)}/yr</span>
          )}
        </div>
        {estimate.isEnterpriseRequired && (
          <p className="text-xs text-amber-400 mt-1 flex items-center gap-1">
            <AlertCircle className="h-3.5 w-3.5" />
            Enterprise pricing — a formal quote is required.
          </p>
        )}
      </div>

      {/* Cost breakdown */}
      <div className="space-y-2">
        <h3 className="text-xs font-semibold text-slate-400 uppercase tracking-wider">Cost Breakdown</h3>
        <div className="bg-[#161b22] border border-white/[0.07] rounded-xl divide-y divide-white/[0.05]">
          {[
            { label: `${plan} Base Plan`,                value: estimate.breakdown.basePlanPrice },
            ...(estimate.breakdown.extraEmployeeCharge   > 0 ? [{ label: `${estimate.breakdown.extraEmployeeCount} extra employees`,  value: estimate.breakdown.extraEmployeeCharge }] : []),
            ...(estimate.breakdown.extraCompanyCharge    > 0 ? [{ label: `${estimate.breakdown.extraCompanyCount} extra companies`,   value: estimate.breakdown.extraCompanyCharge }] : []),
            ...(estimate.breakdown.extraAdminUserCharge  > 0 ? [{ label: `${estimate.breakdown.extraAdminUserCount} extra admin users`, value: estimate.breakdown.extraAdminUserCharge }] : []),
            ...(estimate.breakdown.arabicSupplement      > 0 ? [{ label: 'Arabic/Bilingual Interface',                                value: estimate.breakdown.arabicSupplement }] : []),
            ...(estimate.breakdown.extraCountryCharge    > 0 ? [{ label: `Multi-country (${state.numCountries} countries)`,          value: estimate.breakdown.extraCountryCharge }] : []),
            ...estimate.breakdown.moduleAddOns.map(m => ({ label: `${m.name} add-on`, value: m.monthlyPrice })),
          ].map((row, i) => (
            <div key={i} className="flex justify-between px-4 py-2.5 text-sm">
              <span className="text-slate-400">{row.label}</span>
              <span className="text-slate-200 font-medium">{fmt(row.value)}/mo</span>
            </div>
          ))}
          <div className="flex justify-between px-4 py-3 text-sm font-semibold">
            <span className="text-white">Monthly Total</span>
            <span className="text-white">{fmt(estimate.monthlyTotal)}/mo</span>
          </div>
        </div>
      </div>

      {/* Included + Add-ons */}
      <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
        {estimate.includedFeatures.length > 0 && (
          <div>
            <h3 className="text-xs font-semibold text-slate-400 uppercase tracking-wider mb-2">Included Features</h3>
            <ul className="space-y-1.5">
              {estimate.includedFeatures.map(f => (
                <li key={f} className="flex items-center gap-2 text-xs text-slate-300">
                  <Check className="h-3.5 w-3.5 text-emerald-500 shrink-0" /> {f}
                </li>
              ))}
            </ul>
          </div>
        )}
        {estimate.paidAddOns.length > 0 && (
          <div>
            <h3 className="text-xs font-semibold text-slate-400 uppercase tracking-wider mb-2">Paid Add-Ons</h3>
            <ul className="space-y-1.5">
              {estimate.paidAddOns.map(f => (
                <li key={f} className="flex items-center gap-2 text-xs text-slate-300">
                  <span className="text-amber-400 font-mono text-xs">+</span> {f}
                </li>
              ))}
            </ul>
          </div>
        )}
      </div>

      {/* Usage comparison */}
      <div className="bg-white/[0.03] border border-white/[0.07] rounded-xl p-4 space-y-3">
        <h3 className="text-xs font-semibold text-slate-400 uppercase tracking-wider">Plan Limits</h3>
        <UsageBar label="Active Employees" value={state.numEmployees} max={estimate.breakdown.includedEmployees} />
        <UsageBar label="Legal Companies" value={state.numCompanies} max={estimate.breakdown.includedCompanies} />
        <UsageBar label="Admin Users" value={state.numAdminUsers} max={estimate.breakdown.includedAdminUsers} />
      </div>

      {/* Implementation */}
      <div className="flex items-start gap-3 px-4 py-3 rounded-xl bg-blue-950/30 border border-blue-800/30 text-xs text-blue-300">
        <Info className="h-4 w-4 mt-0.5 shrink-0" />
        <span>
          <strong>One-time implementation fee:</strong> {fmt(estimate.breakdown.implementationEstimate)} — includes data migration, configuration, and onboarding training. Final amount confirmed in proposal.
        </span>
      </div>

      {/* Disclaimer */}
      <p className="text-xs text-slate-500 italic">{estimate.disclaimer}</p>
    </div>
  );
}

interface ContactForm {
  companyName: string;
  contactName: string;
  contactEmail: string;
  phone: string;
  notes: string;
}

function Step5Contact({ form, setForm }: { form: ContactForm; setForm: (f: ContactForm) => void }) {
  const field = (k: keyof ContactForm) => ({
    value: form[k],
    onChange: (e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) =>
      setForm({ ...form, [k]: e.target.value }),
  });

  return (
    <div className="space-y-5">
      <div>
        <h2 className="text-lg font-semibold text-white">Request a Formal Proposal</h2>
        <p className="text-sm text-slate-400 mt-1">We'll send you a detailed proposal with final pricing within 1 business day.</p>
      </div>
      <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
        {([
          { key: 'companyName', label: 'Company Name', placeholder: 'Acme Corp', required: true },
          { key: 'contactName', label: 'Your Name',    placeholder: 'Jane Smith', required: true },
          { key: 'contactEmail',label: 'Work Email',   placeholder: 'jane@acme.com', required: true },
          { key: 'phone',       label: 'Phone',        placeholder: '+1 555 000 0000', required: false },
        ] as { key: keyof ContactForm; label: string; placeholder: string; required: boolean }[]).map(f => (
          <div key={f.key} className="space-y-1.5">
            <label className="text-xs font-medium text-slate-400 uppercase tracking-wider">
              {f.label}{f.required && <span className="text-rose-400 ms-1">*</span>}
            </label>
            <input
              type={f.key === 'contactEmail' ? 'email' : 'text'}
              placeholder={f.placeholder}
              {...field(f.key as keyof ContactForm)}
              className="w-full bg-white/[0.05] border border-white/10 rounded-lg px-3 py-2 text-sm text-white placeholder-slate-600 focus:outline-none focus:border-sapphire/50 transition-colors"
            />
          </div>
        ))}
      </div>
      <div className="space-y-1.5">
        <label className="text-xs font-medium text-slate-400 uppercase tracking-wider">Additional Notes</label>
        <textarea
          rows={3}
          placeholder="Any specific requirements, timeline, or questions..."
          {...field('notes')}
          className="w-full bg-white/[0.05] border border-white/10 rounded-lg px-3 py-2 text-sm text-white placeholder-slate-600 focus:outline-none focus:border-sapphire/50 transition-colors resize-none"
        />
      </div>
    </div>
  );
}

// ── Main page ─────────────────────────────────────────────────────────────────

const STEPS = ['Organization', 'Structure', 'Modules', 'Pricing', 'Request Quote'];

export default function PricingCalculatorPage() {
  const [step, setStep] = useState(0);
  const [wizard, setWizard] = useState<WizardState>(DEFAULT_STATE);
  const [modules, setModules] = useState<PricingModule[]>([]);
  const [estimate, setEstimate] = useState<PricingEstimate | null>(null);
  const [billing, setBilling] = useState<'monthly' | 'annual'>('monthly');
  const [estimating, setEstimating] = useState(false);
  const [contact, setContact] = useState<ContactForm>({ companyName: '', contactName: '', contactEmail: '', phone: '', notes: '' });
  const [submitting, setSubmitting] = useState(false);
  const [submitted, setSubmitted] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const update = useCallback((patch: Partial<WizardState>) => setWizard(prev => ({ ...prev, ...patch })), []);

  useEffect(() => {
    pricingApi.getModules().then(setModules).catch(() => {});
  }, []);

  const runEstimate = useCallback(async () => {
    setEstimating(true);
    setError(null);
    try {
      const result = await pricingApi.estimate({
        orgType: wizard.orgType,
        numCompanies: wizard.numCompanies,
        numBranches: wizard.numBranches,
        numEmployees: wizard.numEmployees,
        numAdminUsers: wizard.numAdminUsers,
        numCountries: wizard.numCountries,
        needsArabic: wizard.needsArabic,
        selectedModules: wizard.selectedModules,
      });
      setEstimate(result);
    } catch {
      setError('Could not calculate estimate. Please try again.');
    } finally {
      setEstimating(false);
    }
  }, [wizard]);

  const goNext = async () => {
    if (step === 2) await runEstimate();
    setStep(s => Math.min(STEPS.length - 1, s + 1));
  };

  const handleSubmit = async () => {
    if (!estimate) return;
    if (!contact.companyName || !contact.contactName || !contact.contactEmail) {
      setError('Please fill in the required fields.');
      return;
    }
    setSubmitting(true);
    setError(null);
    try {
      await pricingApi.submitQuote({
        companyName: contact.companyName,
        contactName: contact.contactName,
        contactEmail: contact.contactEmail,
        phone: contact.phone || undefined,
        orgType: wizard.orgType,
        numCompanies: wizard.numCompanies,
        numBranches: wizard.numBranches,
        numEmployees: wizard.numEmployees,
        numAdminUsers: wizard.numAdminUsers,
        numCountries: wizard.numCountries,
        needsArabic: wizard.needsArabic,
        selectedModules: wizard.selectedModules,
        estimatedMonthlyAmount: estimate.monthlyTotal,
        estimatedAnnualAmount: estimate.annualTotal,
        notes: contact.notes || undefined,
      });
      setSubmitted(true);
    } catch {
      setError('Failed to submit quote request. Please try again.');
    } finally {
      setSubmitting(false);
    }
  };

  if (submitted) {
    return (
      <div className="min-h-screen bg-[#0d1117] flex items-center justify-center p-6">
        <div className="max-w-md w-full text-center space-y-6">
          <div className="h-16 w-16 mx-auto rounded-full bg-emerald-500/10 border border-emerald-500/30 flex items-center justify-center">
            <Check className="h-8 w-8 text-emerald-400" />
          </div>
          <h1 className="text-2xl font-bold text-white">Quote Request Sent!</h1>
          <p className="text-slate-400">
            Thank you, <strong className="text-white">{contact.contactName}</strong>. Our team will send a detailed proposal to <strong className="text-white">{contact.contactEmail}</strong> within 1 business day.
          </p>
          {estimate && (
            <div className="bg-white/[0.03] border border-white/[0.07] rounded-xl p-4 space-y-1">
              <p className="text-xs text-slate-500 uppercase tracking-wider">Estimated</p>
              <p className="text-2xl font-bold text-white">{fmt(estimate.monthlyTotal)}<span className="text-slate-500 text-sm font-normal">/mo</span></p>
              <p className={`text-xs font-semibold ${PLAN_BADGE[estimate.recommendedPlan] ?? ''} inline-block px-2 py-0.5 rounded`}>{estimate.recommendedPlan} Plan</p>
            </div>
          )}
          <Link href="/" className="inline-flex items-center gap-2 text-sapphire hover:underline text-sm">
            Back to home
          </Link>
        </div>
      </div>
    );
  }

  return (
    <div className="min-h-screen bg-[#0d1117] text-white">
      {/* Header */}
      <header className="border-b border-white/[0.07] px-6 py-4 flex items-center justify-between">
        <Link href="/" className="flex items-center gap-2 text-slate-300 hover:text-white transition-colors text-sm">
          <span className="font-bold text-sapphire text-lg">KynexOne</span>
          <span className="text-slate-600">/</span>
          <span>Pricing Calculator</span>
        </Link>
        <a
          href="mailto:sales@kynexone.com"
          className="text-xs text-slate-500 hover:text-white transition-colors"
        >
          Talk to Sales →
        </a>
      </header>

      <div className="max-w-2xl mx-auto px-6 py-10">
        {/* Step indicator */}
        <div className="flex items-center gap-1.5 mb-8">
          {STEPS.map((label, i) => (
            <div key={i} className="flex items-center gap-1.5 flex-1">
              <div className={`flex items-center gap-1.5 ${i < step ? 'text-emerald-400' : i === step ? 'text-white' : 'text-slate-600'}`}>
                <div className={`h-6 w-6 rounded-full flex items-center justify-center text-xs font-bold border transition-all ${
                  i < step  ? 'bg-emerald-500/20 border-emerald-500/40 text-emerald-400' :
                  i === step ? 'bg-sapphire/20 border-sapphire/50 text-sapphire' :
                               'border-white/10 text-slate-600'
                }`}>
                  {i < step ? <Check className="h-3 w-3" /> : i + 1}
                </div>
                <span className="text-xs hidden sm:block">{label}</span>
              </div>
              {i < STEPS.length - 1 && (
                <div className={`flex-1 h-px ${i < step ? 'bg-emerald-500/30' : 'bg-white/[0.07]'}`} />
              )}
            </div>
          ))}
        </div>

        {/* Step content */}
        <div className="bg-[#161b22] border border-white/[0.07] rounded-2xl p-6 mb-5">
          {step === 0 && <Step1OrgType state={wizard} update={update} />}
          {step === 1 && <Step2Structure state={wizard} update={update} />}
          {step === 2 && <Step3Modules state={wizard} update={update} modules={modules} />}
          {step === 3 && estimate && (
            <Step4Summary state={wizard} estimate={estimate} billing={billing} setBilling={setBilling} />
          )}
          {step === 3 && !estimate && estimating && (
            <div className="flex items-center justify-center py-16 gap-3 text-slate-400">
              <RefreshCw className="h-5 w-5 animate-spin" />
              Calculating your estimate…
            </div>
          )}
          {step === 4 && <Step5Contact form={contact} setForm={setContact} />}
        </div>

        {error && (
          <div className="flex items-center gap-2 px-4 py-2.5 mb-4 rounded-lg bg-rose-500/10 border border-rose-500/20 text-rose-400 text-sm">
            <AlertCircle className="h-4 w-4 shrink-0" />
            {error}
          </div>
        )}

        {/* Navigation */}
        <div className="flex items-center justify-between">
          <button
            type="button"
            onClick={() => setStep(s => Math.max(0, s - 1))}
            disabled={step === 0}
            className="flex items-center gap-1.5 px-4 py-2 rounded-lg border border-white/10 text-sm text-slate-400 hover:text-white hover:border-white/20 transition-colors disabled:opacity-40"
          >
            <ChevronLeft className="h-4 w-4" /> Back
          </button>

          {step < STEPS.length - 1 ? (
            <button
              type="button"
              onClick={goNext}
              disabled={estimating}
              className="flex items-center gap-1.5 px-5 py-2 rounded-lg bg-sapphire hover:bg-sapphire/90 text-white text-sm font-medium transition-colors disabled:opacity-50"
            >
              {estimating ? <RefreshCw className="h-4 w-4 animate-spin" /> : null}
              {step === 2 ? 'Calculate Pricing' : step === 3 ? 'Request Proposal' : 'Continue'}
              {!estimating && <ChevronRight className="h-4 w-4" />}
            </button>
          ) : (
            <button
              type="button"
              onClick={handleSubmit}
              disabled={submitting}
              className="flex items-center gap-1.5 px-5 py-2 rounded-lg bg-emerald-600 hover:bg-emerald-500 text-white text-sm font-medium transition-colors disabled:opacity-50"
            >
              {submitting ? <RefreshCw className="h-4 w-4 animate-spin" /> : <Sparkles className="h-4 w-4" />}
              {submitting ? 'Sending…' : 'Submit Quote Request'}
            </button>
          )}
        </div>

        {/* Recalculate link on summary step */}
        {step === 3 && estimate && (
          <div className="mt-4 text-center">
            <button
              type="button"
              onClick={runEstimate}
              disabled={estimating}
              className="text-xs text-slate-500 hover:text-white flex items-center gap-1 mx-auto transition-colors"
            >
              <RefreshCw className={`h-3 w-3 ${estimating ? 'animate-spin' : ''}`} />
              Recalculate with updated inputs
            </button>
          </div>
        )}

        {step < 3 && (
          <p className="text-center text-xs text-slate-600 mt-4">
            No credit card required · Estimate updates live · Final price confirmed in proposal
          </p>
        )}
      </div>
    </div>
  );
}
