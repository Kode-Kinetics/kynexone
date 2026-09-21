'use client';

import { useState, useEffect } from 'react';
import { useParams, useRouter } from 'next/navigation';
import { ArrowLeft, ArrowRight, CheckCircle, AlertCircle, Building2 } from 'lucide-react';
import { platformApi, type PlatformQuote } from '@/src/api/platform';

const INPUT_CLS = 'w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60 placeholder-slate-600';
const LABEL_CLS = 'block text-xs text-slate-400 mb-1';

export default function QuoteConvertPage() {
  const { id } = useParams<{ id: string }>();
  const router = useRouter();

  const [quote, setQuote]       = useState<PlatformQuote | null>(null);
  const [fetchErr, setFetchErr] = useState('');
  const [loading, setLoading]   = useState(true);

  const [form, setForm] = useState({
    slug: '',
    adminEmail: '',
    adminFullName: '',
    adminPassword: '',
    plan: 'Trial',
    billingCycle: 'Monthly',
  });
  const [converting, setConverting] = useState(false);
  const [convErr, setConvErr]       = useState('');
  const [success, setSuccess]       = useState<{ tenantId: string } | null>(null);

  useEffect(() => {
    const token = typeof window !== 'undefined' ? localStorage.getItem('platform_access_token') : null;
    if (!token) { router.replace('/platform/login'); return; }
    platformApi.getQuote(id)
      .then(q => {
        setQuote(q);
        // Pre-fill slug from company name
        const slug = q.companyName.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');
        setForm(f => ({ ...f, slug, adminEmail: q.contactEmail, adminFullName: q.contactName }));
      })
      .catch(() => setFetchErr('Could not load quote details.'))
      .finally(() => setLoading(false));
  }, [id, router]);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (!quote) return;
    setConverting(true); setConvErr('');
    try {
      const result = await platformApi.convertQuote(id, {
        slug: form.slug,
        adminEmail: form.adminEmail,
        adminFullName: form.adminFullName || undefined,
        adminPassword: form.adminPassword,
        plan: form.plan,
        billingCycle: form.billingCycle,
      });
      setSuccess(result);
    } catch (ex: unknown) {
      const msg = (ex as { response?: { data?: { message?: string; error?: string } } })?.response?.data;
      setConvErr(msg?.message ?? msg?.error ?? 'Conversion failed. Please check the details and try again.');
    } finally { setConverting(false); }
  }

  if (loading) {
    return (
      <div className="flex items-center justify-center py-32">
        <div className="h-6 w-6 animate-spin rounded-full border-2 border-sapphire border-t-transparent" />
      </div>
    );
  }

  if (fetchErr || !quote) {
    return (
      <div className="flex flex-col items-center justify-center py-32 gap-4">
        <AlertCircle className="h-8 w-8 text-rose-400" />
        <p className="text-sm text-rose-400">{fetchErr || 'Quote not found.'}</p>
        <button type="button" onClick={() => router.back()}
          className="text-xs text-slate-400 hover:text-white flex items-center gap-1">
          <ArrowLeft className="h-3.5 w-3.5" /> Back
        </button>
      </div>
    );
  }

  if (success) {
    return (
      <div className="max-w-lg mx-auto space-y-5">
        <button type="button" onClick={() => router.push('/platform/pricing')}
          className="flex items-center gap-1.5 text-xs text-slate-500 hover:text-white transition-colors">
          <ArrowLeft className="h-3.5 w-3.5" /> Back to Quotes
        </button>

        <div className="bg-[#161b22] border border-emerald-500/30 rounded-2xl p-8 flex flex-col items-center gap-4 text-center">
          <div className="h-14 w-14 rounded-full bg-emerald-500/10 border border-emerald-500/20 flex items-center justify-center">
            <CheckCircle className="h-7 w-7 text-emerald-400" />
          </div>
          <div>
            <h2 className="text-lg font-bold text-white">Tenant Provisioned!</h2>
            <p className="text-sm text-slate-400 mt-1">
              <span className="text-white font-medium">{quote.companyName}</span> has been converted to a live tenant.
            </p>
          </div>
          <div className="bg-white/[0.03] border border-white/[0.07] rounded-xl px-6 py-4 w-full text-start space-y-2">
            <div className="flex items-center justify-between text-xs">
              <span className="text-slate-500">Tenant ID</span>
              <span className="text-slate-300 font-mono">{success.tenantId}</span>
            </div>
          </div>
          <div className="flex flex-col sm:flex-row gap-3 w-full pt-2">
            <button type="button"
              onClick={() => router.push(`/platform/tenants/${success.tenantId}`)}
              className="flex-1 flex items-center justify-center gap-1.5 bg-sapphire hover:bg-blue-500 text-white rounded-lg py-2.5 text-sm font-semibold transition-colors">
              Go to Tenant <ArrowRight className="h-4 w-4" />
            </button>
            <button type="button"
              onClick={() => router.push('/platform/pricing')}
              className="flex-1 border border-white/10 text-slate-400 hover:text-white rounded-lg py-2.5 text-sm transition-colors">
              Back to Quotes
            </button>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="max-w-lg mx-auto space-y-5">
      {/* Back link */}
      <button type="button" onClick={() => router.back()}
        className="flex items-center gap-1.5 text-xs text-slate-500 hover:text-white transition-colors">
        <ArrowLeft className="h-3.5 w-3.5" /> Back to Quotes
      </button>

      {/* Quote summary card */}
      <div className="bg-[#161b22] border border-white/[0.07] rounded-2xl overflow-hidden">
        <div className="px-5 py-4 border-b border-white/[0.07] flex items-center gap-3">
          <div className="h-9 w-9 rounded-xl bg-sapphire/10 border border-sapphire/20 flex items-center justify-center flex-shrink-0">
            <Building2 className="h-4 w-4 text-blue-400" />
          </div>
          <div>
            <h1 className="text-sm font-bold text-white">{quote.companyName}</h1>
            <p className="text-xs text-slate-500">{quote.contactEmail}</p>
          </div>
          <span className={`ms-auto text-[10px] px-2 py-0.5 rounded font-medium ${quote.status === 'Converted' ? 'bg-emerald-900/50 text-emerald-300' : 'bg-blue-900/50 text-blue-300'}`}>
            {quote.status}
          </span>
        </div>

        <div className="grid grid-cols-3 divide-x rtl:divide-x-reverse divide-white/[0.05] px-1">
          {[
            { label: 'Org Type', value: quote.orgType },
            { label: 'Employees', value: quote.numEmployees.toLocaleString() },
            { label: 'Est. Monthly', value: `$${quote.estimatedMonthlyAmount.toLocaleString()}` },
          ].map(({ label, value }) => (
            <div key={label} className="px-4 py-3">
              <p className="text-[10px] text-slate-500 uppercase tracking-wider">{label}</p>
              <p className="text-sm font-medium text-slate-200 mt-0.5">{value}</p>
            </div>
          ))}
        </div>
      </div>

      {/* Already converted guard */}
      {quote.status === 'Converted' ? (
        <div className="bg-emerald-500/10 border border-emerald-500/20 rounded-xl px-5 py-4 flex items-center gap-3">
          <CheckCircle className="h-5 w-5 text-emerald-400 flex-shrink-0" />
          <div>
            <p className="text-sm text-emerald-300 font-medium">Already converted</p>
            <p className="text-xs text-emerald-400/70 mt-0.5">This quote has already been converted to a tenant.</p>
          </div>
          {quote.convertedToTenantId && (
            <button type="button"
              onClick={() => router.push(`/platform/tenants/${quote.convertedToTenantId}`)}
              className="ms-auto text-xs text-emerald-400 hover:text-emerald-300 underline whitespace-nowrap">
              View Tenant →
            </button>
          )}
        </div>
      ) : (
        <form onSubmit={submit} className="bg-[#161b22] border border-white/[0.07] rounded-2xl overflow-hidden">
          <div className="px-5 py-4 border-b border-white/[0.07]">
            <h2 className="text-sm font-semibold text-white">Tenant Provisioning Details</h2>
            <p className="text-xs text-slate-500 mt-0.5">Fill in the details to create a live tenant from this quote.</p>
          </div>

          <div className="px-5 py-5 space-y-4">
            {convErr && (
              <div className="flex items-start gap-2 text-xs text-rose-400 bg-rose-500/10 border border-rose-500/20 rounded-lg px-3 py-2.5">
                <AlertCircle className="h-3.5 w-3.5 flex-shrink-0 mt-0.5" />
                {convErr}
              </div>
            )}

            <div className="grid grid-cols-2 gap-3">
              <div>
                <label htmlFor="cvtq-slug" className={LABEL_CLS}>Tenant Slug *</label>
                <input id="cvtq-slug" type="text" required title="Tenant Slug"
                  value={form.slug}
                  onChange={e => setForm(f => ({ ...f, slug: e.target.value.toLowerCase().replace(/[^a-z0-9-]/g, '') }))}
                  placeholder="acme-corp"
                  className={`${INPUT_CLS} font-mono`} />
              </div>
              <div>
                <label htmlFor="cvtq-plan" className={LABEL_CLS}>Plan</label>
                <select id="cvtq-plan" title="Plan" value={form.plan}
                  onChange={e => setForm(f => ({ ...f, plan: e.target.value }))}
                  className={INPUT_CLS}>
                  {['Trial', 'Starter', 'Growth', 'Enterprise'].map(p => <option key={p} value={p}>{p}</option>)}
                </select>
              </div>
            </div>

            <div>
              <label htmlFor="cvtq-admin-email" className={LABEL_CLS}>Admin Email *</label>
              <input id="cvtq-admin-email" type="email" required title="Admin Email"
                value={form.adminEmail}
                onChange={e => setForm(f => ({ ...f, adminEmail: e.target.value }))}
                placeholder="admin@company.com"
                className={INPUT_CLS} />
            </div>

            <div>
              <label htmlFor="cvtq-admin-name" className={LABEL_CLS}>Admin Full Name</label>
              <input id="cvtq-admin-name" type="text" title="Admin Full Name"
                value={form.adminFullName}
                onChange={e => setForm(f => ({ ...f, adminFullName: e.target.value }))}
                placeholder="Jane Smith"
                className={INPUT_CLS} />
            </div>

            <div>
              <label htmlFor="cvtq-admin-pw" className={LABEL_CLS}>
                Admin Password * <span className="text-slate-600">(min 10 chars)</span>
              </label>
              <input id="cvtq-admin-pw" type="password" required minLength={10} title="Admin Password"
                value={form.adminPassword}
                onChange={e => setForm(f => ({ ...f, adminPassword: e.target.value }))}
                placeholder="Min 10 characters"
                className={INPUT_CLS} />
            </div>

            <div>
              <label htmlFor="cvtq-billing" className={LABEL_CLS}>Billing Cycle</label>
              <select id="cvtq-billing" title="Billing Cycle" value={form.billingCycle}
                onChange={e => setForm(f => ({ ...f, billingCycle: e.target.value }))}
                className={INPUT_CLS}>
                <option value="Monthly">Monthly</option>
                <option value="Annual">Annual</option>
              </select>
            </div>
          </div>

          <div className="px-5 pb-5">
            <button type="submit" disabled={converting}
              className="w-full flex items-center justify-center gap-2 bg-emerald-600 hover:bg-emerald-500 disabled:opacity-40 text-white rounded-xl py-3 text-sm font-semibold transition-colors">
              {converting ? (
                <><div className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent" /> Provisioning…</>
              ) : (
                <><ArrowRight className="h-4 w-4" /> Convert to Tenant</>
              )}
            </button>
          </div>
        </form>
      )}
    </div>
  );
}
