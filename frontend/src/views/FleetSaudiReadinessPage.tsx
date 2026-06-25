'use client';

import { useEffect, useMemo, useState } from 'react';
import { AlertTriangle, BadgeCheck, ChevronRight, CloudLightning, FileText, Globe, RefreshCw, ShieldCheck, Sparkles, Truck } from 'lucide-react';
import { fleetReadinessApi, type FleetReadinessDocument, type FleetReadinessDocumentRequest, type FleetReadinessExpiry, type FleetInvoiceReadySummary, type SaudiRegionReference } from '../api/fleet';
import { notifyApiError } from '../api/client';

const initialForm: FleetReadinessDocumentRequest = {
  kind: 'Compliance',
  subjectType: 'Branch',
  subjectName: '',
  documentType: '',
  requiresExpiry: true,
  countryCode: 'SA',
  gregorianExpiryDate: new Date(Date.now() + 1000 * 60 * 60 * 24 * 180).toISOString().slice(0, 10),
};

function StatCard({ label, value, hint, tone }: { label: string; value: string; hint: string; tone: 'blue' | 'emerald' | 'amber' | 'violet' }) {
  const ring =
    tone === 'blue' ? 'from-blue-500/20 to-cyan-500/20 text-blue-600 dark:text-cyan-300' :
    tone === 'emerald' ? 'from-emerald-500/20 to-teal-500/20 text-emerald-600 dark:text-emerald-300' :
    tone === 'amber' ? 'from-amber-500/20 to-orange-500/20 text-amber-600 dark:text-amber-300' :
    'from-violet-500/20 to-fuchsia-500/20 text-violet-600 dark:text-violet-300';
  return (
    <div className="surface relative overflow-hidden p-4">
      <div className={`absolute inset-0 bg-gradient-to-br ${ring} opacity-60 blur-2xl`} />
      <div className="relative">
        <p className="text-xs font-semibold uppercase tracking-[0.28em] text-slate-400">{label}</p>
        <p className="mt-2 text-3xl font-black text-slate-900 dark:text-white">{value}</p>
        <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">{hint}</p>
      </div>
    </div>
  );
}

function Section({ title, icon: Icon, children, action }: { title: string; icon: React.ElementType; children: React.ReactNode; action?: React.ReactNode }) {
  return (
    <section className="surface overflow-hidden">
      <div className="flex items-center justify-between gap-3 border-b border-slate-100 px-5 py-4 dark:border-white/[0.07]">
        <div className="flex items-center gap-3">
          <span className="grid h-8 w-8 place-items-center rounded-xl bg-gradient-to-br from-sky-500/15 to-emerald-500/15">
            <Icon className="h-4 w-4 text-sky-600 dark:text-cyan-300" />
          </span>
          <h2 className="text-sm font-bold text-slate-900 dark:text-white">{title}</h2>
        </div>
        {action}
      </div>
      <div className="p-5">{children}</div>
    </section>
  );
}

function Field({ label, children, hint, required }: { label: string; children: React.ReactNode; hint?: string; required?: boolean }) {
  return (
    <label className="block">
      <span className="mb-1.5 block text-xs font-bold uppercase tracking-[0.22em] text-slate-500">
        {label}{required ? ' *' : ''}
      </span>
      {children}
      {hint && <span className="mt-1 block text-[11px] text-slate-400">{hint}</span>}
    </label>
  );
}

function TonePill({ tone, text }: { tone: 'emerald' | 'amber' | 'rose' | 'blue'; text: string }) {
  const cls =
    tone === 'emerald' ? 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/15 dark:text-emerald-300' :
    tone === 'amber' ? 'bg-amber-50 text-amber-700 dark:bg-amber-500/15 dark:text-amber-300' :
    tone === 'rose' ? 'bg-rose-50 text-rose-700 dark:bg-rose-500/15 dark:text-rose-300' :
    'bg-sky-50 text-sky-700 dark:bg-sky-500/15 dark:text-sky-300';
  return <span className={`inline-flex rounded-full px-2.5 py-1 text-[11px] font-bold ${cls}`}>{text}</span>;
}

export function FleetSaudiReadinessPage() {
  const [regions, setRegions] = useState<SaudiRegionReference[]>([]);
  const [documents, setDocuments] = useState<FleetReadinessDocument[]>([]);
  const [expiries, setExpiries] = useState<{ items: FleetReadinessExpiry[]; summary: { totalDocuments: number; expiringSoon: number; expired: number; healthy: number; windowDays: number } } | null>(null);
  const [invoiceReady, setInvoiceReady] = useState<FleetInvoiceReadySummary | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [saving, setSaving] = useState(false);
  const [form, setForm] = useState<FleetReadinessDocumentRequest>(initialForm);
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');

  const load = async () => {
    const [regionsRes, docsRes, expiryRes, invoiceRes] = await Promise.all([
      fleetReadinessApi.regions(),
      fleetReadinessApi.documents(),
      fleetReadinessApi.expiries(),
      fleetReadinessApi.invoiceReady(),
    ]);
    setRegions(regionsRes.items);
    setDocuments(docsRes.items);
    setExpiries(expiryRes);
    setInvoiceReady(invoiceRes);
  };

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    load()
      .catch((err) => { if (!cancelled) notifyApiError(err, 'Unable to load Saudi readiness data.'); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, []);

  const refresh = async () => {
    setRefreshing(true);
    try {
      await load();
      setMessage('Saudi readiness data refreshed.');
    } catch (err) {
      notifyApiError(err, 'Unable to refresh Saudi readiness data.');
    } finally {
      setRefreshing(false);
    }
  };

  const createDocument = async () => {
    setError('');
    setMessage('');
    if (!form.subjectName.trim() || !form.documentType.trim()) {
      setError('Subject name and document type are required.');
      return;
    }
    setSaving(true);
    try {
      const created = await fleetReadinessApi.createDocument(form);
      setDocuments((current) => [created, ...current]);
      setMessage('Readiness document saved.');
      setForm(initialForm);
      await refresh();
    } catch (err: any) {
      setError(err?.response?.data?.message ?? 'Failed to save document.');
    } finally {
      setSaving(false);
    }
  };

  const stats = useMemo(() => {
    const totalDocs = expiries?.summary.totalDocuments ?? documents.length;
    const expiring = expiries?.summary.expiringSoon ?? 0;
    const blockedInvoices = invoiceReady?.summary.blockedCount ?? 0;
    return [
      { label: 'Regions', value: regions.length.toString(), hint: 'Saudi/GCC reference set', tone: 'blue' as const },
      { label: 'Documents', value: totalDocs.toString(), hint: 'Compliance + transport + driver', tone: 'emerald' as const },
      { label: 'Expiring', value: expiring.toString(), hint: 'Needs follow-up inside 30 days', tone: 'amber' as const },
      { label: 'Invoice blocked', value: blockedInvoices.toString(), hint: 'Missing VAT / registration data', tone: 'violet' as const },
    ];
  }, [documents.length, expiries, invoiceReady, regions.length]);

  if (loading) {
    return (
      <div className="surface p-6">
        <div className="h-80 animate-pulse rounded-3xl bg-slate-100 dark:bg-white/[0.05]" />
      </div>
    );
  }

  return (
    <div className="relative space-y-6 overflow-hidden">
      <style>{`
        @keyframes kx-glow { 0%,100% { transform: scale(1); opacity: .35; } 50% { transform: scale(1.08); opacity: .65; } }
        @keyframes kx-rise { 0%,100% { transform: translateY(0); } 50% { transform: translateY(-10px); } }
        @keyframes kx-drift { from { transform: translateX(-4%) translateY(0); } to { transform: translateX(4%) translateY(-6px); } }
        .kx-glow { animation: kx-glow 9s ease-in-out infinite; }
        .kx-rise { animation: kx-rise 8s ease-in-out infinite; }
        .kx-drift { animation: kx-drift 16s ease-in-out infinite alternate; }
        @media (prefers-reduced-motion: reduce) {
          .kx-glow, .kx-rise, .kx-drift { animation: none; }
        }
      `}</style>

      <div className="surface relative overflow-hidden">
        <div className="absolute inset-0 bg-gradient-to-r from-sky-500/10 via-transparent to-emerald-500/10" />
        <div className="kx-glow absolute -left-16 top-0 h-56 w-56 rounded-full bg-sky-400/20 blur-3xl" />
        <div className="kx-drift absolute right-0 top-10 h-44 w-44 rounded-full bg-emerald-400/20 blur-3xl" />
        <div className="relative grid gap-6 px-6 py-6 lg:grid-cols-[1.25fr_0.75fr]">
          <div className="max-w-3xl">
            <div className="mb-4 inline-flex items-center gap-2 rounded-full border border-sky-200 bg-white/70 px-3 py-1 text-xs font-bold uppercase tracking-[0.28em] text-sky-700 shadow-sm backdrop-blur dark:border-sky-500/20 dark:bg-slate-900/70 dark:text-sky-300">
              <Sparkles className="h-3.5 w-3.5" />
              Saudi/GCC readiness foundation
            </div>
            <h1 className="text-3xl font-black tracking-tight text-slate-950 dark:text-white md:text-5xl">
              Compliance-ready freight operations for teams that need to move fast and stay auditable.
            </h1>
            <p className="mt-4 max-w-2xl text-base leading-7 text-slate-600 dark:text-slate-300">
              Use this workspace to keep Saudi regions, compliance documents, driver certifications, and invoice readiness visible in one place.
              It is intentionally integration-ready, tenant-scoped, and backed by live database data.
            </p>
            <div className="mt-5 flex flex-wrap gap-2">
              <TonePill tone="blue" text="Tenant-scoped" />
              <TonePill tone="emerald" text="DB-backed" />
              <TonePill tone="amber" text="Expiry-aware" />
              <TonePill tone="rose" text="Invoice-ready checks" />
            </div>
          </div>
          <div className="surface bg-white/70 p-4 backdrop-blur dark:bg-slate-950/60">
            <div className="flex items-center gap-2 text-sm font-bold text-slate-900 dark:text-white">
              <ShieldCheck className="h-4 w-4 text-emerald-500" />
              Operational posture
            </div>
            <div className="mt-4 space-y-3">
              <div className="flex items-center justify-between rounded-2xl bg-slate-50 px-4 py-3 dark:bg-white/[0.04]">
                <span className="text-sm text-slate-500">Regions loaded</span>
                <span className="font-bold text-slate-900 dark:text-white">{regions.length}</span>
              </div>
              <div className="flex items-center justify-between rounded-2xl bg-slate-50 px-4 py-3 dark:bg-white/[0.04]">
                <span className="text-sm text-slate-500">Documents tracked</span>
                <span className="font-bold text-slate-900 dark:text-white">{documents.length}</span>
              </div>
              <div className="flex items-center justify-between rounded-2xl bg-slate-50 px-4 py-3 dark:bg-white/[0.04]">
                <span className="text-sm text-slate-500">Invoice-ready shipments</span>
                <span className="font-bold text-slate-900 dark:text-white">{invoiceReady?.summary.readyCount ?? 0}</span>
              </div>
            </div>
          </div>
        </div>
      </div>

      <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-4">
        {stats.map((s) => <StatCard key={s.label} label={s.label} value={s.value} hint={s.hint} tone={s.tone} />)}
      </div>

      <div className="grid gap-4 xl:grid-cols-[1.1fr_0.9fr]">
        <Section
          title="Saudi regions and cities"
          icon={Globe}
          action={<button type="button" onClick={refresh} className="btn-secondary inline-flex items-center gap-2"><RefreshCw className={`h-4 w-4 ${refreshing ? 'animate-spin' : ''}`} /> Refresh</button>}
        >
          <div className="grid gap-3 sm:grid-cols-2">
            {regions.map((region) => (
              <div key={region.id} className="rounded-2xl border border-slate-100 bg-slate-50/70 p-4 dark:border-white/[0.06] dark:bg-white/[0.03]">
                <div className="flex items-start justify-between gap-3">
                  <div>
                    <p className="text-sm font-bold text-slate-900 dark:text-white">{region.nameEn}</p>
                    <p className="mt-0.5 text-xs text-slate-500">{region.nameAr}</p>
                  </div>
                  <TonePill tone={region.isGccReady ? 'emerald' : 'amber'} text={region.isGccReady ? 'GCC ready' : 'Review'} />
                </div>
                <p className="mt-3 text-xs uppercase tracking-[0.18em] text-slate-400">Cities</p>
                <p className="mt-1 text-sm text-slate-600 dark:text-slate-300">{region.cities.join(' · ')}</p>
              </div>
            ))}
          </div>
        </Section>

        <Section title="Invoice readiness" icon={Truck}>
          <div className="space-y-3">
            <div className="rounded-2xl border border-slate-100 bg-slate-50/70 p-4 dark:border-white/[0.06] dark:bg-white/[0.03]">
              <div className="flex items-center justify-between">
                <div>
                  <p className="text-xs uppercase tracking-[0.22em] text-slate-400">Readiness</p>
                  <p className="mt-1 text-2xl font-black text-slate-900 dark:text-white">{invoiceReady?.summary.readinessPercent ?? 0}%</p>
                </div>
                <CloudLightning className="h-9 w-9 text-sky-500" />
              </div>
              <div className="mt-4 h-2 overflow-hidden rounded-full bg-slate-200 dark:bg-white/[0.08]">
                <div className="h-full rounded-full bg-gradient-to-r from-sky-500 via-cyan-500 to-emerald-400" style={{ width: `${invoiceReady?.summary.readinessPercent ?? 0}%` }} />
              </div>
              <div className="mt-4 grid grid-cols-2 gap-3 text-sm">
                <div className="rounded-xl bg-white px-3 py-2 shadow-sm dark:bg-slate-950/60">
                  <p className="text-slate-400">Carrier ready</p>
                  <p className="font-bold text-slate-900 dark:text-white">{invoiceReady?.summary.carrierReady ?? 0}</p>
                </div>
                <div className="rounded-xl bg-white px-3 py-2 shadow-sm dark:bg-slate-950/60">
                  <p className="text-slate-400">Branch ready</p>
                  <p className="font-bold text-slate-900 dark:text-white">{invoiceReady?.summary.branchReady ?? 0}</p>
                </div>
              </div>
            </div>
            <div className="space-y-2">
              {(invoiceReady?.readyShipments ?? []).slice(0, 3).map((shipment) => (
                <div key={shipment.id} className="flex items-center justify-between rounded-2xl border border-emerald-100 bg-emerald-50/60 px-4 py-3 dark:border-emerald-500/20 dark:bg-emerald-500/10">
                  <div>
                    <p className="text-sm font-bold text-slate-900 dark:text-white">{shipment.shipmentNumber}</p>
                    <p className="text-xs text-slate-500">{shipment.customerName}</p>
                  </div>
                  <BadgeCheck className="h-4 w-4 text-emerald-500" />
                </div>
              ))}
            </div>
          </div>
        </Section>
      </div>

      <div className="grid gap-4 xl:grid-cols-[0.95fr_1.05fr]">
        <Section title="Compliance documents" icon={FileText}>
          <div className="space-y-4">
            <div className="grid gap-3 md:grid-cols-2">
              <Field label="Kind" required>
                <select className="input w-full" value={form.kind} onChange={(e) => setForm((x) => ({ ...x, kind: e.target.value }))}>
                  <option>Compliance</option>
                  <option>Transport</option>
                  <option>Driver</option>
                </select>
              </Field>
              <Field label="Subject Type" required>
                <select className="input w-full" value={form.subjectType} onChange={(e) => setForm((x) => ({ ...x, subjectType: e.target.value }))}>
                  <option>Branch</option>
                  <option>Carrier</option>
                  <option>Shipment</option>
                  <option>Driver</option>
                  <option>Vehicle</option>
                  <option>Customer</option>
                  <option>Location</option>
                </select>
              </Field>
              <Field label="Subject Name" required>
                <input className="input w-full" value={form.subjectName} onChange={(e) => setForm((x) => ({ ...x, subjectName: e.target.value }))} placeholder="Riyadh HQ" />
              </Field>
              <Field label="Document Type" required>
                <input className="input w-full" value={form.documentType} onChange={(e) => setForm((x) => ({ ...x, documentType: e.target.value }))} placeholder="Commercial Registration" />
              </Field>
              <Field label="Document Number">
                <input className="input w-full" value={form.documentNumber ?? ''} onChange={(e) => setForm((x) => ({ ...x, documentNumber: e.target.value }))} placeholder="CR-123456" />
              </Field>
              <Field label="Expiry Date">
                <input className="input w-full" type="date" value={form.gregorianExpiryDate ?? ''} onChange={(e) => setForm((x) => ({ ...x, gregorianExpiryDate: e.target.value || null }))} />
              </Field>
            </div>
            <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-4">
              <Field label="VAT Number">
                <input className="input w-full" value={form.vatNumber ?? ''} onChange={(e) => setForm((x) => ({ ...x, vatNumber: e.target.value }))} />
              </Field>
              <Field label="CR Number">
                <input className="input w-full" value={form.commercialRegistrationNo ?? ''} onChange={(e) => setForm((x) => ({ ...x, commercialRegistrationNo: e.target.value }))} />
              </Field>
              <Field label="Transport Doc #">
                <input className="input w-full" value={form.transportDocumentNo ?? ''} onChange={(e) => setForm((x) => ({ ...x, transportDocumentNo: e.target.value }))} />
              </Field>
              <Field label="Permit #">
                <input className="input w-full" value={form.permitNo ?? ''} onChange={(e) => setForm((x) => ({ ...x, permitNo: e.target.value }))} />
              </Field>
            </div>
            <Field label="Notes">
              <textarea className="input w-full min-h-24" value={form.notes ?? ''} onChange={(e) => setForm((x) => ({ ...x, notes: e.target.value }))} placeholder="Operational note, exception, or compliance reminder." />
            </Field>
            <div className="flex items-center gap-3">
              {(message || error) && (
                <div className={`rounded-xl px-3 py-2 text-sm ${error ? 'bg-rose-50 text-rose-700 dark:bg-rose-500/10 dark:text-rose-300' : 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-300'}`}>
                  {error || message}
                </div>
              )}
              <button type="button" onClick={createDocument} disabled={saving} className="btn-primary ml-auto inline-flex items-center gap-2 disabled:opacity-60">
                {saving ? <RefreshCw className="h-4 w-4 animate-spin" /> : <ShieldCheck className="h-4 w-4" />}
                Save readiness document
              </button>
            </div>
          </div>
        </Section>

        <Section title="Expiry radar" icon={AlertTriangle}>
          <div className="space-y-3">
            {(expiries?.items ?? []).slice(0, 8).map((item) => (
              <div key={item.id} className="rounded-2xl border border-slate-100 bg-slate-50/70 p-4 dark:border-white/[0.06] dark:bg-white/[0.03]">
                <div className="flex items-start justify-between gap-3">
                  <div>
                    <p className="text-sm font-bold text-slate-900 dark:text-white">{item.subjectName}</p>
                    <p className="mt-0.5 text-xs text-slate-500">{item.documentType} · {item.subjectType}</p>
                  </div>
                  <TonePill tone={item.daysRemaining < 0 ? 'rose' : item.daysRemaining <= 7 ? 'amber' : 'emerald'} text={item.daysRemaining < 0 ? 'Expired' : `${item.daysRemaining} days`} />
                </div>
                <div className="mt-3 flex items-center justify-between text-xs text-slate-500">
                  <span>{item.documentNumber || 'No document number'}</span>
                  <span>{item.expiryStatus}</span>
                </div>
              </div>
            ))}
          </div>
        </Section>
      </div>

      <Section title="Document ledger" icon={ChevronRight}>
        <div className="overflow-hidden rounded-2xl border border-slate-100 dark:border-white/[0.06]">
          <table className="min-w-full divide-y divide-slate-100 text-sm dark:divide-white/[0.06]">
            <thead className="bg-slate-50 text-left text-xs uppercase tracking-[0.18em] text-slate-500 dark:bg-white/[0.03]">
              <tr>
                <th className="px-4 py-3">Subject</th>
                <th className="px-4 py-3">Document</th>
                <th className="px-4 py-3">Type</th>
                <th className="px-4 py-3">Expiry</th>
                <th className="px-4 py-3">Status</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/[0.06]">
              {documents.slice(0, 8).map((doc) => (
                <tr key={doc.id} className="bg-white/80 dark:bg-slate-950/30">
                  <td className="px-4 py-3">
                    <div className="font-semibold text-slate-900 dark:text-white">{doc.subjectName}</div>
                    <div className="text-xs text-slate-500">{doc.subjectType} · {doc.kind}</div>
                  </td>
                  <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{doc.documentNumber || doc.transportDocumentNo || doc.permitNo || '—'}</td>
                  <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{doc.documentType}</td>
                  <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{doc.gregorianExpiryDate ? new Date(doc.gregorianExpiryDate).toLocaleDateString() : '—'}</td>
                  <td className="px-4 py-3"><TonePill tone={doc.expiryStatus === 'Expired' ? 'rose' : doc.expiryStatus === 'ExpiringSoon' ? 'amber' : 'emerald'} text={doc.expiryStatus} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Section>
    </div>
  );
}

export default FleetSaudiReadinessPage;
