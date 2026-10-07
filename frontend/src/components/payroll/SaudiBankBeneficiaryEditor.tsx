'use client';

// ANB Connect beneficiary fields (BIC + three employee address lines) for ONE employee at a time.
// Stored as versioned JSON inside the existing sensitive Employee.WpsBankDetails string and saved via
// the existing PUT /api/employees/{id}; the backend routes that sensitive field to the Approval
// Center (202). Nothing here approves, calls a bank, or touches account numbers / employee IDs.
// No logging, no localStorage.

import React, { useEffect, useId, useMemo, useRef, useState } from 'react';
import Link from 'next/link';
import { Loader2, RefreshCw, Send } from 'lucide-react';
import { employeesApi } from '../../api/employees';
import { extractApiError } from '../../api/saudiBankExports';
import { useLocale } from '../../contexts/LocaleContext';
import { useAuth } from '../../contexts/AuthContext';

const SCHEMA = 'saudi-bank-beneficiary-v1';
const ADDR = ['employeeAddress1', 'employeeAddress2', 'employeeAddress3'] as const;
type Form = { bicCode: string; employeeAddress1: string; employeeAddress2: string; employeeAddress3: string };
const EMPTY: Form = { bicCode: '', employeeAddress1: '', employeeAddress2: '', employeeAddress3: '' };
// eslint-disable-next-line no-control-regex
const CONTROL_RE = /[\u0000-\u001F\u007F]/;
const BIC_RE = /^[A-Z]{4}SA[A-Z0-9]{2}([A-Z0-9]{3})?$/;
const btnCls = 'inline-flex items-center gap-1.5 rounded-lg px-3 py-2 text-sm font-medium disabled:cursor-not-allowed disabled:opacity-50';

const EN = {
  title: 'Employee bank beneficiary details (ANB Connect)',
  help: 'BIC and three address lines per employee, stored in the employee\'s WPS bank details. Changes are submitted for separate approval — they are not applied until an approver decides. Account numbers and national ID / Iqama are changed only in People.',
  employee: 'Employee (from this batch)', choose: 'Choose an employee…', loading: 'Loading employee…', reload: 'Reload employee',
  bic: 'BIC (SWIFT) code', bicHelp: '8 or 11 uppercase characters, Saudi country code (e.g. ARNBSARI).', addr: (n: number) => `Employee address line ${n}`,
  submit: 'Submit for approval', submitting: 'Submitting…', noChange: 'No changes to submit.',
  required: 'Required.', tooLong: (l: number) => `Maximum 30 characters (currently ${l}). Nothing is cut automatically.`,
  control: 'Remove tabs or line breaks.', formula: 'Cannot start with =, +, - or @.', edge: 'Remove leading or trailing spaces.',
  bicBad: 'Must be 8 or 11 uppercase letters/digits with SA as the country code (characters 5–6).',
  masked: 'Your account cannot read this employee\'s existing WPS bank details, so they cannot be preserved. Ask a user with sensitive-data access to make this change.',
  foreign: (s: string) => `Existing details use an unrecognised schema "${s}". Not overwritten — review them in People.`,
  legacy: 'Existing plain-text WPS details will be kept as legacyNotes alongside the new fields.',
  pending: (ref: string) => `Submitted for separate approval${ref ? ` (request ${ref})` : ''}. Not applied yet. After an approver decides, reload the employee here and validate the batch again.`,
  applied: 'Saved by the server. Validate the batch again.',
  approvals: 'Open Approvals', people: 'Change account number or ID in People', failed: 'Could not submit the change.', loadFailed: 'Could not load this employee.',
  noEmployees: 'Open a batch with payment records to edit beneficiary details.',
};
const AR: typeof EN = {
  title: 'بيانات المستفيد البنكية للموظف (ANB Connect)',
  help: 'رمز BIC وثلاثة أسطر عنوان لكل موظف، تُحفظ ضمن بيانات حماية الأجور البنكية للموظف. تُرسل التغييرات لاعتماد منفصل ولا تُطبّق حتى يقرر المعتمِد. يُعدَّل رقم الحساب والهوية / الإقامة من صفحة الموظفين فقط.',
  employee: 'الموظف (من هذه الدفعة)', choose: 'اختر موظفًا…', loading: 'جارٍ تحميل الموظف…', reload: 'إعادة تحميل الموظف',
  bic: 'رمز BIC ‏(SWIFT)', bicHelp: '8 أو 11 حرفًا كبيرًا برمز الدولة SA (مثل ARNBSARI).', addr: (n) => `عنوان الموظف — السطر ${n}`,
  submit: 'إرسال للاعتماد', submitting: 'جارٍ الإرسال…', noChange: 'لا توجد تغييرات للإرسال.',
  required: 'مطلوب.', tooLong: (l) => `الحد الأقصى 30 حرفًا (الحالي ${l}). لا يُقص أي نص تلقائيًا.`,
  control: 'احذف مسافات الجدولة أو فواصل الأسطر.', formula: 'لا يمكن أن يبدأ بـ = أو + أو - أو @.', edge: 'احذف المسافات في البداية أو النهاية.',
  bicBad: 'يجب أن يكون 8 أو 11 حرفًا/رقمًا كبيرًا ورمز الدولة SA (الخانتان 5–6).',
  masked: 'لا يستطيع حسابك قراءة بيانات حماية الأجور البنكية الحالية لهذا الموظف، لذا لا يمكن الحفاظ عليها. اطلب من مستخدم لديه صلاحية البيانات الحساسة إجراء التغيير.',
  foreign: (s) => `البيانات الحالية تستخدم مخططًا غير معروف "${s}". لم يتم استبدالها — راجعها في صفحة الموظفين.`,
  legacy: 'ستُحفظ بيانات حماية الأجور النصية الحالية في legacyNotes مع الحقول الجديدة.',
  pending: (ref) => `أُرسل التغيير لاعتماد منفصل${ref ? ` (الطلب ${ref})` : ''}. لم يُطبّق بعد. بعد قرار المعتمِد أعد تحميل الموظف هنا ثم تحقق من الدفعة مجددًا.`,
  applied: 'حفظه الخادم. تحقق من الدفعة مجددًا.',
  approvals: 'فتح الاعتمادات', people: 'تعديل رقم الحساب أو الهوية من صفحة الموظفين', failed: 'تعذّر إرسال التغيير.', loadFailed: 'تعذّر تحميل هذا الموظف.',
  noEmployees: 'افتح دفعة تحتوي على سجلات دفع لتعديل بيانات المستفيد.',
};

type Parsed = { base: Record<string, unknown>; form: Form; block?: string; legacy?: boolean };

function parseExisting(raw: string, readable: boolean, c: typeof EN): Parsed {
  if (!raw || !raw.trim()) return readable ? { base: {}, form: EMPTY } : { base: {}, form: EMPTY, block: c.masked };
  let v: unknown;
  try { v = JSON.parse(raw); } catch { v = undefined; }
  if (!v || typeof v !== 'object' || Array.isArray(v)) return { base: { legacyNotes: raw }, form: EMPTY, legacy: true };
  const obj = v as Record<string, unknown>;
  if (obj.schema !== undefined && obj.schema !== SCHEMA) return { base: obj, form: EMPTY, block: c.foreign(String(obj.schema)) };
  const s = (k: keyof Form) => (typeof obj[k] === 'string' ? (obj[k] as string) : '');
  return { base: obj, form: { bicCode: s('bicCode'), employeeAddress1: s('employeeAddress1'), employeeAddress2: s('employeeAddress2'), employeeAddress3: s('employeeAddress3') } };
}

function checkField(k: keyof Form, v: string, c: typeof EN): string | null {
  if (!v) return c.required;
  if (CONTROL_RE.test(v)) return c.control;
  if (v !== v.trim()) return c.edge;
  if (k === 'bicCode') return BIC_RE.test(v) ? null : c.bicBad;
  if (/^[=+\-@]/.test(v)) return c.formula;
  return v.length > 30 ? c.tooLong(v.length) : null;
}

const riyadhToday = () => new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Riyadh', year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date());

type Loaded = { id: number; label: string; parsed: Parsed };

export function SaudiBankBeneficiaryEditor({ employeeIds, onChange }: { employeeIds: number[]; onChange?: () => void }) {
  const { locale } = useLocale();
  const c = locale === 'ar' ? AR : EN;
  const { hasPermission } = useAuth();
  // Mirrors the server's CanViewSensitive(): the employees.sensitive permission only, never a role name.
  // Without it WpsBankDetails comes back as '' (masked).
  const readable = hasPermission('employees.sensitive');
  const uid = useId();
  const ids = useMemo(() => Array.from(new Set(employeeIds)).sort((a, b) => a - b), [employeeIds.join(',')]); // eslint-disable-line react-hooks/exhaustive-deps

  const [selected, setSelected] = useState<number | null>(null);
  const [loaded, setLoaded] = useState<Loaded | null>(null);
  const [form, setForm] = useState<Form>(EMPTY);
  const [loading, setLoading] = useState(false);
  const [busy, setBusy] = useState(false);
  const [touched, setTouched] = useState(false);
  const [msg, setMsg] = useState<{ tone: 'ok' | 'warn' | 'error'; text: string } | null>(null);
  const reqRef = useRef(0); // stale-fetch guard: only the latest request for the current selection may land
  const mounted = useRef(true);
  useEffect(() => {
    mounted.current = true; // React StrictMode re-runs setup after its development cleanup.
    return () => { mounted.current = false; reqRef.current++; };
  }, []);

  const fetchOne = async (id: number, notifyParent: boolean) => {
    const req = ++reqRef.current;
    setLoading(true); setLoaded(null); setForm(EMPTY); setTouched(false);
    try {
      const e = await employeesApi.get(id);
      if (!mounted.current || req !== reqRef.current) return;
      const parsed = parseExisting(e.wpsBankDetails ?? '', readable, c);
      setLoaded({ id, label: `${e.employeeCode ? `${e.employeeCode} · ` : ''}${e.fullName || e.englishName || `#${id}`}`, parsed });
      setForm(parsed.form);
      if (notifyParent) onChange?.();
    } catch (err) {
      if (!mounted.current || req !== reqRef.current) return;
      setMsg({ tone: 'error', text: (await extractApiError(err, c.loadFailed)).message });
    } finally {
      if (mounted.current && req === reqRef.current) setLoading(false);
    }
  };

  const onSelect = (v: string) => {
    const id = v ? Number(v) : null;
    setSelected(id); setMsg(null);
    if (id == null) { reqRef.current++; setLoaded(null); setForm(EMPTY); setLoading(false); return; }
    void fetchOne(id, false);
  };

  const errors = useMemo(() => {
    const out: Partial<Record<keyof Form, string>> = {};
    (Object.keys(EMPTY) as (keyof Form)[]).forEach((k) => { const e = checkField(k, form[k], c); if (e) out[k] = e; });
    return out;
  }, [form, c]);
  const dirty = !!loaded && (Object.keys(EMPTY) as (keyof Form)[]).some((k) => form[k] !== loaded.parsed.form[k]);
  const blocked = loaded?.parsed.block;

  const onSubmit = async (ev: React.FormEvent) => {
    ev.preventDefault();
    setTouched(true);
    if (!loaded || busy || blocked || !dirty || Object.keys(errors).length) return;
    const { id, parsed } = loaded;
    const req = reqRef.current;
    // Preserve every existing key (or legacy text as legacyNotes); only the four ANB fields change.
    const next = { ...parsed.base, schema: SCHEMA, ...form };
    setBusy(true); setMsg(null);
    try {
      const res = await employeesApi.update(id, riyadhToday(), { wpsBankDetails: JSON.stringify(next) });
      onChange?.(); // any submitted change invalidates the parent's last validation
      if (!mounted.current || req !== reqRef.current) return;
      if (res.status === 202) {
        const ref = (res.data as { approvalRequestId?: string } | undefined)?.approvalRequestId ?? '';
        setMsg({ tone: 'warn', text: c.pending(ref ? ref.slice(0, 8) : '') });
        setLoaded({ ...loaded, parsed: { ...parsed, form: { ...form } } }); // submitted ≠ applied; keep inputs, block resubmit of same values
      } else {
        setMsg({ tone: 'ok', text: c.applied });
        void fetchOne(id, false);
      }
    } catch (err) {
      if (!mounted.current || req !== reqRef.current) return;
      setMsg({ tone: 'error', text: (await extractApiError(err, c.failed)).message });
    } finally {
      if (mounted.current) setBusy(false);
    }
  };

  const fid = (k: string) => `${uid}-${k}`;
  const field = (k: keyof Form, label: string, help?: string) => {
    const err = touched ? errors[k] : undefined;
    return (
      <div key={k}>
        <label htmlFor={fid(k)} className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-300">{label}</label>
        <input id={fid(k)} className={`input w-full ${err ? 'border-rose-400' : ''}`} value={form[k]} dir="ltr" autoComplete="off" spellCheck={false}
          disabled={!!blocked || busy} onChange={(e) => setForm((f) => ({ ...f, [k]: e.target.value }))}
          aria-invalid={err ? true : undefined} aria-describedby={err ? `${fid(k)}-err` : help ? `${fid(k)}-help` : undefined} />
        {err ? <p id={`${fid(k)}-err`} className="mt-1 text-[11px] font-medium text-rose-600 dark:text-rose-400">{err}</p>
          : help && <p id={`${fid(k)}-help`} className="mt-1 text-[11px] text-slate-400">{help}</p>}
      </div>
    );
  };
  const tone = { ok: 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400', warn: 'bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-400', error: 'bg-rose-50 text-rose-700 dark:bg-rose-500/10 dark:text-rose-400' };

  return (
    <section className="rounded-xl border border-slate-100 p-4 dark:border-white/10" aria-labelledby={fid('title')}>
      <h4 id={fid('title')} className="text-sm font-semibold text-slate-800 dark:text-white">{c.title}</h4>
      <p className="mt-0.5 text-xs text-slate-500 dark:text-slate-400">{c.help}</p>
      {ids.length === 0 ? <p className="mt-3 text-xs text-slate-400">{c.noEmployees}</p> : (
        <form onSubmit={onSubmit} noValidate className="mt-3 space-y-3">
          <div className="flex flex-wrap items-end gap-2">
            <div className="min-w-[12rem] flex-1">
              <label htmlFor={fid('emp')} className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-300">{c.employee}</label>
              <select id={fid('emp')} className="input w-full" value={selected ?? ''} onChange={(e) => onSelect(e.target.value)} disabled={busy}>
                <option value="">{c.choose}</option>
                {ids.map((id) => <option key={id} value={id}>#{id}{loaded?.id === id ? ` — ${loaded.label}` : ''}</option>)}
              </select>
            </div>
            {selected != null && (
              <button type="button" className={`${btnCls} border border-slate-200 text-slate-600 dark:border-white/10 dark:text-slate-300`}
                onClick={() => { setMsg(null); void fetchOne(selected, true); }} disabled={loading || busy}>
                <RefreshCw className="h-4 w-4" aria-hidden="true" /> {c.reload}
              </button>
            )}
          </div>
          {loading && <p role="status" className="flex items-center gap-2 text-xs text-slate-400"><Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />{c.loading}</p>}
          {loaded && !loading && (
            <>
              {blocked && <p role="alert" className={`rounded-lg px-3 py-2 text-xs ${tone.error}`}>{blocked}</p>}
              {loaded.parsed.legacy && <p className={`rounded-lg px-3 py-2 text-xs ${tone.warn}`}>{c.legacy}</p>}
              <div className="grid gap-3 sm:grid-cols-2">
                {field('bicCode', c.bic, c.bicHelp)}
                {ADDR.map((k, i) => field(k, c.addr(i + 1)))}
              </div>
              <div className="flex flex-wrap items-center gap-3">
                <button type="submit" className={`${btnCls} bg-sapphire text-white hover:bg-sapphire/90`} disabled={busy || !!blocked || !dirty}>
                  {busy ? <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" /> : <Send className="h-4 w-4" aria-hidden="true" />}
                  {busy ? c.submitting : c.submit}
                </button>
                {!dirty && !blocked && <span className="text-xs text-slate-400">{c.noChange}</span>}
                <Link href="/people" className="text-xs font-medium text-sapphire hover:underline">{c.people}</Link>
              </div>
            </>
          )}
          <div aria-live="polite">
            {msg && (
              <div className={`rounded-lg px-3 py-2 text-xs ${tone[msg.tone]}`}>
                <p role={msg.tone === 'error' ? 'alert' : undefined}>{msg.text}</p>
                {msg.tone === 'warn' && <Link href="/approvals" className="mt-1 inline-block font-medium underline">{c.approvals}</Link>}
              </div>
            )}
          </div>
        </form>
      )}
    </section>
  );
}

export default SaudiBankBeneficiaryEditor;
