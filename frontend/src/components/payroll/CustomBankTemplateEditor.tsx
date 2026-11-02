'use client';

// Custom bank salary-file templates (maker-checker). A template is a customer-configured LAYOUT read off the
// bank's sample file: which payroll fields, in which order, written how. It never relaxes a Saudi wage-file
// rule - IBAN, ID, amounts, totals and record count are validated on every row whatever the template says.
//
// The file this produces is the BANK upload (the salary/payroll file the employer's bank channel takes). It is
// NOT the Mudad file: the bank pays, then returns a bank-signed WPS file that goes to Mudad.
// Bank acceptance of a custom layout is NEVER claimed; the customer proves it with a test upload.
//
// Nothing here logs, caches or persists bank data. The preview arrives with IBANs and IDs already masked.

import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { ArrowDown, ArrowUp, CheckCircle2, Eye, Loader2, Plus, Save, Trash2, XCircle } from 'lucide-react';
import {
  customBankTemplatesApi, extractApiError, isAbortError,
  type CustomBankCatalog, type CustomBankColumn, type CustomBankPreview, type CustomBankTemplate,
  type CustomBankTemplateDefinition, type CustomBankFieldEntry, type SaudiBankExportIssue,
} from '../../api/saudiBankExports';
import { useLocale } from '../../contexts/LocaleContext';
import { Badge, type BadgeTone } from '../ui/Badge';
import { Button } from '../ui/Button';
import { Input, Select } from '../ui/Field';
import { useConfirm } from '../ui/useConfirm';
import { blankColumn, blankDefinition, moveItem, removeItem } from '../../lib/customBankTemplate';

type Copy = {
  title: string; intro: string; notMudad: string; notCertified: string;
  loading: string; loadFailed: string; retry: string; none: string;
  newFromStarter: string; newBlank: string; starterNotice: string; templates: string;
  state: Record<CustomBankTemplate['state'], string>;
  versionLabel: (v: number) => string; activeUse: (v: number) => string;
  pendingBanner: (v: number) => string; youProposed: string; needsSecondPerson: string;
  approve: string; reject: string; rejectReason: string; decisionDone: string;
  approveTitle: string; approveBody: string; rejectTitle: string; rejectBody: string;
  editorTitle: string; readOnly: string; name: string; sampleReference: string; sampleHelp: string;
  layout: string; layoutDelimited: string; layoutFixed: string; encoding: string; lineEnding: string;
  crlf: string; lf: string; extension: string; delimiter: string; tab: string; quoteRule: string;
  quote: Record<string, string>; headerRow: string; endBreak: string; note: string;
  headerRecord: string; detailColumns: string; trailerRecord: string; addColumn: string; chooseField: string;
  remove: string; moveUp: string; moveDown: string; literal: string; colTitle: string; width: string; widthHelp: string;
  align: string; left: string; right: string; pad: string; decimals: string; decimalSep: string; thousandsSep: string;
  none_: string; dot: string; comma: string; space: string; minor: string; dateFormat: string; upper: string; maxLen: string; allowBlank: string;
  fields: Record<string, string>;
  encodingNames: Record<string, string>;
  save: string; saving: string; saveTitle: string; saveBody: string; saved: string; unsaved: string;
  preview: string; previewing: string; previewTitle: string; previewMasked: string; previewNeedBatch: string; previewOk: string;
  previewBlocked: (n: number) => string; warnings: string; rows: string; total: string;
  genericError: string; fixIssues: string; cancelNote: string;
  showSample: string; sampleTitle: string;
};

const EN: Copy = {
  title: 'Custom bank salary-file templates',
  intro: 'Use this when your bank\'s salary-file layout is not built in. Copy the layout from your bank\'s sample file: choose the fields, their order and how each is written. A second person must approve every new or changed version before a file can be generated from it.',
  notMudad: 'The generated file is the BANK upload (your bank\'s payroll channel). It is not the Mudad file: the bank pays, then returns a bank-signed WPS file that goes to Mudad.',
  notCertified: 'A template is a layout you configured. It is not certified by any bank. Saudi wage-file rules (IBAN, ID, amounts, totals, record count) are checked on every row regardless of the template, and nothing is ever cut, rounded or replaced. Prove the layout with your bank by uploading a test file.',
  loading: 'Loading templates…', loadFailed: 'Could not load the templates.', retry: 'Retry', none: 'No templates yet.',
  newFromStarter: 'New from starter', newBlank: 'New blank template', starterNotice: 'The starter is a generic layout, not any bank\'s format. Compare it with your bank\'s sample before you approve it.',
  templates: 'Templates',
  state: { Active: 'Approved, in use', ActivePendingChange: 'Approved, change awaiting approval', PendingApproval: 'Awaiting approval', Rejected: 'Rejected' },
  versionLabel: (v) => `Version ${v}`, activeUse: (v) => `Files are generated from approved version ${v}.`,
  pendingBanner: (v) => `Version ${v} is waiting for a second person to approve it.`,
  youProposed: 'You proposed this version, so someone else must approve or reject it.',
  needsSecondPerson: 'Another person with payroll approve access must decide it.',
  approve: 'Approve', reject: 'Reject', rejectReason: 'Reason for rejecting', decisionDone: 'Decision recorded.',
  approveTitle: 'Approve this template version?', approveBody: 'Approve exactly the layout shown. Files can then be generated from it. Approving does not mean your bank accepts it.',
  rejectTitle: 'Reject this template version?', rejectBody: 'The proposer will need to change it and propose again.',
  editorTitle: 'Layout', readOnly: 'You can view templates. Changing one needs the payroll structure permission.',
  name: 'Template name', sampleReference: 'Bank sample it was copied from', sampleHelp: 'For example the file name or reference your bank gave you.',
  layout: 'Layout', layoutDelimited: 'Delimited (CSV)', layoutFixed: 'Fixed width', encoding: 'Encoding', lineEnding: 'Line ending',
  crlf: 'Windows (CRLF)', lf: 'Unix (LF)', extension: 'File extension', delimiter: 'Delimiter', tab: 'Tab', quoteRule: 'Quoting',
  quote: { none: 'Never (refuse values that need it)', minimal: 'Only when needed', always: 'Always' },
  headerRow: 'First row holds column titles', endBreak: 'End the last line with a line break', note: 'Note for the approver',
  headerRecord: 'Header record (optional, before the lines)', detailColumns: 'Employee line columns', trailerRecord: 'Trailer record (optional, after the lines)',
  addColumn: 'Add column', chooseField: 'Choose a field…', remove: 'Remove', moveUp: 'Move up', moveDown: 'Move down',
  literal: 'Fixed text', colTitle: 'Title', width: 'Width (bytes)', widthHelp: 'Counted in bytes of the chosen encoding.',
  align: 'Align', left: 'Left', right: 'Right', pad: 'Pad with', decimals: 'Decimals', decimalSep: 'Decimal separator', thousandsSep: 'Thousands separator',
  none_: 'None', dot: 'Dot', comma: 'Comma', space: 'Space', minor: 'None (minor units)', dateFormat: 'Date format', upper: 'Upper case', maxLen: 'Max length (fails, never cuts)', allowBlank: 'Allow blank',
  fields: {
    employeeNumber: 'Employee number', nameEn: 'Name (English)', nameAr: 'Name (Arabic)', nationalId: 'National ID / Iqama', beneficiaryIban: 'Beneficiary IBAN',
    beneficiaryBankCode: 'Beneficiary bank code (from IBAN)', beneficiaryBic: 'Beneficiary bank BIC', netPay: 'Net pay', basicSalary: 'Basic salary',
    housingAllowance: 'Housing allowance', otherEarnings: 'Other earnings', deductions: 'Deductions', currency: 'Currency', paymentReference: 'Payment reference (company narrative)',
    literal: 'Fixed text', fileDate: 'File date', valueDate: 'Value (payment) date', establishmentId: 'Establishment (MOL) ID', debitAccountIban: 'Debit account IBAN',
    totalAmount: 'Total amount', recordCount: 'Record count', batchReference: 'Batch reference',
  },
  encodingNames: { 'utf-8': 'UTF-8', 'utf-8-bom': 'UTF-8 with BOM', 'windows-1256': 'Windows-1256 (Arabic)' },
  save: 'Propose for approval', saving: 'Saving…', saveTitle: 'Propose this layout?', saveBody: 'It becomes a pending version. Another person must approve it before it is used. The current approved version stays in use until then.',
  saved: 'Proposed. Waiting for a second person to approve it.', unsaved: 'Unsaved changes',
  preview: 'Preview with this batch', previewing: 'Building preview…', previewTitle: 'Preview', previewMasked: 'First rows in this layout. IBANs and ID numbers are masked here; the real file contains them.',
  previewNeedBatch: 'Enter the batch reference and payment date above to preview.', previewOk: 'The batch passes every check with this layout.',
  previewBlocked: (n) => `${n} issue(s) would block the file.`, warnings: 'Warnings', rows: 'Employees', total: 'Total',
  genericError: 'The request failed. Try again.', fixIssues: 'Fix these issues:', cancelNote: 'Optional',
  showSample: 'Show the rendered sample of this version', sampleTitle: 'Sample of this version, as it will be written (IBANs and IDs masked)',
};

const AR: Copy = {
  title: 'قوالب مخصصة لملف رواتب البنك',
  intro: 'استخدم هذا عندما لا يكون تنسيق ملف الرواتب لدى بنكك مدمجاً. انسخ التنسيق من ملف العيّنة الذي زوّدك به البنك: اختر الحقول وترتيبها وطريقة كتابة كل منها. يجب أن يعتمد شخص ثانٍ كل نسخة جديدة أو معدّلة قبل إنشاء أي ملف منها.',
  notMudad: 'الملف المُنشأ هو ملف الرفع إلى البنك (قناة الرواتب في بنكك). وهو ليس ملف مدد: البنك ينفّذ الدفع ثم يعيد ملف حماية أجور موقّعاً من البنك يُرسل إلى مدد.',
  notCertified: 'القالب هو تنسيق أعددته بنفسك وليس معتمداً من أي بنك. تُفحص قواعد ملف الأجور السعودي (الآيبان والهوية والمبالغ والمجاميع وعدد السجلات) على كل صف مهما كان القالب، ولا يُقتطع شيء ولا يُقرَّب ولا يُستبدل. تأكّد من التنسيق مع بنكك برفع ملف تجريبي.',
  loading: 'جارٍ تحميل القوالب…', loadFailed: 'تعذّر تحميل القوالب.', retry: 'إعادة المحاولة', none: 'لا توجد قوالب بعد.',
  newFromStarter: 'جديد من قالب مبدئي', newBlank: 'قالب فارغ جديد', starterNotice: 'القالب المبدئي تنسيق عام وليس تنسيق أي بنك. قارنه بعيّنة بنكك قبل اعتماده.',
  templates: 'القوالب',
  state: { Active: 'معتمد ومستخدم', ActivePendingChange: 'معتمد، وتعديل بانتظار الاعتماد', PendingApproval: 'بانتظار الاعتماد', Rejected: 'مرفوض' },
  versionLabel: (v) => `النسخة ${v}`, activeUse: (v) => `تُنشأ الملفات من النسخة المعتمدة ${v}.`,
  pendingBanner: (v) => `النسخة ${v} بانتظار اعتماد شخص ثانٍ.`,
  youProposed: 'أنت من اقترح هذه النسخة، لذا يجب أن يعتمدها أو يرفضها شخص آخر.',
  needsSecondPerson: 'يجب أن يقرّرها شخص آخر لديه صلاحية اعتماد الرواتب.',
  approve: 'اعتماد', reject: 'رفض', rejectReason: 'سبب الرفض', decisionDone: 'تم تسجيل القرار.',
  approveTitle: 'اعتماد نسخة القالب هذه؟', approveBody: 'اعتمد التنسيق المعروض تحديداً. بعدها يمكن إنشاء الملفات منه. الاعتماد لا يعني أن بنكك يقبله.',
  rejectTitle: 'رفض نسخة القالب هذه؟', rejectBody: 'سيحتاج مقدّم الاقتراح إلى تعديلها واقتراحها من جديد.',
  editorTitle: 'التنسيق', readOnly: 'يمكنك عرض القوالب. تعديلها يتطلب صلاحية هيكلة الرواتب.',
  name: 'اسم القالب', sampleReference: 'عيّنة البنك المنسوخ منها', sampleHelp: 'مثل اسم الملف أو المرجع الذي زوّدك به البنك.',
  layout: 'التنسيق', layoutDelimited: 'بفاصل (CSV)', layoutFixed: 'عرض ثابت', encoding: 'الترميز', lineEnding: 'نهاية السطر',
  crlf: 'ويندوز (CRLF)', lf: 'يونكس (LF)', extension: 'امتداد الملف', delimiter: 'الفاصل', tab: 'مسافة جدولة', quoteRule: 'علامات الاقتباس',
  quote: { none: 'بدون (رفض القيم التي تحتاجها)', minimal: 'عند الحاجة فقط', always: 'دائماً' },
  headerRow: 'الصف الأول يحوي عناوين الأعمدة', endBreak: 'إنهاء السطر الأخير بفاصل سطر', note: 'ملاحظة للمعتمِد',
  headerRecord: 'سجل الترويسة (اختياري، قبل الأسطر)', detailColumns: 'أعمدة سطر الموظف', trailerRecord: 'سجل الختام (اختياري، بعد الأسطر)',
  addColumn: 'إضافة عمود', chooseField: 'اختر حقلاً…', remove: 'إزالة', moveUp: 'نقل للأعلى', moveDown: 'نقل للأسفل',
  literal: 'نص ثابت', colTitle: 'العنوان', width: 'العرض (بايت)', widthHelp: 'يُحسب بالبايت حسب الترميز المختار.',
  align: 'المحاذاة', left: 'يسار', right: 'يمين', pad: 'الحشو بـ', decimals: 'المنازل العشرية', decimalSep: 'فاصل الكسور', thousandsSep: 'فاصل الآلاف',
  none_: 'بدون', dot: 'نقطة', comma: 'فاصلة', space: 'مسافة', minor: 'بدون (وحدات صغرى)', dateFormat: 'صيغة التاريخ', upper: 'أحرف كبيرة', maxLen: 'أقصى طول (يفشل الملف ولا يقتطع)', allowBlank: 'السماح بقيمة فارغة',
  fields: {
    employeeNumber: 'رقم الموظف', nameEn: 'الاسم (إنجليزي)', nameAr: 'الاسم (عربي)', nationalId: 'الهوية الوطنية / الإقامة', beneficiaryIban: 'آيبان المستفيد',
    beneficiaryBankCode: 'رمز بنك المستفيد (من الآيبان)', beneficiaryBic: 'رمز BIC لبنك المستفيد', netPay: 'صافي الراتب', basicSalary: 'الراتب الأساسي',
    housingAllowance: 'بدل السكن', otherEarnings: 'مستحقات أخرى', deductions: 'الاستقطاعات', currency: 'العملة', paymentReference: 'مرجع الدفع (بيان الشركة)',
    literal: 'نص ثابت', fileDate: 'تاريخ الملف', valueDate: 'تاريخ القيمة (الدفع)', establishmentId: 'رقم المنشأة (وزارة العمل)', debitAccountIban: 'آيبان حساب الخصم',
    totalAmount: 'المبلغ الإجمالي', recordCount: 'عدد السجلات', batchReference: 'مرجع الدفعة',
  },
  encodingNames: { 'utf-8': 'UTF-8', 'utf-8-bom': 'UTF-8 مع BOM', 'windows-1256': 'Windows-1256 (عربي)' },
  save: 'اقتراح للاعتماد', saving: 'جارٍ الحفظ…', saveTitle: 'اقتراح هذا التنسيق؟', saveBody: 'يصبح نسخة معلّقة. يجب أن يعتمدها شخص آخر قبل استخدامها. تبقى النسخة المعتمدة الحالية قيد الاستخدام حتى ذلك الحين.',
  saved: 'تم الاقتراح. بانتظار اعتماد شخص ثانٍ.', unsaved: 'تغييرات غير محفوظة',
  preview: 'معاينة بهذه الدفعة', previewing: 'جارٍ إنشاء المعاينة…', previewTitle: 'المعاينة', previewMasked: 'أول الصفوف بهذا التنسيق. تُخفى الآيبانات وأرقام الهوية هنا، بينما يحتويها الملف الفعلي.',
  previewNeedBatch: 'أدخل مرجع الدفعة وتاريخ الدفع أعلاه للمعاينة.', previewOk: 'تجتاز الدفعة كل الفحوص بهذا التنسيق.',
  previewBlocked: (n) => `${n} مشكلة ستمنع إنشاء الملف.`, warnings: 'تنبيهات', rows: 'الموظفون', total: 'الإجمالي',
  genericError: 'فشل الطلب. حاول مرة أخرى.', fixIssues: 'عالج هذه المشكلات:', cancelNote: 'اختياري',
  showSample: 'عرض عيّنة مُنتَجة لهذه النسخة', sampleTitle: 'عيّنة من هذه النسخة كما ستُكتب (الآيبان والهوية مخفيان)',
};

const STATE_TONE: Record<CustomBankTemplate['state'], BadgeTone> = {
  Active: 'success', ActivePendingChange: 'warning', PendingApproval: 'warning', Rejected: 'danger',
};

type Props = {
  companyId: string; batchId: string;
  batchReference: string; paymentDate: string;
  canManage: boolean; canDecide: boolean;
  /** Called after a template changes state so the parent can refresh its format list. */
  onChanged?: () => void;
};

export function CustomBankTemplateEditor({ companyId, batchId, batchReference, paymentDate, canManage, canDecide, onChanged }: Props) {
  const { locale } = useLocale();
  const c = locale === 'ar' ? AR : EN;
  const { confirm, dialog } = useConfirm();

  const [catalog, setCatalog] = useState<CustomBankCatalog | null>(null);
  const [templates, setTemplates] = useState<CustomBankTemplate[]>([]);
  const [status, setStatus] = useState<'loading' | 'ready' | 'error'>('loading');
  const [reload, setReload] = useState(0);
  const [selectedId, setSelectedId] = useState<string | null>(null);   // null + draft = a new template
  const [draft, setDraft] = useState<CustomBankTemplateDefinition | null>(null);
  const [baseJson, setBaseJson] = useState('');
  const [starterId, setStarterId] = useState<string | undefined>(undefined);
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState<null | 'save' | 'preview' | 'decide'>(null);
  const [issues, setIssues] = useState<SaudiBankExportIssue[]>([]);
  const [message, setMessage] = useState<{ tone: 'ok' | 'error'; text: string } | null>(null);
  const [preview, setPreview] = useState<CustomBankPreview | null>(null);
  const [rejectNote, setRejectNote] = useState('');
  const [sample, setSample] = useState<CustomBankPreview | null>(null);

  useEffect(() => {
    const ac = new AbortController();
    setStatus('loading');
    Promise.all([customBankTemplatesApi.catalog({ signal: ac.signal }), customBankTemplatesApi.list(companyId, { signal: ac.signal })])
      .then(([cat, list]) => { setCatalog(cat); setTemplates(list); setStatus('ready'); })
      .catch((e) => { if (!isAbortError(e)) setStatus('error'); });
    return () => ac.abort();
  }, [companyId, reload]);

  const selected = useMemo(() => templates.find((t) => t.id === selectedId) ?? null, [templates, selectedId]);
  const dirty = !!draft && JSON.stringify(draft) !== baseJson;
  const fieldMap = useMemo(() => new Map((catalog?.fields ?? []).map((f) => [f.key, f] as const)), [catalog]);

  const open = useCallback((t: CustomBankTemplate | null, def?: CustomBankTemplateDefinition, starter?: string) => {
    const shown = def ?? (t ? (t.versions.find((v) => v.status === 'Pending') ?? t.versions.find((v) => v.version === t.activeVersion) ?? t.versions[0]).definition : blankDefinition());
    const copy = JSON.parse(JSON.stringify(shown)) as CustomBankTemplateDefinition;
    setSelectedId(t?.id ?? null); setDraft(copy); setBaseJson(t ? JSON.stringify(copy) : ''); setStarterId(starter);
    setIssues([]); setMessage(null); setPreview(null); setSample(null); setNote(''); setRejectNote('');
  }, []);

  const fail = async (err: unknown) => {
    const e = await extractApiError(err, c.genericError);
    setIssues(e.issues ?? []);
    setMessage({ tone: 'error', text: e.message });
  };

  const refresh = async (focusId?: string) => {
    const list = await customBankTemplatesApi.list(companyId);
    setTemplates(list);
    if (focusId) {
      const t = list.find((x) => x.id === focusId) ?? null;
      if (t) open(t);
    }
    onChanged?.();
  };

  const onSave = async () => {
    if (!draft || busy) return;
    const ok = await confirm({ title: c.saveTitle, message: c.saveBody, confirmLabel: c.save, tone: 'primary' });
    if (!ok) return;
    setBusy('save'); setIssues([]); setMessage(null);
    try {
      const body = { definition: draft, note: note.trim() || undefined };
      const t = selectedId
        ? await customBankTemplatesApi.update(companyId, selectedId, body)
        : await customBankTemplatesApi.create(companyId, starterId && !dirty ? { starterId, note: body.note } : body);
      await refresh(t.id);
      setMessage({ tone: 'ok', text: c.saved });
    } catch (e) { await fail(e); } finally { setBusy(null); }
  };

  const onPreview = async () => {
    if (!draft || busy) return;
    if (!batchReference || !paymentDate) { setMessage({ tone: 'error', text: c.previewNeedBatch }); return; }
    setBusy('preview'); setMessage(null);
    try { setPreview(await customBankTemplatesApi.preview(batchId, { batchReference, paymentDate, definition: draft })); }
    catch (e) { setPreview(null); await fail(e); } finally { setBusy(null); }
  };

  // The checker reads real output, not just the definition: the pending version rendered with this batch, masked.
  const onSample = async () => {
    const pv = selected?.versions.find((v) => v.status === 'Pending');
    if (!selected || !pv || busy) return;
    if (!batchReference || !paymentDate) { setMessage({ tone: 'error', text: c.previewNeedBatch }); return; }
    setBusy('preview'); setMessage(null);
    try { setSample(await customBankTemplatesApi.preview(batchId, { batchReference, paymentDate, templateId: selected.id, version: pv.version })); }
    catch (e) { setSample(null); await fail(e); } finally { setBusy(null); }
  };

  const onDecide = async (approve: boolean) => {
    const pending = selected?.versions.find((v) => v.status === 'Pending');
    if (!selected || !pending || busy) return;
    if (!approve && rejectNote.trim().length < 3) { setMessage({ tone: 'error', text: c.rejectReason }); return; }
    const ok = await confirm({
      title: approve ? c.approveTitle : c.rejectTitle, message: approve ? c.approveBody : c.rejectBody,
      confirmLabel: approve ? c.approve : c.reject, tone: approve ? 'primary' : 'danger',
    });
    if (!ok) return;
    setBusy('decide'); setMessage(null);
    try {
      const body = { version: pending.version, definitionHash: pending.definitionHash };
      if (approve) await customBankTemplatesApi.approve(companyId, selected.id, body);
      else await customBankTemplatesApi.reject(companyId, selected.id, { ...body, note: rejectNote.trim() });
      await refresh(selected.id);
      setMessage({ tone: 'ok', text: c.decisionDone });
    } catch (e) { await fail(e); } finally { setBusy(null); }
  };

  const set = <K extends keyof CustomBankTemplateDefinition>(k: K, v: CustomBankTemplateDefinition[K]) =>
    setDraft((d) => (d ? { ...d, [k]: v } : d));

  if (status === 'loading') return <p className="text-xs text-slate-500" role="status">{c.loading}</p>;
  if (status === 'error') {
    return (
      <div className="flex items-center gap-3 text-xs text-rose-700 dark:text-rose-400" role="alert">
        <span>{c.loadFailed}</span>
        <Button size="sm" onClick={() => setReload((n) => n + 1)}>{c.retry}</Button>
      </div>
    );
  }

  const pendingVersion = selected?.versions.find((v) => v.status === 'Pending') ?? null;
  const readOnly = !canManage;

  return (
    <section className="rounded-xl border border-slate-100 p-4 dark:border-white/10" aria-labelledby="cbt-title">
      {dialog}
      <h4 id="cbt-title" className="text-sm font-semibold text-slate-800 dark:text-white">{c.title}</h4>
      <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">{c.intro}</p>
      <p className="mt-2 rounded-lg bg-sky-50 p-2 text-xs text-sky-900 dark:bg-sky-500/10 dark:text-sky-200">{c.notMudad}</p>
      <p className="mt-2 rounded-lg bg-amber-50 p-2 text-xs text-amber-900 dark:bg-amber-500/10 dark:text-amber-200">{c.notCertified}</p>

      {/* Template list */}
      <div className="mt-3">
        <p className="text-xs font-medium text-slate-600 dark:text-slate-300">{c.templates}</p>
        {templates.length === 0 && <p className="mt-1 text-xs text-slate-400">{c.none}</p>}
        <ul className="mt-1 flex flex-wrap gap-2">
          {templates.map((t) => (
            <li key={t.id}>
              <button type="button" onClick={() => open(t)} aria-pressed={t.id === selectedId}
                className={`flex items-center gap-2 rounded-lg border px-3 py-1.5 text-start text-xs ${t.id === selectedId ? 'border-sapphire bg-sapphire/5' : 'border-slate-200 dark:border-white/10'}`}>
                <span className="font-medium">{t.name}</span>
                <Badge tone={STATE_TONE[t.state]}>{c.state[t.state]}</Badge>
              </button>
            </li>
          ))}
        </ul>
        {canManage && (
          <div className="mt-2 flex flex-wrap items-center gap-2">
            {(catalog?.starters ?? []).map((s) => (
              <Button key={s.id} size="sm" onClick={() => open(null, s.definition, s.id)}>
                <Plus className="h-3.5 w-3.5" aria-hidden="true" />{c.newFromStarter}: {s.name}
              </Button>
            ))}
            <Button size="sm" onClick={() => open(null)}><Plus className="h-3.5 w-3.5" aria-hidden="true" />{c.newBlank}</Button>
          </div>
        )}
        {canManage && <p className="mt-1 text-xs text-slate-400">{c.starterNotice}</p>}
      </div>

      {/* Selected template: version state and the checker's decision */}
      {selected && (
        <div className="mt-3 space-y-2 text-xs">
          {selected.activeVersion != null && <p>{c.activeUse(selected.activeVersion)}</p>}
          {pendingVersion && (
            <div className="rounded-lg border border-amber-200 p-3 dark:border-amber-500/30" role="group" aria-label={c.pendingBanner(pendingVersion.version)}>
              <p className="font-medium">{c.pendingBanner(pendingVersion.version)}</p>
              {pendingVersion.proposeNote && <p className="mt-1 text-slate-500">{pendingVersion.proposeNote}</p>}
              <div className="mt-2">
                <Button size="sm" busy={busy === 'preview'} onClick={onSample}><Eye className="h-3.5 w-3.5" aria-hidden="true" />{c.showSample}</Button>
              </div>
              {sample && (
                <div className="mt-2">
                  <p className="text-slate-500">{c.sampleTitle}</p>
                  <pre dir="ltr" className="mt-1 overflow-x-auto rounded bg-slate-50 p-2 font-mono text-xs leading-5 dark:bg-white/5">{sample.lines.join('\n')}</pre>
                  {sample.errors.length > 0 && <ul className="ms-4 mt-1 list-disc text-rose-700 dark:text-rose-400">{sample.errors.slice(0, 10).map((i, n) => <li key={`${i.code}-${n}`}>{i.message}</li>)}</ul>}
                </div>
              )}
              {pendingVersion.proposedByCurrentUser
                ? <p className="mt-1 text-slate-500">{c.youProposed}</p>
                : canDecide
                  ? (
                    <div className="mt-2 flex flex-wrap items-end gap-2">
                      <Button variant="primary" size="sm" busy={busy === 'decide'} onClick={() => onDecide(true)}>
                        <CheckCircle2 className="h-3.5 w-3.5" aria-hidden="true" />{c.approve}
                      </Button>
                      <label className="flex min-w-48 flex-1 flex-col gap-1">
                        <span>{c.rejectReason}</span>
                        <Input size="sm" value={rejectNote} maxLength={500} onChange={(e) => setRejectNote(e.target.value)} />
                      </label>
                      <Button variant="danger" size="sm" busy={busy === 'decide'} onClick={() => onDecide(false)}>
                        <XCircle className="h-3.5 w-3.5" aria-hidden="true" />{c.reject}
                      </Button>
                    </div>
                  )
                  : <p className="mt-1 text-slate-500">{c.needsSecondPerson}</p>}
            </div>
          )}
        </div>
      )}

      {/* Editor */}
      {draft && catalog && (
        <div className="mt-4 space-y-3">
          <h5 className="text-xs font-semibold uppercase tracking-wide text-slate-500">{c.editorTitle}{selected ? ` · ${c.versionLabel(Math.max(0, ...selected.versions.map((v) => v.version)))}` : ''}</h5>
          {readOnly && <p className="text-xs text-amber-700 dark:text-amber-400">{c.readOnly}</p>}
          <fieldset disabled={readOnly || busy !== null} className="grid gap-3 sm:grid-cols-2">
            <Labeled label={c.name}><Input value={draft.name} maxLength={80} onChange={(e) => set('name', e.target.value)} /></Labeled>
            <Labeled label={c.sampleReference} help={c.sampleHelp}><Input value={draft.sampleReference} maxLength={200} onChange={(e) => set('sampleReference', e.target.value)} /></Labeled>
            <Labeled label={c.layout}>
              <Select value={draft.layout} onChange={(e) => set('layout', e.target.value as CustomBankTemplateDefinition['layout'])}>
                <option value="delimited">{c.layoutDelimited}</option>
                <option value="fixed-width">{c.layoutFixed}</option>
              </Select>
            </Labeled>
            <Labeled label={c.encoding}>
              <Select value={draft.encoding} onChange={(e) => set('encoding', e.target.value as CustomBankTemplateDefinition['encoding'])}>
                {catalog.encodings.map((x) => <option key={x} value={x}>{c.encodingNames[x] ?? x}</option>)}
              </Select>
            </Labeled>
            <Labeled label={c.lineEnding}>
              <Select value={draft.lineEnding} onChange={(e) => set('lineEnding', e.target.value as 'crlf' | 'lf')}>
                <option value="crlf">{c.crlf}</option><option value="lf">{c.lf}</option>
              </Select>
            </Labeled>
            <Labeled label={c.extension}>
              <Select value={draft.fileExtension} onChange={(e) => set('fileExtension', e.target.value)}>
                {catalog.extensions.map((x) => <option key={x} value={x}>.{x}</option>)}
              </Select>
            </Labeled>
            {draft.layout === 'delimited' && (
              <>
                <Labeled label={c.delimiter}>
                  <Select dir="ltr" value={draft.delimiter} onChange={(e) => set('delimiter', e.target.value)}>
                    {catalog.delimiters.map((x) => <option key={x} value={x}>{x === '\t' ? c.tab : x}</option>)}
                  </Select>
                </Labeled>
                <Labeled label={c.quoteRule}>
                  <Select value={draft.quoteRule} onChange={(e) => set('quoteRule', e.target.value as 'none' | 'minimal' | 'always')}>
                    {catalog.quoteRules.map((x) => <option key={x} value={x}>{c.quote[x] ?? x}</option>)}
                  </Select>
                </Labeled>
                <label className="flex items-center gap-2 text-xs"><input type="checkbox" checked={draft.headerRow} onChange={(e) => set('headerRow', e.target.checked)} />{c.headerRow}</label>
              </>
            )}
            <label className="flex items-center gap-2 text-xs"><input type="checkbox" checked={draft.endWithLineBreak} onChange={(e) => set('endWithLineBreak', e.target.checked)} />{c.endBreak}</label>
          </fieldset>

          <ColumnList title={c.headerRecord} cols={draft.headerRecord} scope="envelope" catalog={catalog} fieldMap={fieldMap} c={c} fixed={draft.layout === 'fixed-width'}
            disabled={readOnly || busy !== null} onChange={(l) => set('headerRecord', l)} />
          <ColumnList title={c.detailColumns} cols={draft.columns} scope="detail" catalog={catalog} fieldMap={fieldMap} c={c} fixed={draft.layout === 'fixed-width'}
            disabled={readOnly || busy !== null} onChange={(l) => set('columns', l)} />
          <ColumnList title={c.trailerRecord} cols={draft.trailerRecord} scope="envelope" catalog={catalog} fieldMap={fieldMap} c={c} fixed={draft.layout === 'fixed-width'}
            disabled={readOnly || busy !== null} onChange={(l) => set('trailerRecord', l)} />

          {canManage && (
            <label className="flex flex-col gap-1 text-xs">
              <span>{c.note} ({c.cancelNote})</span>
              <Input value={note} maxLength={500} onChange={(e) => setNote(e.target.value)} />
            </label>
          )}

          <div className="flex flex-wrap items-center gap-3">
            <Button size="md" busy={busy === 'preview'} onClick={onPreview}>
              {busy === 'preview' ? <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" /> : <Eye className="h-4 w-4" aria-hidden="true" />}
              {busy === 'preview' ? c.previewing : c.preview}
            </Button>
            {canManage && (
              <Button variant="primary" busy={busy === 'save'} disabled={!dirty && !!selectedId} onClick={onSave}>
                {busy === 'save' ? <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" /> : <Save className="h-4 w-4" aria-hidden="true" />}
                {busy === 'save' ? c.saving : c.save}
              </Button>
            )}
            {dirty && <span className="text-xs text-amber-700 dark:text-amber-400">{c.unsaved}</span>}
          </div>
        </div>
      )}

      {message && (
        <p role={message.tone === 'error' ? 'alert' : 'status'}
          className={`mt-3 text-xs ${message.tone === 'error' ? 'text-rose-700 dark:text-rose-400' : 'text-emerald-700 dark:text-emerald-400'}`}>{message.text}</p>
      )}
      {issues.length > 0 && (
        <div className="mt-2 text-xs text-rose-700 dark:text-rose-400" role="alert">
          <p className="font-medium">{c.fixIssues}</p>
          <ul className="ms-4 list-disc">{issues.map((i, n) => <li key={`${i.code}-${n}`}>{i.message}</li>)}</ul>
        </div>
      )}

      {preview && (
        <div className="mt-4 rounded-lg border border-slate-200 p-3 dark:border-white/10">
          <h5 className="text-xs font-semibold text-slate-700 dark:text-slate-200">{c.previewTitle}</h5>
          <p className="mt-1 text-xs text-slate-500">{c.previewMasked}</p>
          <pre dir="ltr" className="mt-2 overflow-x-auto rounded bg-slate-50 p-2 font-mono text-xs leading-5 text-slate-800 dark:bg-white/5 dark:text-slate-100">
            {preview.lines.join('\n')}
          </pre>
          <p className="mt-2 text-xs text-slate-600 dark:text-slate-300">
            {c.rows}: {preview.recordCount} · {c.total}: {preview.currency} {preview.totalAmount.toFixed(2)} · {c.encodingNames[preview.encoding] ?? preview.encoding}
          </p>
          <p className={`mt-1 text-xs ${preview.canExport ? 'text-emerald-700 dark:text-emerald-400' : 'text-rose-700 dark:text-rose-400'}`} role="status">
            {preview.canExport ? c.previewOk : c.previewBlocked(preview.errors.length)}
          </p>
          {preview.errors.length > 0 && <ul className="ms-4 mt-1 list-disc text-xs text-rose-700 dark:text-rose-400">{preview.errors.slice(0, 20).map((i, n) => <li key={`${i.code}-${n}`}>{i.message}</li>)}</ul>}
          {preview.warnings.length > 0 && (
            <div className="mt-2 text-xs text-amber-700 dark:text-amber-400">
              <p className="font-medium">{c.warnings}</p>
              <ul className="ms-4 list-disc">{preview.warnings.map((w, n) => <li key={`${w.code}-${n}`}>{w.message}</li>)}</ul>
            </div>
          )}
        </div>
      )}
    </section>
  );
}

function Labeled({ label, help, children }: { label: string; help?: string; children: React.ReactNode }) {
  return (
    <label className="flex flex-col gap-1 text-xs text-slate-600 dark:text-slate-300">
      <span className="font-medium">{label}</span>
      {children}
      {help && <span className="text-xs text-slate-400">{help}</span>}
    </label>
  );
}

function ColumnList({ title, cols, scope, catalog, fieldMap, c, fixed, disabled, onChange }: {
  title: string; cols: CustomBankColumn[]; scope: 'detail' | 'envelope'; catalog: CustomBankCatalog;
  fieldMap: Map<string, CustomBankFieldEntry>; c: Copy; fixed: boolean; disabled: boolean;
  onChange: (next: CustomBankColumn[]) => void;
}) {
  const options = catalog.fields.filter((f) => (scope === 'detail' ? f.detail : f.envelope));
  const patch = (i: number, p: Partial<CustomBankColumn>) => onChange(cols.map((col, n) => (n === i ? { ...col, ...p } : col)));
  return (
    <div>
      <p className="text-xs font-semibold text-slate-700 dark:text-slate-200">{title}</p>
      <ol className="mt-1 space-y-2">
        {cols.map((col, i) => {
          const kind = fieldMap.get(col.field)?.kind ?? 'text';
          return (
            <li key={`${col.field}-${i}`} className="rounded-lg border border-slate-100 p-2 dark:border-white/10">
              <div className="flex flex-wrap items-center gap-2">
                <span className="w-6 text-center text-xs text-slate-400">{i + 1}</span>
                <span className="min-w-40 flex-1 text-xs font-medium">{c.fields[col.field] ?? col.field}</span>
                <Button size="sm" variant="ghost" disabled={disabled || i === 0} aria-label={c.moveUp} onClick={() => onChange(moveItem(cols, i, -1))}><ArrowUp className="h-3.5 w-3.5" aria-hidden="true" /></Button>
                <Button size="sm" variant="ghost" disabled={disabled || i === cols.length - 1} aria-label={c.moveDown} onClick={() => onChange(moveItem(cols, i, 1))}><ArrowDown className="h-3.5 w-3.5" aria-hidden="true" /></Button>
                <Button size="sm" variant="ghost" disabled={disabled} aria-label={c.remove} onClick={() => onChange(removeItem(cols, i))}><Trash2 className="h-3.5 w-3.5" aria-hidden="true" /></Button>
              </div>
              <div className="mt-2 grid gap-2 sm:grid-cols-3">
                {col.field === 'literal' && <Labeled label={c.literal}><Input size="sm" value={col.literal ?? ''} maxLength={60} disabled={disabled} onChange={(e) => patch(i, { literal: e.target.value })} /></Labeled>}
                <Labeled label={c.colTitle}><Input size="sm" value={col.title} maxLength={60} disabled={disabled} onChange={(e) => patch(i, { title: e.target.value })} /></Labeled>
                {fixed && <Labeled label={c.width} help={c.widthHelp}><Input size="sm" dir="ltr" type="number" min={1} max={500} value={col.width ?? ''} disabled={disabled} onChange={(e) => patch(i, { width: e.target.value ? Number(e.target.value) : null })} /></Labeled>}
                {fixed && (
                  <>
                    <Labeled label={c.align}>
                      <Select size="sm" value={col.align} disabled={disabled} onChange={(e) => patch(i, { align: e.target.value as 'left' | 'right' })}>
                        <option value="left">{c.left}</option><option value="right">{c.right}</option>
                      </Select>
                    </Labeled>
                    <Labeled label={c.pad}><Input size="sm" dir="ltr" value={col.pad} maxLength={1} disabled={disabled} onChange={(e) => patch(i, { pad: e.target.value || ' ' })} /></Labeled>
                  </>
                )}
                {kind === 'amount' && (
                  <>
                    <Labeled label={c.decimals}><Input size="sm" dir="ltr" type="number" min={0} max={4} value={col.amountDecimals} disabled={disabled} onChange={(e) => patch(i, { amountDecimals: Number(e.target.value) })} /></Labeled>
                    <Labeled label={c.decimalSep}>
                      <Select size="sm" value={col.decimalSeparator} disabled={disabled} onChange={(e) => patch(i, { decimalSeparator: e.target.value })}>
                        {catalog.decimalSeparators.map((x) => <option key={x} value={x}>{x === '.' ? c.dot : x === ',' ? c.comma : c.minor}</option>)}
                      </Select>
                    </Labeled>
                    <Labeled label={c.thousandsSep}>
                      <Select size="sm" value={col.thousandsSeparator} disabled={disabled} onChange={(e) => patch(i, { thousandsSeparator: e.target.value })}>
                        {catalog.thousandsSeparators.map((x) => <option key={x} value={x}>{x === '' ? c.none_ : x === '.' ? c.dot : x === ',' ? c.comma : c.space}</option>)}
                      </Select>
                    </Labeled>
                  </>
                )}
                {kind === 'date' && (
                  <Labeled label={c.dateFormat}>
                    <Select size="sm" dir="ltr" value={col.dateFormat} disabled={disabled} onChange={(e) => patch(i, { dateFormat: e.target.value })}>
                      {catalog.dateFormats.map((x) => <option key={x} value={x}>{x}</option>)}
                    </Select>
                  </Labeled>
                )}
                {kind === 'text' && col.field !== 'literal' && (
                  <>
                    <Labeled label={c.maxLen}><Input size="sm" dir="ltr" type="number" min={1} max={500} value={col.maxLength ?? ''} disabled={disabled} onChange={(e) => patch(i, { maxLength: e.target.value ? Number(e.target.value) : null })} /></Labeled>
                    <label className="flex items-center gap-2 text-xs"><input type="checkbox" checked={col.upperCase} disabled={disabled} onChange={(e) => patch(i, { upperCase: e.target.checked })} />{c.upper}</label>
                    {col.field !== 'beneficiaryIban' && col.field !== 'nationalId' && (
                      <label className="flex items-center gap-2 text-xs"><input type="checkbox" checked={col.allowBlank} disabled={disabled} onChange={(e) => patch(i, { allowBlank: e.target.checked })} />{c.allowBlank}</label>
                    )}
                  </>
                )}
              </div>
            </li>
          );
        })}
      </ol>
      <div className="mt-2">
        <Select size="sm" aria-label={c.addColumn} value="" disabled={disabled}
          onChange={(e) => { if (e.target.value) onChange([...cols, blankColumn(e.target.value)]); }}>
          <option value="">{c.addColumn} — {c.chooseField}</option>
          {options.map((f) => <option key={f.key} value={f.key}>{c.fields[f.key] ?? f.key}</option>)}
        </Select>
      </div>
    </div>
  );
}
