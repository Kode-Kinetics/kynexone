'use client';

import { useState, useEffect, useCallback } from 'react';
import { useRouter } from 'next/navigation';
import { RefreshCw, Edit3, Check, X } from 'lucide-react';
import { platformApi, type PlatformPlan } from '@/src/api/platform';

const PLAN_BADGE: Record<string, string> = {
  trial:      'bg-slate-700 text-slate-300',
  starter:    'bg-blue-900/50 text-blue-300',
  growth:     'bg-purple-900/50 text-purple-300',
  enterprise: 'bg-amber-900/50 text-amber-300',
};

const PLAN_FEATURES: Record<string, string[]> = {
  trial:      ['Up to 20 employees', 'Core HR', 'Basic Reports', '14-day limit'],
  starter:    ['Up to 100 employees', 'HR + Payroll', 'Feature flags selectable', 'Email support'],
  growth:     ['Up to 500 employees', 'Full HR suite', 'Workspace Assistant', 'Priority support'],
  enterprise: ['Unlimited employees', 'All features', 'Dedicated support', 'Custom integrations'],
};

export default function PlansPage() {
  const router = useRouter();
  const [plans, setPlans]     = useState<PlatformPlan[]>([]);
  const [loading, setLoading] = useState(true);
  const [editing, setEditing] = useState<string | null>(null);
  const [price, setPrice]     = useState(0);
  const [saving, setSaving]   = useState(false);
  const [msg, setMsg]         = useState<{ text: string; ok: boolean } | null>(null);

  useEffect(() => {
    const token = typeof window !== 'undefined' ? localStorage.getItem('platform_access_token') : null;
    if (!token) { router.replace('/platform/login'); return; }
    load();
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const load = useCallback(async () => {
    setLoading(true);
    try { setPlans(await platformApi.getPlans()); }
    finally { setLoading(false); }
  }, []);

  async function savePrice(planName: string) {
    setSaving(true);
    try {
      await platformApi.updatePlanPrice(planName, price);
      setMsg({ text: `${planName} price updated to $${price}/mo.`, ok: true });
      setEditing(null);
      await load();
    } catch { setMsg({ text: 'Update failed.', ok: false }); }
    finally { setSaving(false); }
  }

  return (
    <div className="space-y-5">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-lg font-bold text-white">Plans & Features</h1>
          <p className="text-xs text-slate-500 mt-0.5">Configure pricing and capabilities for each plan tier</p>
        </div>
        <button type="button" onClick={load} disabled={loading} title="Refresh" aria-label="Refresh"
          className="h-8 w-8 flex items-center justify-center text-slate-500 hover:text-white border border-white/10 rounded-lg transition-colors disabled:opacity-40">
          <RefreshCw className={`h-3.5 w-3.5 ${loading ? 'animate-spin' : ''}`} />
        </button>
      </div>

      {msg && (
        <div className={`flex items-center justify-between px-4 py-2.5 rounded-lg border text-sm ${msg.ok ? 'bg-emerald-500/10 border-emerald-500/20 text-emerald-400' : 'bg-rose-500/10 border-rose-500/20 text-rose-400'}`}>
          {msg.text}
          <button type="button" onClick={() => setMsg(null)}><X className="h-3.5 w-3.5" /></button>
        </div>
      )}

      {loading ? (
        <div className="flex items-center justify-center py-20">
          <div className="h-5 w-5 animate-spin rounded-full border-2 border-sapphire border-t-transparent" />
        </div>
      ) : (
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4">
          {plans.map(plan => {
            const key = plan.name.toLowerCase();
            const isEdit = editing === plan.name;
            return (
              <div key={plan.name} className="bg-[#161b22] border border-white/[0.07] rounded-xl overflow-hidden hover:border-white/15 transition-colors">
                <div className={`px-4 py-2.5 border-b border-white/[0.07] flex items-center justify-between`}>
                  <span className={`text-xs font-bold uppercase tracking-wider px-2 py-0.5 rounded ${PLAN_BADGE[key] ?? PLAN_BADGE.trial}`}>
                    {plan.name}
                  </span>
                </div>
                <div className="px-4 py-4 space-y-4">
                  {/* Price */}
                  <div className="flex items-center gap-2">
                    {isEdit ? (
                      <div className="flex items-center gap-1.5 flex-1">
                        <span className="text-slate-500 text-sm">$</span>
                        <input
                          type="number"
                          value={price}
                          onChange={e => setPrice(parseFloat(e.target.value) || 0)}
                          autoFocus
                          className="w-full bg-white/[0.05] border border-white/[0.12] rounded-lg px-2 py-1.5 text-sm text-white focus:outline-none focus:border-sapphire/60"
                        />
                        <span className="text-slate-600 text-xs">/mo</span>
                        <button type="button" onClick={() => savePrice(plan.name)} disabled={saving} className="text-emerald-400 hover:text-emerald-300 transition-colors disabled:opacity-40">
                          <Check className="h-4 w-4" />
                        </button>
                        <button type="button" onClick={() => setEditing(null)} className="text-slate-500 hover:text-white transition-colors">
                          <X className="h-4 w-4" />
                        </button>
                      </div>
                    ) : (
                      <div className="flex items-center gap-2 flex-1">
                        <span className="text-2xl font-bold text-white">
                          {plan.monthlyPrice === 0 ? 'Free' : `$${plan.monthlyPrice}`}
                        </span>
                        {plan.monthlyPrice > 0 && <span className="text-xs text-slate-600">/mo</span>}
                        <button type="button"
                          onClick={() => { setEditing(plan.name); setPrice(plan.monthlyPrice); }}
                          className="ml-auto text-slate-600 hover:text-white transition-colors">
                          <Edit3 className="h-3.5 w-3.5" />
                        </button>
                      </div>
                    )}
                  </div>

                  {/* Limits */}
                  <div className="space-y-1 text-xs text-slate-500">
                    <div className="flex justify-between">
                      <span>Max Users</span>
                      <span className="text-slate-300">{plan.maxUsers === 0 ? '∞' : plan.maxUsers}</span>
                    </div>
                    <div className="flex justify-between">
                      <span>Max Employees</span>
                      <span className="text-slate-300">{plan.maxEmployees === 0 ? '∞' : plan.maxEmployees}</span>
                    </div>
                  </div>

                  {/* Features */}
                  {PLAN_FEATURES[key] && (
                    <ul className="space-y-1">
                      {PLAN_FEATURES[key].map(f => (
                        <li key={f} className="flex items-center gap-1.5 text-xs text-slate-500">
                          <span className="text-emerald-500">✓</span> {f}
                        </li>
                      ))}
                    </ul>
                  )}
                </div>
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}
