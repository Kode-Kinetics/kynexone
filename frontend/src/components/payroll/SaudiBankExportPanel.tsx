'use client';

// Saudi bank-instruction export panel (IMPLEMENTATION-CONTRACT 2026-09-26).
//
// Scope, deliberately narrow:
//  • One supported format today: `anb-connect-csv-v1` — ANB Connect payroll-payment
//    channel, two CSV files (header.csv + body.csv). Not ANB corporate-portal WPY,
//    not "all Saudi banks", not Mudad. Bank acceptance is NOT verified.
//  • Company is resolved server-side from the batch (read-only here).
//  • Flow: save employer setup once → per-batch reference/date → Validate →
//    Generate (immutable, idempotent) → Download frozen zip.
//  • Generating does not pay anyone and submits nothing to a bank.
//
// Bank values live only in component state (form inputs). Nothing is logged or
// written to localStorage.

import React, { useCallback, useEffect, useId, useMemo, useRef, useState } from 'react';
import {
  AlertTriangle, CheckCircle2, Download, ExternalLink, FileText, Landmark, Loader2, RefreshCw, Save, ShieldCheck,
} from 'lucide-react';
import {
  saudiBankExportsApi, saveBlob, isAbortError, extractApiError,
  SAUDI_BANK_BATCH_TYPES,
  type SaudiBankExportContext, type SaudiBankExportFormat, type SaudiBankExportSettings,
  type SaudiBankExportRequest, type SaudiBankExportValidation, type SaudiBankExistingExport,
  type SaudiBankExportIssue,
} from '../../api/saudiBankExports';
import { useLocale } from '../../contexts/LocaleContext';
import { useAuth } from '../../contexts/AuthContext';
import { SaudiBankBeneficiaryEditor } from './SaudiBankBeneficiaryEditor';

// ── Styles (mirrors PayrollPage primitives; no new design language) ─────────────

const inp = 'w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 focus:border-sapphire focus:outline-none dark:border-white/10 dark:bg-white/5 dark:text-white';
const inpErr = 'border-rose-400 dark:border-rose-500/60';
const sel = `${inp} appearance-none`;
const btn = {
  primary: 'inline-flex items-center gap-1.5 rounded-lg bg-sapphire px-4 py-2 text-sm font-medium text-white hover:bg-sapphire/90 disabled:cursor-not-allowed disabled:opacity-50',
  ghost: 'inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50 dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5',
  link: 'text-xs font-medium text-sapphire underline-offset-2 hover:underline',
};

// ── Copy (EN / AR). Kept local: PayrollPage itself is not yet in the i18n dicts. ─

type Copy = {
  title: string; subtitle: string; loading: string; loadFailed: string; retry: string;
  notSaudi: (cc: string) => string; noFormats: string; unsupportedFormat: (id: string) => string;
  company: string; runStatus: string; format: string; bank: string; channel: string; source: string;
  reviewedOn: string; acceptance: string; disclaimerTitle: string; disclaimer: string; zipNote: string;
  legacyNote: string;
  setupTitle: string; setupHelp: string; molEstablishmentId: string; molHelp: string;
  mainAccountNumber: string; mainAccountHelp: string; organizationName: string;
  organizationAddress: (n: number) => string; companyName: string; narrative: string; batchType: string;
  selectBatchType: string; saveSetup: string; saving: string; saved: string; unsaved: string;
  noManagePerm: string; noExportPerm: string;
  batchTitle: string; batchReference: string; batchReferenceHelp: string; paymentDate: string; paymentDateHelp: string;
  validate: string; validating: string; generate: string; generating: string; download: string; downloading: string;
  needSave: string; needValidate: string; staleValidation: string; blocked: string; busy: string;
  validationOk: string; validationBlocked: (n: number) => string; warnings: string; employees: string; total: string;
  employee: (id: string) => string; field: string; goToField: string;
  artifactTitle: string; artifactId: string; files: string; generatedNote: string; existingDiffers: (ref: string, date: string) => string;
  existingMatches: string; downloadNeedsArtifact: string;
  required: string; tooLong: (max: number, len: number) => string; tooShort: (min: number) => string;
  controlChars: string; formulaPrefix: string; edgeSpaces: string; accountDigits: string; refDigits: string;
  dateRequired: string; fixFields: string; genericError: string; conflict: string;
};

const EN: Copy = {
  title: 'Bank instruction file — ANB Connect CSV',
  subtitle: 'Prepares the two ANB Connect payroll-payment CSV files (header.csv + body.csv) for this payment batch.',
  loading: 'Loading bank export details…',
  loadFailed: 'Could not load the bank export details for this batch.',
  retry: 'Retry',
  notSaudi: (cc) => `This batch belongs to a company with country code "${cc || 'unknown'}". The ANB Connect export applies only to Saudi (SA) companies. Other countries keep their existing WPS files unchanged.`,
  noFormats: 'The server returned no supported bank export formats. Nothing can be generated.',
  unsupportedFormat: (id) => `Saved format "${id}" is not supported by the server. Choose a supported format and save the employer setup again.`,
  company: 'Company (from batch, read-only)',
  runStatus: 'Payroll run status',
  format: 'Format',
  bank: 'Bank',
  channel: 'Channel',
  source: 'Specification source',
  reviewedOn: 'Specification reviewed',
  acceptance: 'Status',
  disclaimerTitle: 'This does not pay anyone',
  disclaimer: 'Generating these files moves no money and submits nothing to ANB. Pass the files to your approved ANB Connect integration/operator for submission and authorisation under your bank agreement. Bank acceptance of this file layout has not been verified — this is not a certified format, and it is not the ANB corporate-portal (WPY) upload or a format for other Saudi banks.',
  zipNote: 'The download is a zip for transport only. ANB Connect takes header.csv and body.csv as two separate files — extract them before uploading.',
  legacyNote: 'Separate from the "Legacy WPS/SIF" output in the batch list, which is internal and unverified.',
  setupTitle: 'Employer setup (saved per company)',
  setupHelp: 'Enter these once. They are your account and establishment facts as registered with ANB, not login credentials. Nothing is filled in automatically.',
  molEstablishmentId: 'MOL establishment ID',
  molHelp: '2–15 characters, as registered with the Ministry.',
  mainAccountNumber: 'ANB main account number',
  mainAccountHelp: 'Exactly 16 digits — your ANB employer account number, not an IBAN.',
  organizationName: 'Organisation name',
  organizationAddress: (n) => `Organisation address line ${n}`,
  companyName: 'Company name',
  narrative: 'Narrative',
  batchType: 'Batch type',
  selectBatchType: 'Select batch type…',
  saveSetup: 'Save employer setup',
  saving: 'Saving…',
  saved: 'Employer setup saved.',
  unsaved: 'Unsaved changes — save the employer setup before validating.',
  noManagePerm: 'Your account may lack the payroll structure permission needed to change the employer setup; the server will refuse the save if so.',
  noExportPerm: 'Your account may lack the payroll export permission; the server will refuse validation or generation if so.',
  batchTitle: 'This batch',
  batchReference: 'Batch number',
  batchReferenceHelp: '1–20 digits. Must be unique for this employer bank account across the workspace; also check references used outside KynexOne.',
  paymentDate: 'Credit value date',
  paymentDateHelp: 'Date the bank should credit employees.',
  validate: 'Validate',
  validating: 'Validating…',
  generate: 'Generate files',
  generating: 'Generating…',
  download: 'Download zip',
  downloading: 'Downloading…',
  needSave: 'Complete and save the employer setup first.',
  needValidate: 'Validate first — Generate is enabled only after a successful validation of the current inputs.',
  staleValidation: 'Inputs changed since the last validation. Validate again.',
  blocked: 'Resolve the blockers below, then validate again.',
  busy: 'Another action is in progress.',
  validationOk: 'Validation passed. No blockers found.',
  validationBlocked: (n) => `${n} blocker${n === 1 ? '' : 's'} must be resolved. Nothing is omitted — every listed item blocks the whole file.`,
  warnings: 'Warnings',
  employees: 'Employees',
  total: 'Total',
  employee: (id) => `Employee ${id}`,
  field: 'Field',
  goToField: 'Edit field',
  artifactTitle: 'Generated export (immutable)',
  artifactId: 'Export ID',
  files: 'Files and SHA-256',
  generatedNote: 'These files are frozen. Re-generating with the same inputs returns this same export; different inputs are refused.',
  existingDiffers: (ref, date) => `An export already exists for this batch (batch number ${ref}, date ${date}). Different inputs will be refused by the server.`,
  existingMatches: 'These inputs match the existing export.',
  downloadNeedsArtifact: 'Generate the files first.',
  required: 'Required.',
  tooLong: (max, len) => `Maximum ${max} characters (currently ${len}). Shorten it — nothing is cut automatically.`,
  tooShort: (min) => `At least ${min} characters.`,
  controlChars: 'Contains control characters (tabs or line breaks). Remove them.',
  formulaPrefix: 'Cannot start with =, +, - or @.',
  edgeSpaces: 'Remove leading or trailing spaces.',
  accountDigits: 'Must be exactly 16 digits (0–9).',
  refDigits: 'Must be 1–20 digits (0–9).',
  dateRequired: 'Choose a date.',
  fixFields: 'Fix the highlighted fields.',
  genericError: 'The request failed. Please try again.',
  conflict: 'The server refused this because it conflicts with an existing export or batch number.',
};

const AR: Copy = {
  title: 'ملف تعليمات البنك — ANB Connect CSV',
  subtitle: 'يجهّز ملفي CSV الخاصين بدفع الرواتب عبر ANB Connect ‏(header.csv و body.csv) لدفعة الدفع هذه.',
  loading: 'جارٍ تحميل تفاصيل التصدير البنكي…',
  loadFailed: 'تعذّر تحميل تفاصيل التصدير البنكي لهذه الدفعة.',
  retry: 'إعادة المحاولة',
  notSaudi: (cc) => `تتبع هذه الدفعة شركة رمز دولتها "${cc || 'غير معروف'}". تصدير ANB Connect مخصص للشركات السعودية (SA) فقط. تبقى ملفات حماية الأجور للدول الأخرى دون تغيير.`,
  noFormats: 'لم يُرجع الخادم أي صيغة تصدير بنكي مدعومة. لا يمكن إنشاء أي ملف.',
  unsupportedFormat: (id) => `الصيغة المحفوظة "${id}" غير مدعومة من الخادم. اختر صيغة مدعومة واحفظ إعدادات صاحب العمل مجددًا.`,
  company: 'الشركة (من الدفعة، للقراءة فقط)',
  runStatus: 'حالة مسير الرواتب',
  format: 'الصيغة',
  bank: 'البنك',
  channel: 'القناة',
  source: 'مصدر المواصفات',
  reviewedOn: 'تاريخ مراجعة المواصفات',
  acceptance: 'الحالة',
  disclaimerTitle: 'هذا لا يدفع لأحد',
  disclaimer: 'إنشاء هذه الملفات لا يحوّل أي أموال ولا يرسل شيئًا إلى البنك العربي الوطني. سلّم الملفات إلى مشغّل أو تكامل ANB Connect المعتمد لديك للإرسال والتفويض وفق اتفاقيتك مع البنك. لم يتم التحقق من قبول البنك لهذا التنسيق — ليست صيغة معتمدة، وليست صيغة رفع بوابة الشركات (WPY) ولا صيغة لبنوك سعودية أخرى.',
  zipNote: 'الملف المضغوط للنقل فقط. يستقبل ANB Connect الملفين header.csv و body.csv بشكل منفصل — استخرجهما قبل الرفع.',
  legacyNote: 'منفصل عن مخرجات "WPS/SIF القديمة" في قائمة الدفعات، وهي مخرجات داخلية غير موثّقة.',
  setupTitle: 'إعدادات صاحب العمل (تُحفظ لكل شركة)',
  setupHelp: 'أدخلها مرة واحدة. هي بيانات حسابك ومنشأتك المسجّلة لدى البنك، وليست بيانات دخول. لا يُملأ أي حقل تلقائيًا.',
  molEstablishmentId: 'رقم المنشأة لدى وزارة الموارد البشرية',
  molHelp: 'من 2 إلى 15 حرفًا كما هو مسجّل لدى الوزارة.',
  mainAccountNumber: 'رقم الحساب الرئيسي لدى البنك العربي الوطني',
  mainAccountHelp: '16 رقمًا بالضبط — رقم حساب صاحب العمل لدى البنك، وليس رقم آيبان.',
  organizationName: 'اسم المنظمة',
  organizationAddress: (n) => `عنوان المنظمة — السطر ${n}`,
  companyName: 'اسم الشركة',
  narrative: 'الوصف',
  batchType: 'نوع الدفعة',
  selectBatchType: 'اختر نوع الدفعة…',
  saveSetup: 'حفظ إعدادات صاحب العمل',
  saving: 'جارٍ الحفظ…',
  saved: 'تم حفظ إعدادات صاحب العمل.',
  unsaved: 'تغييرات غير محفوظة — احفظ إعدادات صاحب العمل قبل التحقق.',
  noManagePerm: 'قد لا يملك حسابك صلاحية إدارة هيكل الرواتب اللازمة لتعديل الإعدادات؛ وسيرفض الخادم الحفظ في هذه الحالة.',
  noExportPerm: 'قد لا يملك حسابك صلاحية تصدير الرواتب؛ وسيرفض الخادم التحقق أو الإنشاء في هذه الحالة.',
  batchTitle: 'هذه الدفعة',
  batchReference: 'رقم الدفعة',
  batchReferenceHelp: 'من 1 إلى 20 رقمًا، ويجب ألا يتكرر بين دفعات هذه الشركة المصدّرة.',
  paymentDate: 'تاريخ قيمة الإيداع',
  paymentDateHelp: 'التاريخ الذي يودع فيه البنك الرواتب.',
  validate: 'تحقق',
  validating: 'جارٍ التحقق…',
  generate: 'إنشاء الملفات',
  generating: 'جارٍ الإنشاء…',
  download: 'تنزيل الملف المضغوط',
  downloading: 'جارٍ التنزيل…',
  needSave: 'أكمل إعدادات صاحب العمل واحفظها أولًا.',
  needValidate: 'تحقق أولًا — يتاح الإنشاء فقط بعد نجاح التحقق من المدخلات الحالية.',
  staleValidation: 'تغيّرت المدخلات بعد آخر تحقق. تحقق مجددًا.',
  blocked: 'عالج العوائق أدناه ثم تحقق مجددًا.',
  busy: 'هناك إجراء آخر قيد التنفيذ.',
  validationOk: 'نجح التحقق. لا توجد عوائق.',
  validationBlocked: (n) => `يجب معالجة ${n} من العوائق. لا يُستبعد أي سجل — كل عنصر مدرج يمنع إنشاء الملف بالكامل.`,
  warnings: 'تنبيهات',
  employees: 'الموظفون',
  total: 'الإجمالي',
  employee: (id) => `الموظف ${id}`,
  field: 'الحقل',
  goToField: 'تعديل الحقل',
  artifactTitle: 'التصدير المُنشأ (غير قابل للتعديل)',
  artifactId: 'معرّف التصدير',
  files: 'الملفات وبصمة SHA-256',
  generatedNote: 'هذه الملفات مجمّدة. إعادة الإنشاء بنفس المدخلات تُرجع نفس التصدير، والمدخلات المختلفة تُرفض.',
  existingDiffers: (ref, date) => `يوجد تصدير سابق لهذه الدفعة (رقم الدفعة ${ref}، التاريخ ${date}). سيرفض الخادم أي مدخلات مختلفة.`,
  existingMatches: 'هذه المدخلات مطابقة للتصدير الموجود.',
  downloadNeedsArtifact: 'أنشئ الملفات أولًا.',
  required: 'مطلوب.',
  tooLong: (max, len) => `الحد الأقصى ${max} حرفًا (الحالي ${len}). اختصره — لا يُقص أي نص تلقائيًا.`,
  tooShort: (min) => `${min} أحرف على الأقل.`,
  controlChars: 'يحتوي على أحرف تحكم (مسافات جدولة أو فواصل أسطر). احذفها.',
  formulaPrefix: 'لا يمكن أن يبدأ بـ = أو + أو - أو @.',
  edgeSpaces: 'احذف المسافات في البداية أو النهاية.',
  accountDigits: 'يجب أن يكون 16 رقمًا بالضبط (0–9).',
  refDigits: 'يجب أن يكون من 1 إلى 20 رقمًا (0–9).',
  dateRequired: 'اختر تاريخًا.',
  fixFields: 'صحّح الحقول المحددة.',
  genericError: 'فشل الطلب. حاول مرة أخرى.',
  conflict: 'رفض الخادم الطلب لتعارضه مع تصدير أو رقم دفعة موجود.',
};

// ── Client-side checks (mirror the contract; the server remains authoritative) ─

type SettingsKey = Exclude<keyof SaudiBankExportSettings, 'formatId'>;
type BatchKey = keyof SaudiBankExportRequest;

const EMPTY_SETTINGS: SaudiBankExportSettings = {
  formatId: '', molEstablishmentId: '', mainAccountNumber: '', organizationName: '',
  organizationAddress1: '', organizationAddress2: '', organizationAddress3: '',
  companyName: '', narrative: '', batchType: '',
};
const SETTINGS_KEYS = Object.keys(EMPTY_SETTINGS) as (keyof SaudiBankExportSettings)[];
const FREE_TEXT_35: SettingsKey[] = [
  'organizationName', 'organizationAddress1', 'organizationAddress2', 'organizationAddress3', 'companyName', 'narrative',
];
const SAUDI_CODES = new Set(['SA', 'SAU', 'KSA']);
// eslint-disable-next-line no-control-regex
const CONTROL_RE = /[\u0000-\u001F\u007F]/;
const FORMULA_RE = /^[=+\-@]/;

function checkFreeText(v: string, min: number, max: number, c: Copy): string | null {
  if (!v) return c.required;
  if (CONTROL_RE.test(v)) return c.controlChars;
  if (v !== v.trim()) return c.edgeSpaces;
  if (FORMULA_RE.test(v)) return c.formulaPrefix;
  if (v.length < min) return c.tooShort(min);
  if (v.length > max) return c.tooLong(max, v.length);
  return null;
}

function checkSettings(s: SaudiBankExportSettings, c: Copy): Partial<Record<keyof SaudiBankExportSettings, string>> {
  const out: Partial<Record<keyof SaudiBankExportSettings, string>> = {};
  if (!s.formatId) out.formatId = c.required;
  const mol = checkFreeText(s.molEstablishmentId, 2, 15, c);
  if (mol) out.molEstablishmentId = mol;
  if (!s.mainAccountNumber) out.mainAccountNumber = c.required;
  else if (!/^[0-9]{16}$/.test(s.mainAccountNumber)) out.mainAccountNumber = c.accountDigits;
  for (const k of FREE_TEXT_35) {
    const e = checkFreeText(s[k], 1, 35, c);
    if (e) out[k] = e;
  }
  if (!(SAUDI_BANK_BATCH_TYPES as readonly string[]).includes(s.batchType)) out.batchType = c.required;
  return out;
}

function checkBatch(b: SaudiBankExportRequest, c: Copy): Partial<Record<BatchKey, string>> {
  const out: Partial<Record<BatchKey, string>> = {};
  if (!b.batchReference) out.batchReference = c.required;
  else if (!/^[0-9]{1,20}$/.test(b.batchReference)) out.batchReference = c.refDigits;
  if (!/^\d{4}-\d{2}-\d{2}$/.test(b.paymentDate)) out.paymentDate = c.dateRequired;
  return out;
}

function sameSettings(a: SaudiBankExportSettings | null, b: SaudiBankExportSettings): boolean {
  if (!a) return false;
  return SETTINGS_KEYS.every((k) => (a[k] ?? '') === (b[k] ?? ''));
}

function normaliseSettings(raw: Partial<SaudiBankExportSettings> | null | undefined): SaudiBankExportSettings {
  const out = { ...EMPTY_SETTINGS };
  for (const k of SETTINGS_KEYS) {
    const v = raw?.[k];
    out[k] = typeof v === 'string' ? v : '';
  }
  return out;
}

function fmtMoney(n: number, currency: string) {
  // Up to 6 fraction digits so an out-of-spec value is visible rather than rounded away.
  return `${currency ? `${currency} ` : ''}${n.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 6 })}`;
}

// ── Small presentational helpers ────────────────────────────────────────────────

function FieldShell({ id, label, help, error, children }: {
  id: string; label: string; help?: string; error?: string | null; children: React.ReactNode;
}) {
  return (
    <div>
      <label htmlFor={id} className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-300">{label}</label>
      {children}
      {help && !error && <p id={`${id}-help`} className="mt-1 text-[11px] text-slate-400">{help}</p>}
      {error && <p id={`${id}-err`} className="mt-1 text-[11px] font-medium text-rose-600 dark:text-rose-400">{error}</p>}
    </div>
  );
}

function Alert({ tone, children, icon = true }: { tone: 'error' | 'warn' | 'info' | 'ok'; children: React.ReactNode; icon?: boolean }) {
  const cls = {
    error: 'bg-rose-50 text-rose-700 dark:bg-rose-500/10 dark:text-rose-400',
    warn: 'bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-400',
    info: 'bg-slate-50 text-slate-600 dark:bg-white/5 dark:text-slate-300',
    ok: 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400',
  }[tone];
  const Icon = tone === 'ok' ? CheckCircle2 : AlertTriangle;
  return (
    <div className={`flex items-start gap-2 rounded-lg px-4 py-2.5 text-sm ${cls}`}>
      {icon && <Icon className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />}
      <div className="min-w-0 flex-1">{children}</div>
    </div>
  );
}

// ── Panel ───────────────────────────────────────────────────────────────────────

type Busy = null | 'save' | 'validate' | 'generate' | 'download';
type LoadState = { status: 'loading' } | { status: 'error'; message: string } | { status: 'ready' };
type ActionError = { message: string; issues?: SaudiBankExportIssue[] } | null;

export function SaudiBankExportPanel({ batchId, employeeIds }: { batchId: string; employeeIds?: number[] }) {
  const { locale } = useLocale();
  const c = locale === 'ar' ? AR : EN;
  const { hasPermission } = useAuth();
  const uid = useId();
  const fid = (k: string) => `${uid}-${k}`;

  const [load, setLoad] = useState<LoadState>({ status: 'loading' });
  const [reloadToken, setReloadToken] = useState(0);
  const [ctx, setCtx] = useState<SaudiBankExportContext | null>(null);
  const [formats, setFormats] = useState<SaudiBankExportFormat[]>([]);
  const [savedSettings, setSavedSettings] = useState<SaudiBankExportSettings | null>(null);
  const [draft, setDraft] = useState<SaudiBankExportSettings>(EMPTY_SETTINGS);
  const [batchInput, setBatchInput] = useState<SaudiBankExportRequest>({ batchReference: '', paymentDate: '' });
  const [validation, setValidation] = useState<{ key: string; result: SaudiBankExportValidation } | null>(null);
  const [artifact, setArtifact] = useState<SaudiBankExistingExport | null>(null);
  const [busy, setBusy] = useState<Busy>(null);
  const [actionError, setActionError] = useState<ActionError>(null);
  const [notice, setNotice] = useState('');
  const [showSettingsErrors, setShowSettingsErrors] = useState(false);
  const [showBatchErrors, setShowBatchErrors] = useState(false);

  // Stale-response guards: the batch this panel currently represents, a sync busy
  // flag (blocks double clicks before React re-renders) and the in-flight action.
  const activeBatchRef = useRef(batchId);
  const busyRef = useRef(false);
  const actionAbortRef = useRef<AbortController | null>(null);
  const resultRef = useRef<HTMLDivElement>(null);

  // ── Load: context first (gives companyId), then formats + settings in parallel.
  useEffect(() => {
    const ac = new AbortController();
    activeBatchRef.current = batchId;
    actionAbortRef.current?.abort();
    actionAbortRef.current = null;
    busyRef.current = false;
    setBusy(null);
    setLoad({ status: 'loading' });
    setCtx(null);
    setFormats([]);
    setSavedSettings(null);
    setDraft(EMPTY_SETTINGS);
    setBatchInput({ batchReference: '', paymentDate: '' });
    setValidation(null);
    setArtifact(null);
    setActionError(null);
    setNotice('');
    setShowSettingsErrors(false);
    setShowBatchErrors(false);

    (async () => {
      try {
        const context = await saudiBankExportsApi.getContext(batchId, { signal: ac.signal });
        if (ac.signal.aborted) return;
        setCtx(context);
        const existing = context.existingExport ?? null;
        setArtifact(existing);
        if (existing) {
          setBatchInput({ batchReference: existing.batchReference ?? '', paymentDate: (existing.paymentDate ?? '').slice(0, 10) });
        }
        if (!SAUDI_CODES.has((context.countryCode ?? '').toUpperCase())) {
          setLoad({ status: 'ready' });
          return;
        }
        const [fmts, settings] = await Promise.all([
          saudiBankExportsApi.listFormats({ signal: ac.signal }),
          saudiBankExportsApi.getSettings(context.companyId, { signal: ac.signal }),
        ]);
        if (ac.signal.aborted) return;
        const norm = normaliseSettings(settings);
        setFormats(Array.isArray(fmts) ? fmts : []);
        setSavedSettings(norm);
        setDraft(norm);
        setLoad({ status: 'ready' });
      } catch (err) {
        if (ac.signal.aborted || isAbortError(err)) return;
        const { message } = await extractApiError(err, c.loadFailed);
        if (ac.signal.aborted) return;
        setLoad({ status: 'error', message });
      }
    })();

    return () => ac.abort();
    // `c` intentionally excluded: a locale switch must not refetch or wipe the form.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [batchId, reloadToken]);

  useEffect(() => () => actionAbortRef.current?.abort(), []);

  // ── Derived state
  const isSaudi = !!ctx && SAUDI_CODES.has((ctx.countryCode ?? '').toUpperCase());
  const settingsErrors = useMemo(() => checkSettings(draft, c), [draft, c]);
  const batchErrors = useMemo(() => checkBatch(batchInput, c), [batchInput, c]);
  const settingsDirty = !sameSettings(savedSettings, draft);
  const savedSettingsValid = !!savedSettings && Object.keys(checkSettings(savedSettings, c)).length === 0;
  const selectedFormat = formats.find((f) => f.id === draft.formatId) ?? null;
  const savedFormatUnsupported = !!savedSettings?.formatId && formats.length > 0 && !formats.some((f) => f.id === savedSettings.formatId);
  const inputsKey = JSON.stringify({ s: savedSettings, b: batchInput });
  const validationCurrent = !!validation && validation.key === inputsKey && !settingsDirty;
  const validationStale = !!validation && !validationCurrent;
  const canGenerate = validationCurrent && validation!.result.canExport && busy === null;
  const existingMatches = !!artifact
    && artifact.batchReference === batchInput.batchReference
    && (artifact.paymentDate ?? '').slice(0, 10) === batchInput.paymentDate;

  // Pre-select the only supported format when nothing is saved yet (explicit, visible choice).
  useEffect(() => {
    if (load.status !== 'ready' || formats.length !== 1) return;
    setDraft((d) => (d.formatId ? d : { ...d, formatId: formats[0].id }));
  }, [load.status, formats]);

  const setField = useCallback((k: keyof SaudiBankExportSettings, v: string) => {
    setDraft((d) => ({ ...d, [k]: v }));
    setNotice('');
  }, []);
  const setBatchField = useCallback((k: BatchKey, v: string) => {
    setBatchInput((b) => ({ ...b, [k]: v }));
    setNotice('');
  }, []);

  const focusField = (k: string) => {
    const el = document.getElementById(fid(k));
    if (el) { el.focus(); el.scrollIntoView({ block: 'center', behavior: 'smooth' }); }
  };

  // Runs an action with double-click, stale-batch and abort protection.
  const runAction = async (kind: Exclude<Busy, null>, fn: (signal: AbortSignal) => Promise<void>, fallback: string) => {
    if (busyRef.current) return;
    busyRef.current = true;
    const startedFor = batchId;
    const ac = new AbortController();
    actionAbortRef.current = ac;
    setBusy(kind);
    setActionError(null);
    setNotice('');
    try {
      await fn(ac.signal);
    } catch (err) {
      if (ac.signal.aborted || isAbortError(err) || activeBatchRef.current !== startedFor) return;
      const { status, message, issues } = await extractApiError(err, fallback);
      if (activeBatchRef.current !== startedFor) return;
      if (status === 401 || status === 402) return; // handled globally
      setActionError({ message: status === 409 && message === fallback ? c.conflict : message, issues });
    } finally {
      if (activeBatchRef.current === startedFor) {
        busyRef.current = false;
        setBusy(null);
        if (actionAbortRef.current === ac) actionAbortRef.current = null;
      }
    }
  };

  const onSave = (e?: React.FormEvent) => {
    e?.preventDefault();
    setShowSettingsErrors(true);
    const firstBad = SETTINGS_KEYS.find((k) => settingsErrors[k]);
    if (firstBad) { setActionError({ message: c.fixFields }); focusField(firstBad); return; }
    if (!ctx) return;
    const startedFor = batchId;
    const payload = { ...draft };
    void runAction('save', async (signal) => {
      const saved = normaliseSettings(await saudiBankExportsApi.saveSettings(ctx.companyId, payload, { signal }));
      if (activeBatchRef.current !== startedFor) return;
      setSavedSettings(saved);
      // Keep any edits made while the save was in flight; otherwise adopt the server copy.
      setDraft((d) => (sameSettings(payload, d) ? saved : d));
      setShowSettingsErrors(false);
      setNotice(c.saved);
    }, c.genericError);
  };

  const onValidate = () => {
    setShowBatchErrors(true);
    if (settingsDirty) { setActionError({ message: c.needSave }); return; }
    const firstBad = (['batchReference', 'paymentDate'] as BatchKey[]).find((k) => batchErrors[k]);
    if (firstBad) { setActionError({ message: c.fixFields }); focusField(firstBad); return; }
    const startedFor = batchId;
    const keyAtStart = inputsKey;
    const body = { ...batchInput };
    void runAction('validate', async (signal) => {
      const result = await saudiBankExportsApi.validate(batchId, body, { signal });
      if (activeBatchRef.current !== startedFor) return;
      setValidation({ key: keyAtStart, result });
      requestAnimationFrame(() => resultRef.current?.focus());
    }, c.genericError);
  };

  const onGenerate = () => {
    if (!canGenerate) return;
    const startedFor = batchId;
    const body = { ...batchInput };
    void runAction('generate', async (signal) => {
      const meta = await saudiBankExportsApi.generate(batchId, body, { signal });
      if (activeBatchRef.current !== startedFor) return;
      const frozen: SaudiBankExistingExport = {
        id: meta.id, formatId: meta.formatId, files: meta.files ?? [], batchReference: meta.batchReference,
        paymentDate: meta.paymentDate, employeeCount: meta.employeeCount, totalAmount: meta.totalAmount,
      };
      setArtifact(frozen);
      setCtx((prev) => (prev ? { ...prev, existingExport: frozen } : prev));
      requestAnimationFrame(() => resultRef.current?.focus());
    }, c.genericError);
  };

  const onDownload = () => {
    if (!artifact) return;
    const startedFor = batchId;
    void runAction('download', async (signal) => {
      const file = await saudiBankExportsApi.download(batchId, { signal });
      if (activeBatchRef.current !== startedFor) return;
      saveBlob(file);
    }, c.genericError);
  };

  // ── Render ────────────────────────────────────────────────────────────────────

  const header = (
    <div className="flex items-start gap-3 border-b border-slate-100 px-4 py-3 dark:border-white/5">
      <Landmark className="mt-0.5 h-5 w-5 shrink-0 text-sapphire" aria-hidden="true" />
      <div className="min-w-0">
        <h3 className="text-sm font-semibold text-slate-800 dark:text-white">{c.title}</h3>
        <p className="mt-0.5 text-xs text-slate-500 dark:text-slate-400">{c.subtitle}</p>
      </div>
    </div>
  );

  if (load.status === 'loading') {
    return (
      <section className="surface overflow-hidden" aria-busy="true" aria-label={c.title}>
        {header}
        <div className="flex items-center gap-2 px-4 py-8 text-sm text-slate-400" role="status">
          <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" /> {c.loading}
        </div>
      </section>
    );
  }

  if (load.status === 'error') {
    return (
      <section className="surface overflow-hidden" aria-label={c.title}>
        {header}
        <div className="space-y-3 p-4">
          <Alert tone="error"><span role="alert">{load.message}</span></Alert>
          <button type="button" className={btn.ghost} onClick={() => setReloadToken((n) => n + 1)}>
            <RefreshCw className="h-4 w-4" aria-hidden="true" /> {c.retry}
          </button>
        </div>
      </section>
    );
  }

  if (ctx && !isSaudi) {
    return (
      <section className="surface overflow-hidden" aria-label={c.title}>
        {header}
        <div className="p-4"><Alert tone="info">{c.notSaudi(ctx.countryCode)}</Alert></div>
      </section>
    );
  }

  const sErr = (k: keyof SaudiBankExportSettings) => (showSettingsErrors ? settingsErrors[k] ?? null : null);
  const bErr = (k: BatchKey) => (showBatchErrors ? batchErrors[k] ?? null : null);
  const describedBy = (k: string, err: string | null, hasHelp: boolean) =>
    err ? `${fid(k)}-err` : hasHelp ? `${fid(k)}-help` : undefined;

  const textInput = (k: SettingsKey, label: string, opts: { help?: string; ltr?: boolean; numeric?: boolean } = {}) => {
    const err = sErr(k);
    const len = draft[k].length;
    return (
      <FieldShell key={k} id={fid(k)} label={label} help={opts.help} error={err}>
        <input
          id={fid(k)}
          name={k}
          className={`${inp} ${err ? inpErr : ''}`}
          value={draft[k]}
          onChange={(e) => setField(k, e.target.value)}
          aria-invalid={err ? true : undefined}
          aria-describedby={describedBy(k, err, !!opts.help)}
          autoComplete="off"
          spellCheck={false}
          dir={opts.ltr ? 'ltr' : undefined}
          inputMode={opts.numeric ? 'numeric' : undefined}
        />
        {FREE_TEXT_35.includes(k) && (
          <p className={`mt-0.5 text-end text-[10px] tabular-nums ${len > 35 ? 'text-rose-500' : 'text-slate-400'}`} aria-hidden="true">{len}/35</p>
        )}
      </FieldShell>
    );
  };

  // Reason text for any disabled action button — never disabled silently.
  const validateReason = busy ? c.busy : settingsDirty ? c.needSave : !savedSettingsValid ? c.needSave : savedFormatUnsupported ? c.unsupportedFormat(savedSettings?.formatId ?? '') : null;
  const generateReason = busy ? c.busy
    : !validation ? c.needValidate
    : validationStale ? c.staleValidation
    : !validation.result.canExport ? c.blocked
    : null;
  const downloadReason = busy ? c.busy : !artifact ? c.downloadNeedsArtifact : null;

  const result = validationCurrent ? validation!.result : null;

  return (
    <section className="surface overflow-hidden" aria-label={c.title}>
      {header}
      <div className="space-y-4 p-4">
        {/* Format facts + disclaimer */}
        <dl className="grid gap-x-6 gap-y-2 text-xs sm:grid-cols-2">
          <div><dt className="text-slate-400">{c.company}</dt><dd className="font-medium text-slate-800 dark:text-white">{ctx?.companyName}</dd></div>
          <div><dt className="text-slate-400">{c.runStatus}</dt><dd className="font-medium text-slate-800 dark:text-white">{ctx?.runStatus}</dd></div>
          {selectedFormat && (
            <>
              <div><dt className="text-slate-400">{c.bank} · {c.channel}</dt><dd className="font-medium text-slate-800 dark:text-white">{selectedFormat.bank} · {selectedFormat.channel}</dd></div>
              <div><dt className="text-slate-400">{c.acceptance}</dt><dd className="font-mono text-amber-700 dark:text-amber-400">{selectedFormat.acceptanceStatus}</dd></div>
              <div><dt className="text-slate-400">{c.reviewedOn}</dt><dd className="text-slate-700 dark:text-slate-300">{selectedFormat.reviewedOn}</dd></div>
              <div className="min-w-0"><dt className="text-slate-400">{c.source}</dt>
                <dd className="truncate">
                  <a href={selectedFormat.sourceUrl} target="_blank" rel="noopener noreferrer" className={`${btn.link} inline-flex items-center gap-1`} dir="ltr" title={selectedFormat.sourceUrl}>
                    {selectedFormat.sourceUrl} <ExternalLink className="h-3 w-3 shrink-0" aria-hidden="true" />
                  </a>
                </dd>
              </div>
            </>
          )}
        </dl>

        <Alert tone="warn">
          <p className="font-semibold">{c.disclaimerTitle}</p>
          <p className="mt-1 text-xs">{c.disclaimer}</p>
          <p className="mt-1 text-xs">{c.legacyNote}</p>
        </Alert>

        {formats.length === 0 && <Alert tone="error">{c.noFormats}</Alert>}
        {savedFormatUnsupported && <Alert tone="error">{c.unsupportedFormat(savedSettings?.formatId ?? '')}</Alert>}
        {!hasPermission('payroll.export') && <Alert tone="info">{c.noExportPerm}</Alert>}

        {/* Employer setup */}
        <form onSubmit={onSave} noValidate className="rounded-xl border border-slate-100 p-4 dark:border-white/10" aria-labelledby={fid('setup-title')}>
          <h4 id={fid('setup-title')} className="text-sm font-semibold text-slate-800 dark:text-white">{c.setupTitle}</h4>
          <p className="mt-0.5 text-xs text-slate-500 dark:text-slate-400">{c.setupHelp}</p>
          {!hasPermission('payroll.structure_manage') && <p className="mt-2 text-xs text-amber-700 dark:text-amber-400">{c.noManagePerm}</p>}
          <div className="mt-3 grid gap-3 sm:grid-cols-2">
            <FieldShell id={fid('formatId')} label={c.format} error={sErr('formatId')}>
              <select id={fid('formatId')} className={`${sel} ${sErr('formatId') ? inpErr : ''}`} value={draft.formatId}
                onChange={(e) => setField('formatId', e.target.value)} aria-invalid={sErr('formatId') ? true : undefined}
                aria-describedby={describedBy('formatId', sErr('formatId'), false)}>
                <option value="">—</option>
                {formats.map((f) => <option key={f.id} value={f.id}>{f.name}</option>)}
              </select>
            </FieldShell>
            <FieldShell id={fid('batchType')} label={c.batchType} error={sErr('batchType')}>
              <select id={fid('batchType')} className={`${sel} ${sErr('batchType') ? inpErr : ''}`} value={draft.batchType}
                onChange={(e) => setField('batchType', e.target.value)} aria-invalid={sErr('batchType') ? true : undefined}
                aria-describedby={describedBy('batchType', sErr('batchType'), false)}>
                <option value="">{c.selectBatchType}</option>
                {SAUDI_BANK_BATCH_TYPES.map((t) => <option key={t} value={t}>{t}</option>)}
              </select>
            </FieldShell>
            {textInput('molEstablishmentId', c.molEstablishmentId, { help: c.molHelp, ltr: true })}
            {textInput('mainAccountNumber', c.mainAccountNumber, { help: c.mainAccountHelp, ltr: true, numeric: true })}
            {textInput('organizationName', c.organizationName)}
            {textInput('companyName', c.companyName)}
            {textInput('organizationAddress1', c.organizationAddress(1))}
            {textInput('organizationAddress2', c.organizationAddress(2))}
            {textInput('organizationAddress3', c.organizationAddress(3))}
            {textInput('narrative', c.narrative)}
          </div>
          <div className="mt-3 flex flex-wrap items-center gap-3">
            <button type="submit" className={btn.primary} disabled={busy !== null} aria-describedby={busy ? fid('save-reason') : undefined}>
              {busy === 'save' ? <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" /> : <Save className="h-4 w-4" aria-hidden="true" />}
              {busy === 'save' ? c.saving : c.saveSetup}
            </button>
            {busy && busy !== 'save' && <span id={fid('save-reason')} className="text-xs text-slate-400">{c.busy}</span>}
            {settingsDirty && <span className="text-xs text-amber-700 dark:text-amber-400">{c.unsaved}</span>}
          </div>
        </form>

        {/* Per-batch inputs + actions */}
        <div className="rounded-xl border border-slate-100 p-4 dark:border-white/10">
          <h4 className="text-sm font-semibold text-slate-800 dark:text-white">{c.batchTitle}</h4>
          <div className="mt-3 grid gap-3 sm:grid-cols-2">
            <FieldShell id={fid('batchReference')} label={c.batchReference} help={c.batchReferenceHelp} error={bErr('batchReference')}>
              <input id={fid('batchReference')} className={`${inp} ${bErr('batchReference') ? inpErr : ''}`} value={batchInput.batchReference}
                onChange={(e) => setBatchField('batchReference', e.target.value)} inputMode="numeric" dir="ltr" autoComplete="off"
                aria-invalid={bErr('batchReference') ? true : undefined}
                aria-describedby={describedBy('batchReference', bErr('batchReference'), true)} />
            </FieldShell>
            <FieldShell id={fid('paymentDate')} label={c.paymentDate} help={c.paymentDateHelp} error={bErr('paymentDate')}>
              <input id={fid('paymentDate')} type="date" className={`${inp} ${bErr('paymentDate') ? inpErr : ''}`} value={batchInput.paymentDate}
                onChange={(e) => setBatchField('paymentDate', e.target.value)} dir="ltr"
                aria-invalid={bErr('paymentDate') ? true : undefined}
                aria-describedby={describedBy('paymentDate', bErr('paymentDate'), true)} />
            </FieldShell>
          </div>

          {artifact && (
            <p className="mt-2 text-xs text-slate-500 dark:text-slate-400">
              {existingMatches ? c.existingMatches : c.existingDiffers(artifact.batchReference, (artifact.paymentDate ?? '').slice(0, 10))}
            </p>
          )}

          <div className="mt-3 flex flex-wrap items-center gap-2">
            <button type="button" className={btn.ghost} onClick={onValidate} disabled={!!validateReason}
              aria-describedby={validateReason ? fid('validate-reason') : undefined}>
              {busy === 'validate' ? <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" /> : <ShieldCheck className="h-4 w-4" aria-hidden="true" />}
              {busy === 'validate' ? c.validating : c.validate}
            </button>
            <button type="button" className={btn.primary} onClick={onGenerate} disabled={!canGenerate}
              aria-describedby={generateReason ? fid('generate-reason') : undefined}>
              {busy === 'generate' ? <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" /> : <FileText className="h-4 w-4" aria-hidden="true" />}
              {busy === 'generate' ? c.generating : c.generate}
            </button>
            <button type="button" className={btn.ghost} onClick={onDownload} disabled={!!downloadReason}
              aria-describedby={downloadReason ? fid('download-reason') : undefined}>
              {busy === 'download' ? <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" /> : <Download className="h-4 w-4" aria-hidden="true" />}
              {busy === 'download' ? c.downloading : c.download}
            </button>
          </div>
          <ul className="mt-2 space-y-0.5 text-[11px] text-slate-500 dark:text-slate-400">
            {validateReason && <li id={fid('validate-reason')}>{c.validate}: {validateReason}</li>}
            {generateReason && <li id={fid('generate-reason')}>{c.generate}: {generateReason}</li>}
            {downloadReason && <li id={fid('download-reason')}>{c.download}: {downloadReason}</li>}
          </ul>
        </div>

        {/* Live region: notices, errors, validation result, artifact */}
        <div ref={resultRef} tabIndex={-1} aria-live="polite" className="space-y-3 outline-none">
          {notice && <Alert tone="ok">{notice}</Alert>}

          {actionError && (
            <Alert tone="error">
              <p role="alert">{actionError.message}</p>
              {actionError.issues && actionError.issues.length > 0 && <IssueList issues={actionError.issues} c={c} onFocusField={focusField} />}
            </Alert>
          )}

          {validationStale && <Alert tone="warn">{c.staleValidation}</Alert>}

          {result && (
            <div className="space-y-2">
              <Alert tone={result.canExport && result.errors.length === 0 ? 'ok' : 'error'}>
                <p className="font-medium">{result.canExport && result.errors.length === 0 ? c.validationOk : c.validationBlocked(result.errors.length)}</p>
                <p className="mt-1 text-xs">
                  {c.employees}: <span className="tabular-nums">{result.employeeCount}</span> · {c.total}: <span className="tabular-nums" dir="ltr">{fmtMoney(result.totalAmount, result.currency)}</span>
                </p>
                {result.errors.length > 0 && <IssueList issues={result.errors} c={c} onFocusField={focusField} />}
              </Alert>
              {result.warnings.length > 0 && (
                <Alert tone="warn">
                  <p className="font-medium">{c.warnings} ({result.warnings.length})</p>
                  <ul className="mt-1 list-disc space-y-0.5 ps-4 text-xs">
                    {result.warnings.map((w, i) => <li key={`${w.code}-${i}`}><span className="font-mono">{w.code}</span> — {w.message}</li>)}
                  </ul>
                </Alert>
              )}
            </div>
          )}

          {artifact && (
            <div className="rounded-xl border border-teal-200 bg-teal-50/50 p-4 dark:border-teal-500/20 dark:bg-teal-500/5">
              <p className="text-sm font-semibold text-slate-800 dark:text-white">{c.artifactTitle}</p>
              <dl className="mt-2 grid gap-x-6 gap-y-1 text-xs sm:grid-cols-2">
                <div><dt className="text-slate-400">{c.artifactId}</dt><dd className="break-all font-mono text-slate-700 dark:text-slate-300" dir="ltr">{artifact.id}</dd></div>
                <div><dt className="text-slate-400">{c.format}</dt><dd className="font-mono text-slate-700 dark:text-slate-300">{artifact.formatId}</dd></div>
                <div><dt className="text-slate-400">{c.batchReference}</dt><dd className="font-mono text-slate-700 dark:text-slate-300" dir="ltr">{artifact.batchReference}</dd></div>
                <div><dt className="text-slate-400">{c.paymentDate}</dt><dd className="text-slate-700 dark:text-slate-300" dir="ltr">{(artifact.paymentDate ?? '').slice(0, 10)}</dd></div>
                <div><dt className="text-slate-400">{c.employees}</dt><dd className="tabular-nums text-slate-700 dark:text-slate-300">{artifact.employeeCount}</dd></div>
                <div><dt className="text-slate-400">{c.total}</dt><dd className="tabular-nums text-slate-700 dark:text-slate-300" dir="ltr">{fmtMoney(artifact.totalAmount, result?.currency ?? '')}</dd></div>
              </dl>
              <p className="mt-3 text-xs font-medium text-slate-500 dark:text-slate-400">{c.files}</p>
              <ul className="mt-1 space-y-1">
                {artifact.files.map((f) => (
                  <li key={f.name} className="text-xs" dir="ltr">
                    <span className="font-medium text-slate-800 dark:text-white">{f.name}</span>
                    <span className="block break-all font-mono text-[11px] text-slate-500">{f.sha256}</span>
                  </li>
                ))}
              </ul>
              <p className="mt-3 text-xs text-slate-500 dark:text-slate-400">{c.generatedNote}</p>
              <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">{c.zipNote}</p>
            </div>
          )}
        </div>

        {/* Per-employee BIC + address lines, submitted through the existing employee approval workflow.
            Any submitted or reloaded change discards the last validation so the batch must be revalidated. */}
        {employeeIds && <SaudiBankBeneficiaryEditor key={batchId} employeeIds={employeeIds} onChange={() => setValidation(null)} />}
      </div>
    </section>
  );
}

const FOCUSABLE_FIELDS = new Set<string>([...SETTINGS_KEYS, 'batchReference', 'paymentDate']);

function IssueList({ issues, c, onFocusField }: { issues: SaudiBankExportIssue[]; c: Copy; onFocusField: (k: string) => void }) {
  // Every blocker is rendered (scrollable, never truncated): the file is all-or-nothing.
  return (
    <ul className="mt-2 max-h-72 space-y-1 overflow-y-auto pe-1 text-xs">
      {issues.map((i, idx) => (
        <li key={`${i.code}-${i.employeeId ?? ''}-${i.field ?? ''}-${idx}`} className="rounded-md bg-white/60 px-2 py-1.5 dark:bg-black/10">
          <span className="font-mono text-[11px] opacity-80">{i.code}</span>
          {i.employeeId != null && i.employeeId !== '' && <span className="ms-2 font-medium">{c.employee(String(i.employeeId))}</span>}
          {i.field && <span className="ms-2 opacity-80">{c.field}: <span className="font-mono" dir="ltr">{i.field}</span></span>}
          <span className="block">{i.message}</span>
          {i.field && FOCUSABLE_FIELDS.has(i.field) && (
            <button type="button" className={`${btn.link} mt-0.5`} onClick={() => onFocusField(i.field!)}>{c.goToField}</button>
          )}
        </li>
      ))}
    </ul>
  );
}

export default SaudiBankExportPanel;
