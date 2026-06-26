'use client';

import { useState } from 'react';
import { Wand2, CheckCircle2, Trash2, Sparkles } from 'lucide-react';
import { setupAssistantApi, type CompanyProfile, type SetupDraft } from '../api/setupAssistant';

const COUNTRIES = [
  { code: 'SA', label: 'Saudi Arabia' }, { code: 'AE', label: 'United Arab Emirates' },
  { code: 'QA', label: 'Qatar' }, { code: 'KW', label: 'Kuwait' },
  { code: 'BH', label: 'Bahrain' }, { code: 'OM', label: 'Oman' },
  { code: 'EG', label: 'Egypt' }, { code: 'IN', label: 'India' }, { code: 'GB', label: 'United Kingdom' }, { code: 'US', label: 'United States' },
];
const SIZES = ['1-50', '51-200', '201-500', '500+'];
const CURRENCIES = ['SAR', 'AED', 'QAR', 'KWD', 'BHD', 'OMR', 'USD', 'EUR', 'GBP', 'INR', 'EGP'];

type SectionKey = 'org' | 'leave' | 'shifts' | 'payroll';

export function AiSetupAssistant() {
  const [country, setCountry] = useState('SA');
  const [industry, setIndustry] = useState('');
  const [size, setSize] = useState('51-200');
  const [currency, setCurrency] = useState('SAR');
  const [notes, setNotes] = useState('');
  const [sections, setSections] = useState<Record<SectionKey, boolean>>({ org: true, leave: true, shifts: true, payroll: true });

  const [loading, setLoading] = useState(false);
  const [applying, setApplying] = useState(false);
  const [error, setError] = useState('');
  const [engine, setEngine] = useState('');
  const [genNotes, setGenNotes] = useState<string[]>([]);
  const [draft, setDraft] = useState<SetupDraft | null>(null);
  const [done, setDone] = useState<{ applied: Record<string, number>; total: number } | null>(null);

  const toggle = (k: SectionKey) => setSections(s => ({ ...s, [k]: !s[k] }));

  const generate = async () => {
    if (!industry.trim()) { setError('Tell me your industry so the suggestions fit.'); return; }
    setLoading(true); setError(''); setDone(null);
    try {
      const profile: CompanyProfile = {
        countryCode: country, industry: industry.trim(), companySize: size, currencyCode: currency,
        notes: notes.trim() || undefined,
        sections: { org: sections.org, leave: sections.leave, shifts: sections.shifts, payroll: sections.payroll },
      };
      const r = await setupAssistantApi.preview(profile);
      setDraft(r.draft); setEngine(r.engine); setGenNotes(r.notes);
    } catch (e: unknown) {
      setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message ?? 'Could not generate a setup. Try again.');
    } finally { setLoading(false); }
  };

  const apply = async () => {
    if (!draft) return;
    setApplying(true); setError('');
    try {
      const r = await setupAssistantApi.apply(draft, country, currency);
      setDone(r);
    } catch (e: unknown) {
      setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message ?? 'Could not apply the setup.');
    } finally { setApplying(false); }
  };

  // remove a row from a draft list
  function removeAt<K extends keyof SetupDraft>(key: K, idx: number) {
    setDraft(d => {
      if (!d) return d;
      const list = d[key];
      if (!Array.isArray(list)) return d;
      return { ...d, [key]: list.filter((_, i) => i !== idx) };
    });
  }

  const totalItems = draft
    ? draft.departments.length + draft.designations.length + draft.grades.length +
      draft.leaveTypes.length + draft.shifts.length + draft.payComponents.length +
      draft.statutoryRules.length + (draft.workingWeek ? 1 : 0)
    : 0;

  if (done) {
    return (
      <div className="mx-auto max-w-md rounded-2xl border border-slate-200 bg-white p-8 text-center dark:border-white/10 dark:bg-white/[0.03]">
        <CheckCircle2 className="mx-auto mb-4 h-14 w-14 text-emerald-500" />
        <h3 className="text-lg font-bold text-slate-900 dark:text-white">Setup Applied</h3>
        <p className="mt-2 text-sm text-slate-500 dark:text-slate-400">
          Created <span className="font-semibold text-sapphire dark:text-cyanAccent">{done.total}</span> item(s):
        </p>
        <div className="mt-3 flex flex-wrap justify-center gap-1.5">
          {Object.entries(done.applied).map(([k, n]) => (
            <span key={k} className="rounded-full bg-slate-100 px-2.5 py-1 text-xs text-slate-600 dark:bg-white/10 dark:text-slate-300">{k}: {n}</span>
          ))}
        </div>
        <button type="button" className="btn-primary mt-6 w-full" onClick={() => { setDone(null); setDraft(null); }}>Run again</button>
      </div>
    );
  }

  return (
    <div className="space-y-5">
      {/* Intro */}
      <div className="flex items-start gap-3 rounded-xl border border-sapphire/20 bg-sapphire/[0.04] p-4 dark:border-cyanAccent/20 dark:bg-cyanAccent/[0.04]">
        <Sparkles className="mt-0.5 h-5 w-5 shrink-0 text-sapphire dark:text-cyanAccent" />
        <div>
          <h3 className="text-sm font-semibold text-slate-900 dark:text-white">AI Setup Assistant</h3>
          <p className="mt-0.5 text-xs text-slate-500 dark:text-slate-400">
            Describe your company and I&apos;ll propose a complete starter configuration — org structure, leave types, shifts, working week, payroll components and statutory rules. You review everything before it&apos;s applied. Nothing is saved until you click Apply.
          </p>
        </div>
      </div>

      {/* Guided form */}
      <div className="grid gap-4 rounded-xl border border-slate-200 bg-white p-5 dark:border-white/10 dark:bg-white/[0.03] sm:grid-cols-2">
        <label className="block">
          <span className="mb-1.5 block text-xs font-medium text-slate-700 dark:text-slate-300">Country</span>
          <select className="select w-full" value={country} onChange={e => { setCountry(e.target.value); setDraft(null); }}>
            {COUNTRIES.map(c => <option key={c.code} value={c.code}>{c.label}</option>)}
          </select>
        </label>
        <label className="block">
          <span className="mb-1.5 block text-xs font-medium text-slate-700 dark:text-slate-300">Industry</span>
          <input className="input w-full" value={industry} onChange={e => { setIndustry(e.target.value); setDraft(null); }} placeholder="e.g. Construction, Retail, Healthcare" />
        </label>
        <label className="block">
          <span className="mb-1.5 block text-xs font-medium text-slate-700 dark:text-slate-300">Company size</span>
          <select className="select w-full" value={size} onChange={e => setSize(e.target.value)}>
            {SIZES.map(s => <option key={s} value={s}>{s} employees</option>)}
          </select>
        </label>
        <label className="block">
          <span className="mb-1.5 block text-xs font-medium text-slate-700 dark:text-slate-300">Currency</span>
          <select className="select w-full" value={currency} onChange={e => setCurrency(e.target.value)}>
            {CURRENCIES.map(c => <option key={c} value={c}>{c}</option>)}
          </select>
        </label>
        <label className="block sm:col-span-2">
          <span className="mb-1.5 block text-xs font-medium text-slate-700 dark:text-slate-300">Anything specific? (optional)</span>
          <input className="input w-full" value={notes} onChange={e => setNotes(e.target.value)} placeholder="e.g. we run 24/7 operations with field crews" />
        </label>
        <div className="sm:col-span-2">
          <span className="mb-1.5 block text-xs font-medium text-slate-700 dark:text-slate-300">Generate</span>
          <div className="flex flex-wrap gap-2">
            {([['org', 'Org structure'], ['leave', 'Leave types'], ['shifts', 'Shifts & working week'], ['payroll', 'Payroll & statutory']] as [SectionKey, string][]).map(([k, label]) => (
              <button key={k} type="button" onClick={() => toggle(k)}
                className={`rounded-lg border px-3 py-1.5 text-xs font-medium transition ${
                  sections[k] ? 'border-transparent bg-sapphire text-white dark:bg-cyanAccent/80' : 'border-slate-200 text-slate-600 hover:border-slate-300 dark:border-white/10 dark:text-slate-300'
                }`}>
                {label}
              </button>
            ))}
          </div>
        </div>
      </div>

      {error && <p className="text-sm text-red-500">{error}</p>}

      <div className="flex items-center gap-3">
        <button type="button" className="btn-primary flex items-center gap-1.5" onClick={generate} disabled={loading}>
          <Wand2 className="h-4 w-4" />
          {loading ? 'Thinking…' : draft ? 'Regenerate' : 'Generate Setup'}
        </button>
        {draft && (
          <button type="button" className="btn-secondary flex items-center gap-1.5" onClick={apply} disabled={applying || totalItems === 0}>
            <CheckCircle2 className="h-4 w-4" />
            {applying ? 'Applying…' : `Apply ${totalItems} item(s)`}
          </button>
        )}
      </div>

      {/* Preview */}
      {draft && (
        <div className="space-y-4">
          <div className="flex flex-wrap items-center gap-2">
            <span className="rounded-full bg-sapphire/10 px-2.5 py-1 text-xs font-medium text-sapphire dark:bg-cyanAccent/10 dark:text-cyanAccent">{engine}</span>
            {genNotes.map((n, i) => <span key={i} className="text-xs text-amber-600 dark:text-amber-400">⚠ {n}</span>)}
          </div>

          <DraftSection title="Departments" rows={draft.departments.map(x => [x.code, x.nameEn])} onRemove={i => removeAt('departments', i)} />
          <DraftSection title="Designations" rows={draft.designations.map(x => [x.code, `${x.titleEn}${x.departmentCode ? ` · ${x.departmentCode}` : ''}${x.isManagerRole ? ' · Manager' : ''}`])} onRemove={i => removeAt('designations', i)} />
          <DraftSection title="Grades" rows={draft.grades.map(x => [x.code, `${x.name} (L${x.level})`])} onRemove={i => removeAt('grades', i)} />
          <DraftSection title="Leave Types" rows={draft.leaveTypes.map(x => [x.code, `${x.nameEn} · ${x.isPaid ? 'Paid' : 'Unpaid'} · max ${x.maxConsecutiveDays}d`])} onRemove={i => removeAt('leaveTypes', i)} />
          <DraftSection title="Shifts" rows={draft.shifts.map(x => [x.code, `${x.name} · ${x.start}–${x.end}`])} onRemove={i => removeAt('shifts', i)} />
          {draft.workingWeek && (
            <DraftSection title="Working Week" rows={[['WEEK', `${draft.workingWeek.workWeek} · starts ${draft.workingWeek.weekStartDay}`]]} onRemove={() => setDraft(d => d ? { ...d, workingWeek: null } : d)} />
          )}
          <DraftSection title="Payroll Components" rows={draft.payComponents.map(x => [x.code, `${x.name} · ${x.componentType} · ${x.calculationType === 'Percentage' ? `${x.percentage}%` : x.amount}`])} onRemove={i => removeAt('payComponents', i)} />
          <DraftSection title="Statutory Rules" rows={draft.statutoryRules.map(x => [x.ruleKey, `${x.ruleValue} — ${x.description}`])} onRemove={i => removeAt('statutoryRules', i)} />
        </div>
      )}
    </div>
  );
}

function DraftSection({ title, rows, onRemove }: { title: string; rows: [string, string][]; onRemove: (idx: number) => void }) {
  if (rows.length === 0) return null;
  return (
    <div className="rounded-xl border border-slate-200 dark:border-white/10">
      <div className="flex items-center justify-between border-b border-slate-100 px-4 py-2.5 dark:border-white/[0.06]">
        <h4 className="text-sm font-semibold text-slate-900 dark:text-white">{title}</h4>
        <span className="text-xs text-slate-400">{rows.length}</span>
      </div>
      <ul className="divide-y divide-slate-50 dark:divide-white/[0.04]">
        {rows.map(([code, desc], i) => (
          <li key={`${code}-${i}`} className="flex items-center gap-3 px-4 py-2 text-sm">
            <span className="rounded bg-slate-100 px-1.5 py-0.5 font-mono text-xs text-slate-600 dark:bg-white/10 dark:text-slate-300">{code}</span>
            <span className="text-slate-700 dark:text-slate-200">{desc}</span>
            <button type="button" aria-label={`Remove ${code}`} onClick={() => onRemove(i)}
              className="ml-auto grid h-6 w-6 place-items-center rounded text-slate-400 hover:bg-rose-50 hover:text-rose-500 dark:hover:bg-rose-500/10">
              <Trash2 className="h-3.5 w-3.5" />
            </button>
          </li>
        ))}
      </ul>
    </div>
  );
}
