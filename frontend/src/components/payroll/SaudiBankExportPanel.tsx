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
import Link from 'next/link';
import {
  AlertCircle, AlertTriangle, CheckCircle2, ExternalLink, FileText, Info, Loader2, RefreshCw, Save,
} from 'lucide-react';
import {
  saudiBankExportsApi, customBankTemplatesApi, saveBlob, isAbortError, extractApiError,
  SAUDI_BANK_BATCH_TYPES, isCustomFormatId,
  type SaudiBankExportContext, type SaudiBankExportFormat, type SaudiBankExportSettings,
  type SaudiBankExportRequest, type SaudiBankExportValidation, type SaudiBankExistingExport,
  type SaudiBankExportIssue, type CustomBankTemplate,
} from '../../api/saudiBankExports';
import { useLocale } from '../../contexts/LocaleContext';
import { enumLabel } from '../../i18n/enumLabel';
import { bankAcceptanceLabel, bankFormatLabel } from '../../lib/payrollBatchDisplay';
import { DateField } from '../ui/DateField';
import { Field } from '../ui/Field';
import { isSelectableCreditDate, nextWorkingDay, todayInRiyadh } from '../../lib/creditDate';
import { useFormat } from '../../hooks/useFormat';
import type { PayrollPaymentBatch } from '../../api/payroll';
import { useAuth } from '../../contexts/AuthContext';
import { bankFileProductionAllowed, bankTemplatesAllowed } from '../../lib/payrollPaymentAccess';
import { EmployeeLabelText } from './EmployeeLabelText';
import { SaudiBankBeneficiaryEditor } from './SaudiBankBeneficiaryEditor';
import type { BankFlowProgress } from '../../lib/bankFileSteps';
import { bankFixTarget, maskedTail, type BankFixTarget } from '../../lib/bankFileGuide';
import { CustomBankTemplateEditor } from './CustomBankTemplateEditor';
import { Modal } from '../Modal';
import type { GuideSlots } from './BankFileGuide';
import { InfoTip } from '../InfoTip';
import { bankIssueText } from './bankIssueText';

// Copy for the custom-template format only (the ANB copy above is unchanged).
export const CUSTOM_COPY = {
  en: {
    suffix: 'custom template',
    debitAccount: 'Salary (debit) account IBAN',
    debitHelp: 'Your company\'s salary account as a Saudi IBAN. Written only where the template uses it.',
    debitInvalid: 'Enter a Saudi IBAN: SA followed by 22 characters, upper case, no spaces.',
    notice: 'This file is the BANK upload for your bank\'s payroll channel. It is not the Mudad file: the bank pays, then returns a bank-signed WPS file that goes to Mudad. The layout is one you configured and is not certified by any bank.',
  },
  ar: {
    suffix: 'قالب مخصص',
    debitAccount: 'آيبان حساب الرواتب (الخصم)',
    debitHelp: 'حساب الرواتب لشركتك بصيغة آيبان سعودي. يُكتب فقط حيث يستخدمه القالب.',
    debitInvalid: 'أدخل آيباناً سعودياً: SA يتبعه 22 حرفاً، بأحرف كبيرة ودون مسافات.',
    notice: 'هذا الملف هو ملف الرفع إلى البنك عبر قناة الرواتب لدى بنكك، وليس ملف مدد: البنك ينفّذ الدفع ثم يعيد ملف حماية أجور موقّعاً منه يُرسل إلى مدد. التنسيق من إعدادك ولا يعتمده أي بنك.',
  },
} as const;

// ── Styles (mirrors PayrollPage primitives; no new design language) ─────────────

export const inp = 'w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 focus:border-sapphire focus:outline-none dark:border-white/10 dark:bg-white/5 dark:text-white';
export const inpErr = 'border-rose-400 dark:border-rose-500/60';
export const sel = `${inp} appearance-none`;
export const btn = {
  primary: 'inline-flex items-center gap-1.5 rounded-lg bg-sapphire px-4 py-2 text-sm font-medium text-white hover:bg-sapphire/90 disabled:cursor-not-allowed disabled:opacity-50',
  ghost: 'inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50 dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5',
  link: 'text-xs font-medium text-sapphire underline-offset-2 hover:underline',
};

// ── Copy (EN / AR). Kept local: PayrollPage itself is not yet in the i18n dicts. ─

export type Copy = {
  title: string; subtitle: string; loading: string; loadFailed: string; retry: string;
  notSaudi: (cc: string) => string; noFormats: string; unsupportedFormat: (id: string) => string;
  company: string; runStatus: string; format: string; bank: string; channel: string; source: string;
  reviewedOn: string; acceptance: string; disclaimerTitle: string; disclaimer: string; zipNote: string;
  legacyNote: string;
  autoWpsUpload: string; autoWpsHelp: string; nationalUnifiedNo: string; nationalUnifiedNoHelp: string; unifiedDigits: string;
  molDigits: string; molPlaceholder: string;
  setupTitle: string; setupHelp: string; molEstablishmentId: string; molHelp: string;
  mainAccountNumber: string; mainAccountHelp: string; organizationName: string; organizationNameHelp: string; companyNameHelp: string;
  organizationAddress: (n: number) => string; companyName: string; narrative: string; batchType: string;
  selectBatchType: string; saveSetup: string; saving: string; saved: string; unsaved: string;
  noManagePerm: string; noExportPerm: string;
  batchTitle: string; batchReference: string; batchReferenceHelp: string; paymentDate: string; paymentDateHelp: string;
  batchReferenceSuggested: string;
  validate: string; validating: string; generate: string; generating: string; download: string; downloading: string;
  needSave: string; needValidate: string; staleValidation: string; blocked: string; busy: string;
  validationOk: string; validationBlocked: (n: number) => string; warnings: string; employees: string; total: string;
  excludedSummary: (n: number) => string; runNet: string; excludedTitle: string;
  employee: (id: string) => string; field: string; goToField: string;
  artifactTitle: string; artifactId: string; files: string; generatedNote: string; existingDiffers: (ref: string, date: string) => string;
  existingMatches: string; downloadNeedsArtifact: string;
  required: string; tooLong: (max: number, len: number) => string; tooShort: (min: number) => string;
  controlChars: string; formulaPrefix: string; edgeSpaces: string; accountDigits: string; refDigits: string;
  dateRequired: string; fixFields: string; genericError: string; conflict: string;
  // The guided Bank / WPS flow: the one-line setup row, the notice, the links, the step summaries.
  bankSetup: string; setupComplete: string; setupIncomplete: string; account: string; edit: string; change: string;
  payNobody: string; showDetails: string; hideDetails: string; bankFormats: string; editEmployeeBank: string; employeeBankTitle: string;
  fix: string; fixInPeople: string; checkPassed: string; checkPassedWith: (n: number, total: string) => string;
  createdSummary: (ref: string, date: string) => string; submittedSummary: string; creditDateRecap: string; changeInCheck: string;
  readOnlyRefHelp: string;
};

export const EN: Copy = {
  title: 'Payroll payment instruction file (bank format) — ANB Connect CSV',
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
  zipNote: 'The download is a zip for transport only. ANB Connect takes its two files separately — extract them before uploading.',
  legacyNote: 'This is your Saudi bank payroll file. The "Payroll register (internal — not a bank/WPS file)" in the batch list is for review only — never send it to a bank or Mudad.',
  autoWpsUpload: 'Ask ANB to upload the WPS file to Mudad for us (auto-WPS)',
  autoWpsHelp: 'When on, ANB builds and signs the WPS file from this instruction and uploads it to Mudad. It needs the national unified number: its field appears below once this is ticked. Check with ANB that auto-WPS is enabled on your account.',
  nationalUnifiedNo: 'National unified number',
  nationalUnifiedNoHelp: 'Exactly 10 digits, as shown in Qiwa (usually starts with 7).',
  unifiedDigits: 'Must be exactly 10 digits (0–9).',
  molDigits: 'Digits only, optionally one hyphen between the labour-office and sequence numbers (for example 7-1234567).',
  molPlaceholder: 'This is a placeholder (all zeros), not a real establishment ID.',
  setupTitle: 'Employer setup (saved per company)',
  setupHelp: 'Enter these once. They are your account and establishment facts as registered with ANB, not login credentials. Nothing is filled in automatically.',
  molEstablishmentId: 'Establishment ID (Ministry of HR)',
  molHelp: '2–15 characters: labour-office number and sequence number, as shown in Qiwa (for example 7-1234567).',
  mainAccountNumber: 'ANB main account number',
  mainAccountHelp: 'Exactly 16 digits — your ANB employer account number, not an IBAN.',
  organizationName: 'Organisation name',
  organizationNameHelp: 'Your organisation as your bank registered it on the employer account.',
  companyNameHelp: 'Your company’s name as it should appear in the bank file.',
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
  batchReferenceHelp: '1–16 digits. This is the WPS file reference: it must never repeat for this establishment, including on voided runs; also check references used outside KynexOne.',
  paymentDate: 'Credit value date',
  paymentDateHelp: 'Date the bank should credit employees.',
  batchReferenceSuggested: 'Suggested from the payment batch number. Change it if your bank or your own records use another number.',
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
  excludedSummary: (n) => `${n} employee${n === 1 ? '' : 's'} left out of this bank file`,
  runNet: 'run net pay',
  excludedTitle: 'Not in this bank file — pay and record these separately',
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
  refDigits: 'Must be 1–16 digits (0–9).',
  dateRequired: 'Choose a date.',
  fixFields: 'Fix the highlighted fields.',
  genericError: 'The request failed. Please try again.',
  conflict: 'The server refused this because it conflicts with an existing export or batch number.',
  bankSetup: 'Bank setup',
  setupComplete: 'complete',
  setupIncomplete: 'not complete',
  account: 'account',
  edit: 'Edit',
  change: 'Change',
  payNobody: 'This file pays nobody — your bank pays it.',
  showDetails: 'Show details',
  hideDetails: 'Hide details',
  bankFormats: 'Bank formats',
  editEmployeeBank: 'Edit an employee’s bank details',
  employeeBankTitle: 'Employee bank details',
  fix: 'Fix',
  fixInPeople: 'Fix in People',
  checkPassed: 'Passed',
  checkPassedWith: (n, total) => `Passed · ${n} employee${n === 1 ? '' : 's'} · ${total}`,
  createdSummary: (ref, date) => `Files created · batch number ${ref} · credit value date ${date}`,
  submittedSummary: 'Recorded as submitted to the bank.',
  creditDateRecap: 'Credit value date',
  changeInCheck: 'To change the batch number or the date, reopen step 2 with Change. The check is then run again.',
  readOnlyRefHelp: 'Suggested from the payment batch number.',
};

export const AR: Copy = {
  title: 'ملف تعليمات دفع الرواتب (بصيغة البنك) — ANB Connect CSV',
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
  zipNote: 'الملف المضغوط للنقل فقط. يستقبل ANB Connect ملفيه بشكل منفصل — استخرجهما قبل الرفع.',
  legacyNote: 'هذا هو ملف الرواتب البنكي السعودي. أما "سجل الرواتب (داخلي — ليس ملفًا بنكيًا أو ملف حماية أجور)" في قائمة الدفعات فهو للمراجعة فقط — لا ترسله إلى البنك أو منصة مدد.',
  autoWpsUpload: 'اطلب من البنك العربي الوطني رفع ملف حماية الأجور إلى مدد نيابةً عنا',
  autoWpsHelp: 'عند التفعيل يُعدّ البنك ملف حماية الأجور الموقّع من هذه التعليمات ويرفعه إلى مدد. يتطلب الرقم الوطني الموحد: يظهر حقله أدناه بعد التفعيل. تأكد من تفعيل الخدمة على حسابك لدى البنك.',
  nationalUnifiedNo: 'الرقم الوطني الموحد',
  nationalUnifiedNoHelp: '10 أرقام بالضبط كما يظهر في منصة قوى (يبدأ عادةً بالرقم 7).',
  unifiedDigits: 'يجب أن يكون 10 أرقام بالضبط (0–9).',
  molDigits: 'أرقام فقط، مع شرطة واحدة اختيارية بين رقم مكتب العمل والرقم التسلسلي (مثل 7-1234567).',
  molPlaceholder: 'هذه قيمة مؤقتة (أصفار)، وليست رقم منشأة حقيقيًا.',
  setupTitle: 'إعدادات صاحب العمل (تُحفظ لكل شركة)',
  setupHelp: 'أدخلها مرة واحدة. هي بيانات حسابك ومنشأتك المسجّلة لدى البنك، وليست بيانات دخول. لا يُملأ أي حقل تلقائيًا.',
  molEstablishmentId: 'رقم المنشأة (وزارة الموارد البشرية)',
  molHelp: 'من 2 إلى 15 حرفًا كما هو مسجّل لدى الوزارة.',
  mainAccountNumber: 'رقم الحساب الرئيسي لدى البنك العربي الوطني',
  mainAccountHelp: '16 رقمًا بالضبط — رقم حساب صاحب العمل لدى البنك، وليس رقم آيبان.',
  organizationName: 'اسم المنظمة',
  organizationNameHelp: 'منظمتك كما سجّلها بنكك على حساب صاحب العمل.',
  companyNameHelp: 'اسم شركتك كما يجب أن يظهر في ملف البنك.',
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
  batchReferenceHelp: 'من 1 إلى 16 رقمًا. هذا هو مرجع ملف حماية الأجور ويجب ألا يتكرر لهذه المنشأة أبدًا، بما في ذلك المسيرات الملغاة.',
  paymentDate: 'تاريخ قيمة الإيداع',
  paymentDateHelp: 'التاريخ الذي يودع فيه البنك الرواتب.',
  batchReferenceSuggested: 'مقترح من رقم دفعة الدفع. غيّره إذا كان بنكك أو سجلاتك تستخدم رقمًا آخر.',
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
  excludedSummary: (n) => `${n} من الموظفين خارج ملف البنك هذا`,
  runNet: 'صافي رواتب المسير',
  excludedTitle: 'غير مدرجين في ملف البنك — ادفع لهم وسجّل الدفع بشكل منفصل',
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
  refDigits: 'يجب أن يكون من 1 إلى 16 رقمًا (0–9).',
  dateRequired: 'اختر تاريخًا.',
  fixFields: 'صحّح الحقول المحددة.',
  genericError: 'فشل الطلب. حاول مرة أخرى.',
  conflict: 'رفض الخادم الطلب لتعارضه مع تصدير أو رقم دفعة موجود.',
  bankSetup: 'إعداد البنك',
  setupComplete: 'مكتمل',
  setupIncomplete: 'غير مكتمل',
  account: 'الحساب',
  edit: 'تعديل',
  change: 'تغيير',
  payNobody: 'هذا الملف لا يدفع لأحد — البنك هو من يدفع.',
  showDetails: 'عرض التفاصيل',
  hideDetails: 'إخفاء التفاصيل',
  bankFormats: 'صيغ ملفات البنك',
  editEmployeeBank: 'تعديل بيانات موظف البنكية',
  employeeBankTitle: 'بيانات الموظف البنكية',
  fix: 'معالجة',
  fixInPeople: 'المعالجة من صفحة الموظفين',
  checkPassed: 'نجح التحقق',
  checkPassedWith: (n, total) => `نجح التحقق · ${n} من الموظفين · ${total}`,
  createdSummary: (ref, date) => `أُنشئت الملفات · رقم الدفعة ${ref} · تاريخ قيمة الإيداع ${date}`,
  submittedSummary: 'سُجّلت كمُرسلة إلى البنك.',
  creditDateRecap: 'تاريخ قيمة الإيداع',
  changeInCheck: 'لتغيير رقم الدفعة أو التاريخ أعد فتح الخطوة 2 بزر «تغيير». يُعاد التحقق بعدها.',
  readOnlyRefHelp: 'مقترح من رقم دفعة الدفع.',
};

// ── Client-side checks (mirror the contract; the server remains authoritative) ─

export type SettingsKey = Exclude<keyof SaudiBankExportSettings, 'formatId' | 'autoWpsUpload'>;
type BatchKey = keyof SaudiBankExportRequest;

export const EMPTY_SETTINGS: SaudiBankExportSettings = {
  formatId: '', molEstablishmentId: '', mainAccountNumber: '', organizationName: '',
  organizationAddress1: '', organizationAddress2: '', organizationAddress3: '',
  companyName: '', narrative: '', batchType: '', autoWpsUpload: false, nationalUnifiedNo: '', debitAccountIban: '',
};
const SETTINGS_KEYS = Object.keys(EMPTY_SETTINGS) as (keyof SaudiBankExportSettings)[];
const FREE_TEXT_35: SettingsKey[] = [
  'organizationName', 'organizationAddress1', 'organizationAddress2', 'organizationAddress3', 'companyName', 'narrative',
];
export const SAUDI_CODES = new Set(['SA', 'SAU', 'KSA']);
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

export function checkSettings(s: SaudiBankExportSettings, c: Copy, debitInvalid = ''): Partial<Record<keyof SaudiBankExportSettings, string>> {
  const out: Partial<Record<keyof SaudiBankExportSettings, string>> = {};
  if (!s.formatId) out.formatId = c.required;
  if (isCustomFormatId(s.formatId)) {
    // A custom template needs only the establishment id (and, if given, a well-formed debit IBAN).
    // The ANB employer fields do not apply. The server stays authoritative (mod-97 etc.).
    const molErr = checkFreeText(s.molEstablishmentId, 2, 15, c)
      ?? (!/^[0-9]+(-[0-9]+)?$/.test(s.molEstablishmentId) ? c.molDigits
        : /^[0-]+$/.test(s.molEstablishmentId) ? c.molPlaceholder : null);
    if (molErr) out.molEstablishmentId = molErr;
    if (s.debitAccountIban && !/^SA[0-9A-Z]{22}$/.test(s.debitAccountIban)) out.debitAccountIban = debitInvalid;
    if (s.narrative) { const e = checkFreeText(s.narrative, 1, 35, c); if (e) out.narrative = e; }
    return out;
  }
  // [MOL-ESTBID] 2–15 chars: digits, optionally one hyphen; never a placeholder. Server is authoritative.
  const mol = checkFreeText(s.molEstablishmentId, 2, 15, c)
    ?? (!/^[0-9]+(-[0-9]+)?$/.test(s.molEstablishmentId) ? c.molDigits
      : /^[0-]+$/.test(s.molEstablishmentId) ? c.molPlaceholder : null);
  if (mol) out.molEstablishmentId = mol;
  if (s.autoWpsUpload && !s.nationalUnifiedNo) out.nationalUnifiedNo = c.required;
  else if (s.nationalUnifiedNo && !/^[0-9]{10}$/.test(s.nationalUnifiedNo)) out.nationalUnifiedNo = c.unifiedDigits;
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
  else if (!/^[0-9]{1,16}$/.test(b.batchReference)) out.batchReference = c.refDigits;
  if (!/^\d{4}-\d{2}-\d{2}$/.test(b.paymentDate)) out.paymentDate = c.dateRequired;
  return out;
}

export function sameSettings(a: SaudiBankExportSettings | null, b: SaudiBankExportSettings): boolean {
  if (!a) return false;
  return SETTINGS_KEYS.every((k) => (a[k] ?? '') === (b[k] ?? ''));
}

export function normaliseSettings(raw: Partial<SaudiBankExportSettings> | null | undefined): SaudiBankExportSettings {
  const out = { ...EMPTY_SETTINGS };
  for (const k of SETTINGS_KEYS) {
    if (k === 'autoWpsUpload') continue;
    const v = raw?.[k];
    out[k] = typeof v === 'string' ? v : '';
  }
  out.autoWpsUpload = raw?.autoWpsUpload === true;
  return out;
}

/** Templates with an approved version become selectable formats (id `custom:<templateId>`). */
export function customFormatsOf(list: CustomBankTemplate[]): SaudiBankExportFormat[] {
  return list.filter((t) => t.activeVersion != null).map((t) => ({
    id: t.formatId, name: t.name, bank: '', channel: '', sourceUrl: '', reviewedOn: '', acceptanceStatus: t.acceptanceStatus,
  }));
}

// ── Small presentational helpers ────────────────────────────────────────────────

export function FieldShell({ id, label, help, error, children }: {
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

export function Alert({ tone, children, icon = true }: { tone: 'error' | 'warn' | 'info' | 'ok'; children: React.ReactNode; icon?: boolean }) {
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

export function SaudiBankSettingsForm({ c, idPrefix, draft, setField, errors, formats, canManage, saving, otherBusy, dirty, onSubmit, formId, hideSubmit = false, submitLabel }: {
  c: Copy;
  /** The submit button's words (default: "Save employer setup"). */
  submitLabel?: string;
  idPrefix: string;
  /** The form's id, so a button elsewhere on the page (the guided flow's header) can submit it. */
  formId?: string;
  /** The submit button lives elsewhere (the guided flow's header); the form still says why it is busy or unsaved. */
  hideSubmit?: boolean;
  draft: SaudiBankExportSettings;
  setField: (k: keyof SaudiBankExportSettings, v: string | boolean) => void;
  /** Only the errors to show: the caller decides when a field has been "touched". */
  errors: Partial<Record<keyof SaudiBankExportSettings, string>>;
  formats: SaudiBankExportFormat[];
  canManage: boolean;
  saving: boolean;
  otherBusy: boolean;
  dirty: boolean;
  onSubmit: (e?: React.FormEvent) => void;
}) {
  const { locale, t } = useLocale();
  const cc = CUSTOM_COPY[locale === 'ar' ? 'ar' : 'en'];
  // A custom template needs only the establishment id and (optionally) a debit IBAN; the ANB employer fields do not apply.
  const draftIsCustom = isCustomFormatId(draft.formatId);
  const fid = (k: string) => `${idPrefix}-${k}`;
  const sErr = (k: keyof SaudiBankExportSettings) => errors[k] ?? null;
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
  return (
    <form id={formId} onSubmit={onSubmit} noValidate className="rounded-xl border border-slate-100 p-4 dark:border-white/10" aria-labelledby={fid('setup-title')}>
      <h4 id={fid('setup-title')} className="text-sm font-semibold text-slate-800 dark:text-white">{c.setupTitle}</h4>
      <p className="mt-0.5 text-xs text-slate-500 dark:text-slate-400">{c.setupHelp}</p>
      {!canManage && <p className="mt-2 text-xs text-amber-700 dark:text-amber-400">{c.noManagePerm}</p>}
      <div className="mt-3 grid gap-3 sm:grid-cols-2">
        <FieldShell id={fid('formatId')} label={c.format} error={sErr('formatId')}>
          <select id={fid('formatId')} className={`${sel} ${sErr('formatId') ? inpErr : ''}`} value={draft.formatId}
            onChange={(e) => setField('formatId', e.target.value)} aria-invalid={sErr('formatId') ? true : undefined}
            aria-describedby={describedBy('formatId', sErr('formatId'), false)}>
            <option value="">—</option>
            {formats.map((f) => <option key={f.id} value={f.id}>{isCustomFormatId(f.id) ? `${t(f.name)} (${cc.suffix})` : t(f.name)}</option>)}
          </select>
        </FieldShell>
        {!draftIsCustom && <FieldShell id={fid('batchType')} label={c.batchType} error={sErr('batchType')}>
          <select id={fid('batchType')} className={`${sel} ${sErr('batchType') ? inpErr : ''}`} value={draft.batchType}
            onChange={(e) => setField('batchType', e.target.value)} aria-invalid={sErr('batchType') ? true : undefined}
            aria-describedby={describedBy('batchType', sErr('batchType'), false)}>
            <option value="">{c.selectBatchType}</option>
            {SAUDI_BANK_BATCH_TYPES.map((type) => <option key={type} value={type}>{enumLabel(t, 'BankBatchType', type)}</option>)}
          </select>
        </FieldShell>}
        {textInput('molEstablishmentId', c.molEstablishmentId, { help: c.molHelp, ltr: true })}
        {draftIsCustom && textInput('debitAccountIban', cc.debitAccount, { help: cc.debitHelp, ltr: true })}
        {!draftIsCustom && textInput('mainAccountNumber', c.mainAccountNumber, { help: c.mainAccountHelp, ltr: true, numeric: true })}
        {!draftIsCustom && textInput('organizationName', c.organizationName, { help: c.organizationNameHelp })}
        {!draftIsCustom && textInput('companyName', c.companyName, { help: c.companyNameHelp })}
        {!draftIsCustom && textInput('organizationAddress1', c.organizationAddress(1))}
        {!draftIsCustom && textInput('organizationAddress2', c.organizationAddress(2))}
        {!draftIsCustom && textInput('organizationAddress3', c.organizationAddress(3))}
        {textInput('narrative', c.narrative)}
        {!draftIsCustom && <div className="sm:col-span-2">
          <label className="flex items-start gap-2 text-xs text-slate-700 dark:text-slate-300" htmlFor={fid('autoWpsUpload')}>
            <input id={fid('autoWpsUpload')} type="checkbox" className="mt-0.5" checked={draft.autoWpsUpload}
              onChange={(e) => setField('autoWpsUpload', e.target.checked)} aria-describedby={`${fid('autoWpsUpload')}-help`} />
            <span className="font-medium">{c.autoWpsUpload}</span>
          </label>
          <p id={`${fid('autoWpsUpload')}-help`} className="mt-1 text-[11px] text-slate-400">{c.autoWpsHelp}</p>
        </div>}
        {!draftIsCustom && draft.autoWpsUpload && textInput('nationalUnifiedNo', c.nationalUnifiedNo, { help: c.nationalUnifiedNoHelp, ltr: true, numeric: true })}
      </div>
      <div className="mt-3 flex flex-wrap items-center gap-3">
        {!hideSubmit && (
          <button type="submit" className={btn.primary} disabled={saving || otherBusy} aria-describedby={otherBusy ? fid('save-reason') : undefined}>
            {saving ? <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" /> : <Save className="h-4 w-4" aria-hidden="true" />}
            {saving ? c.saving : submitLabel ?? c.saveSetup}
          </button>
        )}
        {otherBusy && <span id={fid('save-reason')} className="text-xs text-slate-400">{c.busy}</span>}
        {dirty && <span className="text-xs text-amber-700 dark:text-amber-400">{c.unsaved}</span>}
      </div>
    </form>
  );
}

// ── Panel ───────────────────────────────────────────────────────────────────────

type Busy = null | 'save' | 'validate' | 'generate' | 'download';
type LoadState = { status: 'loading' } | { status: 'error'; message: string } | { status: 'ready' };
type ActionError = { message: string; issues?: SaudiBankExportIssue[] } | null;

export function SaudiBankExportPanel({ batchId, employeeIds, employeeLabels, onProgress, suggestedReference, renderGuide, batch = null, recordsCount = null, onOpenRecords }: {
  batchId: string; employeeIds?: number[]; employeeLabels?: Record<number, string>;
  /** The open payment batch (its exclusions: the employees paid outside the file). */
  batch?: PayrollPaymentBatch | null;
  /** How many payment records the batch has, when loaded. */
  recordsCount?: number | null;
  /** Opens the payment records drawer (the tab owns it). */
  onOpenRecords?: () => void;
  /** Draws the guided flow (header, four steps, footer) around what this panel supplies for each step. */
  renderGuide: (slots: GuideSlots) => React.ReactNode;
  /** Pre-fills the batch number of a batch that has no export yet (editable; the server still refuses a reused one). */
  suggestedReference?: string;
  /** Where the batch is in check -> create -> download, for the step flow above the panel. */
  onProgress?: (p: BankFlowProgress) => void;
}) {
  const { locale, t } = useLocale();
  const fx = useFormat();
  const c = locale === 'ar' ? AR : EN;
  const { hasPermission } = useAuth();
  const uid = useId();
  const fid = (k: string) => `${uid}-${k}`;
  // The credit date: today in Riyadh, and the next working day as the suggestion (lib/creditDate.ts).
  const [today] = useState(() => todayInRiyadh());

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
  // The guided flow's drawers and small disclosures. A drawer never survives a batch change.
  const [setupOpen, setSetupOpen] = useState(false);
  const [formatsOpen, setFormatsOpen] = useState(false);
  const [beneficiary, setBeneficiary] = useState<{ employeeId: number | null; locked?: boolean } | null>(null);
  // Employees whose bank-detail fix was sent for approval (not applied yet): their blocker says so until the batch changes.
  const [sentForApproval, setSentForApproval] = useState<ReadonlySet<number>>(new Set());
  const [refEditing, setRefEditing] = useState(false);
  const [pendingFocus, setPendingFocus] = useState<string | null>(null);
  const [aboutOpen, setAboutOpen] = useState(false);
  // The last "Create bank file" was refused (conflict, field errors): step 3 says so until the inputs change.
  const [createRefused, setCreateRefused] = useState(false);
  const [refError, setRefError] = useState<string | null>(null);
  const [downloaded, setDownloaded] = useState(false);
  const focusSummary = useRef(false);

  // Stale-response guards: the batch this panel currently represents, a sync busy
  // flag (blocks double clicks before React re-renders) and the in-flight action.
  const activeBatchRef = useRef(batchId);
  const busyRef = useRef(false);
  const actionAbortRef = useRef<AbortController | null>(null);
  const resultRef = useRef<HTMLDivElement>(null);
  const suggestedRef = useRef(suggestedReference ?? '');
  suggestedRef.current = suggestedReference ?? '';

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
    setBatchInput({ batchReference: suggestedRef.current, paymentDate: nextWorkingDay(todayInRiyadh()) });
    setCreateRefused(false); setRefError(null); setDownloaded(false); setAboutOpen(false);
    setValidation(null);
    setArtifact(null);
    setActionError(null);
    setNotice('');
    setShowSettingsErrors(false);
    setShowBatchErrors(false);
    setSetupOpen(false); setFormatsOpen(false); setBeneficiary(null); setRefEditing(false); setPendingFocus(null); setSentForApproval(new Set());

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
        // Approved custom templates join the format list; a failure here must never hide the built-in format.
        const custom = await customBankTemplatesApi.list(context.companyId, { signal: ac.signal }).catch(() => []);
        if (ac.signal.aborted) return;
        setFormats([...(Array.isArray(fmts) ? fmts : []), ...customFormatsOf(custom)]);
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
  const cc = CUSTOM_COPY[locale === 'ar' ? 'ar' : 'en'];
  const settingsErrors = useMemo(() => checkSettings(draft, c, cc.debitInvalid), [draft, c, cc.debitInvalid]);
  const draftIsCustom = isCustomFormatId(draft.formatId);
  const batchErrors = useMemo(() => checkBatch(batchInput, c), [batchInput, c]);
  const settingsDirty = !sameSettings(savedSettings, draft);
  const savedSettingsValid = !!savedSettings && Object.keys(checkSettings(savedSettings, c, cc.debitInvalid)).length === 0;
  const savedFormatUnsupported = !!savedSettings?.formatId && formats.length > 0 && !formats.some((f) => f.id === savedSettings.formatId);
  const inputsKey = JSON.stringify({ s: savedSettings, b: batchInput });
  const settingsKey = JSON.stringify(savedSettings);
  const validationCurrent = !!validation && validation.key === inputsKey && !settingsDirty;
  // Only the date or the reference changed after a clean check of the employees: step 3 stays open, and "Create bank file" checks again
  // with the new inputs before it creates anything. A changed bank setup sends the user back to step 2.
  const validationSettingsSame = !!validation && JSON.stringify(JSON.parse(validation.key).s) === settingsKey && !settingsDirty;
  const validationStale = !!validation && !validationCurrent && !(validationSettingsSame && employeesPassed(validation.result));
  const canGenerate = validationCurrent && validation!.result.canExport && busy === null;

  // The step flow above this panel follows the same facts the buttons below use.
  const progressValidation: BankFlowProgress['validation'] = busy === 'validate' ? 'running'
    : !validation ? 'none' : validationStale ? 'stale' : employeesPassed(validation.result) ? 'passed' : 'blocked';
  // Field errors on the date or the reference are step 3's, not step 2's.
  const inputRefused = !!validation && !validationStale && employeesPassed(validation.result) && !validation.result.canExport && validationCurrent;
  const progressCreateRefused = createRefused || inputRefused;
  const progressGenerated = !!artifact;
  // The bank setup is a gate in front of step 2: complete means saved, valid, and a supported format to make it with.
  const setupComplete = !!savedSettings && savedSettingsValid && !savedFormatUnsupported && formats.length > 0;
  const progressSetup: NonNullable<BankFlowProgress['setup']> = setupComplete ? 'complete' : 'incomplete';
  useEffect(() => {
    // Still loading: the step flow keeps saying it is checking. A failed load or a non-Saudi batch changes nothing it can act on.
    if (load.status === 'loading') { onProgress?.({ gate: 'checking', validation: 'none', generated: false }); return; }
    if (load.status !== 'ready' || !isSaudi) { onProgress?.({ gate: 'ready', validation: 'none', generated: false }); return; }
    onProgress?.({ gate: 'ready', validation: progressValidation, generated: progressGenerated, setup: progressSetup, createRefused: progressCreateRefused, downloaded, checked: validation?.result.employeeCount });
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [load.status, isSaudi, progressValidation, progressGenerated, progressSetup, progressCreateRefused, downloaded, validation?.result.employeeCount]);

  // Pre-select the only supported format when nothing is saved yet (explicit, visible choice).
  useEffect(() => {
    if (load.status !== 'ready' || formats.length !== 1) return;
    setDraft((d) => (d.formatId ? d : { ...d, formatId: formats[0].id }));
  }, [load.status, formats]);

  const setField = useCallback((k: keyof SaudiBankExportSettings, v: string | boolean) => {
    setDraft((d) => ({ ...d, [k]: v }));
    setNotice('');
  }, []);
  const setBatchField = useCallback((k: BatchKey, v: string) => {
    setBatchInput((b) => ({ ...b, [k]: v }));
    setNotice('');
    setCreateRefused(false);
    if (k === 'batchReference') setRefError(null);
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
    const wasComplete = setupComplete;
    void runAction('save', async (signal) => {
      const saved = normaliseSettings(await saudiBankExportsApi.saveSettings(ctx.companyId, payload, { signal }));
      if (activeBatchRef.current !== startedFor) return;
      setSavedSettings(saved);
      // Keep any edits made while the save was in flight; otherwise adopt the server copy.
      setDraft((d) => (sameSettings(payload, d) ? saved : d));
      setShowSettingsErrors(false);
      setSetupOpen(false);
      if (wasComplete) setNotice(c.saved);
    }, c.genericError);
  };

  // Step 2: "Check employees", with the suggested file reference and the suggested credit date (both are confirmed in step 3; a field
  // error from this check is shown there, not here).
  const onValidate = () => {
    if (settingsDirty) { setActionError({ message: c.needSave }); return; }
    const startedFor = batchId;
    const keyAtStart = inputsKey;
    const body = { ...batchInput };
    void runAction('validate', async (signal) => {
      const result = await saudiBankExportsApi.validate(batchId, body, { signal });
      if (activeBatchRef.current !== startedFor) return;
      setValidation({ key: keyAtStart, result });
      // A clean check hands over to step 3 (the guide moves focus there); blockers stay on this step, so the summary takes focus once.
      if (!employeesPassed(result)) focusSummary.current = true;
    }, c.genericError);
  };

  // Step 3: "Create bank file". The file is made only after a clean check of exactly these inputs: if the date or the reference changed
  // since the check, it checks again first and creates only when the server says the file can be made.
  const onCreate = () => {
    setShowBatchErrors(true);
    const firstBad = (['paymentDate', 'batchReference'] as BatchKey[]).find((k) => batchErrors[k]);
    if (firstBad) { if (firstBad === 'batchReference') setRefEditing(true); setPendingFocus(firstBad); return; }
    if (settingsDirty) { setActionError({ message: c.needSave }); return; }
    const startedFor = batchId;
    const keyAtStart = inputsKey;
    const body = { ...batchInput };
    const alreadyClean = canGenerate;
    void runAction('generate', async (signal) => {
      if (!alreadyClean) {
        const result = await saudiBankExportsApi.validate(batchId, body, { signal });
        if (activeBatchRef.current !== startedFor) return;
        setValidation({ key: keyAtStart, result });
        if (!result.canExport) {
          if (!employeesPassed(result)) focusSummary.current = true;
          else { setCreateRefused(true); const field = result.errors.find(e => e.field === 'paymentDate') ? 'paymentDate' : 'batchReference'; if (field === 'batchReference') setRefEditing(true); setPendingFocus(field); }
          return;
        }
      }
      try {
        const meta = await saudiBankExportsApi.generate(batchId, body, { signal });
        if (activeBatchRef.current !== startedFor) return;
        const frozen: SaudiBankExistingExport = {
          id: meta.id, formatId: meta.formatId, files: meta.files ?? [], batchReference: meta.batchReference,
          paymentDate: meta.paymentDate, employeeCount: meta.employeeCount, totalAmount: meta.totalAmount,
        };
        setArtifact(frozen);
        setCtx((prev) => (prev ? { ...prev, existingExport: frozen } : prev));
      } catch (err) {
        if (isAbortError(err)) throw err;
        setCreateRefused(true);
        const status = (err as { response?: { status?: number } })?.response?.status;
        if (status === 409) {
          // The reference is spent for this establishment: say so under the field, with the number.
          setRefError(t('File reference {ref} is already used for this establishment. Choose another number.', { ref: body.batchReference }));
          setRefEditing(true); setPendingFocus('batchReference');
          return;
        }
        throw err;
      }
    }, c.genericError);
  };


  const onDownload = () => {
    if (!artifact) return;
    const startedFor = batchId;
    void runAction('download', async (signal) => {
      const file = await saudiBankExportsApi.download(batchId, { signal });
      if (activeBatchRef.current !== startedFor) return;
      saveBlob(file);
      setDownloaded(true);
    }, c.genericError);
  };

  // ── Render ────────────────────────────────────────────────────────────────────
  // The panel keeps every guard and every action it had; it hands the guided flow what each step shows (see BankFileGuide).

  const closeSetup = () => { setSetupOpen(false); setShowSettingsErrors(false); if (savedSettings) setDraft(savedSettings); };

  // A blocked check: the error summary takes focus once, after it has been drawn (role="alert" announces it).
  useEffect(() => {
    if (!focusSummary.current || !validation || employeesPassed(validation.result)) return;
    focusSummary.current = false;
    const frame = requestAnimationFrame(() => resultRef.current?.focus());
    return () => cancelAnimationFrame(frame);
  }, [validation]);

  // Move focus once the field it names has been drawn (a drawer opening, a read-only value turning into an input).
  useEffect(() => {
    if (!pendingFocus) return;
    const timer = window.setTimeout(() => { focusField(pendingFocus); setPendingFocus(null); }, 80);
    return () => window.clearTimeout(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [pendingFocus, setupOpen, refEditing]);

  const onFix = (target: BankFixTarget) => {
    if (target.kind === 'setup') { setSetupOpen(true); setPendingFocus(target.field); }
    else if (target.kind === 'input') { if (target.field === 'batchReference') setRefEditing(true); setPendingFocus(target.field); }
    else if (target.kind === 'beneficiary') {
      const known = employeeIds?.includes(target.employeeId);
      setBeneficiary({ employeeId: known ? target.employeeId : null, locked: !!known });
    }
  };

  const inStep = (body: React.ReactNode) => <>{renderGuide({ panels: { check: { body } } })}</>;
  if (load.status === 'loading') {
    return inStep(<div className="flex items-center gap-2 text-body-sm text-fg-muted" role="status" aria-busy="true"><Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" /> {c.loading}</div>);
  }
  if (load.status === 'error') {
    return inStep(
      <div className="space-y-3">
        <Alert tone="error"><span role="alert">{load.message}</span></Alert>
        <button type="button" className="btn-secondary btn-sm" onClick={() => setReloadToken((n) => n + 1)}>
          <RefreshCw className="h-4 w-4" aria-hidden="true" /> {c.retry}
        </button>
      </div>,
    );
  }
  if (ctx && !isSaudi) return inStep(<Alert tone="info">{c.notSaudi(ctx.countryCode)}</Alert>);

  const bErr = (k: BatchKey) => (showBatchErrors ? batchErrors[k] ?? null : null);
  const validateReason = busy ? c.busy : settingsDirty ? c.needSave : !savedSettingsValid ? c.needSave : savedFormatUnsupported ? c.unsupportedFormat(savedSettings?.formatId ?? '') : null;
  const result = validation && !validationStale ? validation.result : null;
  const canManageSetup = hasPermission('payroll.structure_manage');
  const currency = result?.currency || batch?.currency || '';
  const money = (n: number) => fx.money(n, currency || null);
  const savedIsCustom = isCustomFormatId(savedSettings?.formatId ?? '');
  const accountTail = savedSettings ? maskedTail(savedIsCustom ? savedSettings.debitAccountIban : savedSettings.mainAccountNumber) : '';
  const savedFormat = formats.find((f) => f.id === savedSettings?.formatId) ?? null;
  const bankName = savedFormat && !isCustomFormatId(savedFormat.id) ? t(savedFormat.bank) : savedFormat ? t(savedFormat.name) : null;
  const formatName = savedSettings?.formatId ? bankFormatLabel(t, formats, savedSettings.formatId, cc.suffix) : null;
  const notVerified = !!savedFormat && /not-verified/.test(savedFormat.acceptanceStatus ?? '');
  const dateText = (iso: string) => (iso ? fx.gregorian(iso, 'weekdayDate') : '—');
  const employeeIssues = result ? result.errors.filter((e) => !INPUT_FIELDS.has(e.field ?? '')) : [];
  const exclusions = batch?.paymentExclusions ?? [];
  const peopleInFile = recordsCount ?? result?.employeeCount ?? null;

  const settingsForm = (
    <SaudiBankSettingsForm c={c} idPrefix={uid} draft={draft} setField={setField} submitLabel={t('Save bank setup')}
      errors={showSettingsErrors ? settingsErrors : {}} formats={formats} canManage={canManageSetup}
      saving={busy === 'save'} otherBusy={busy !== null && busy !== 'save'} dirty={settingsDirty} onSubmit={onSave} />
  );

  // ── Gate: the bank setup, once (before step 2)
  const savedErrors = checkSettings(savedSettings ?? EMPTY_SETTINGS, c, cc.debitInvalid);
  const has = (...keys: (keyof SaudiBankExportSettings)[]) => keys.every((k) => !savedErrors[k]);
  const checklist: { key: string; label: string; detail: string; saved: boolean }[] = [
    { key: 'format', label: t('Bank and file format'), detail: formatName ?? '', saved: !!savedSettings?.formatId && !savedFormatUnsupported && has('formatId') },
    { key: 'mol', label: t('Establishment ID (Ministry of HR)'), detail: c.molHelp, saved: has('molEstablishmentId') },
    { key: 'account', label: t('Employer account'), detail: savedIsCustom ? cc.debitHelp : c.mainAccountHelp, saved: savedIsCustom ? has('debitAccountIban') : has('mainAccountNumber') },
    { key: 'org', label: t('Organisation name and address'), detail: '', saved: savedIsCustom || has('organizationName', 'organizationAddress1', 'organizationAddress2', 'organizationAddress3', 'companyName', 'narrative', 'batchType') },
  ];
  const savedCount = checklist.filter((i) => i.saved).length;
  const setupBody = (
    <div className="space-y-3" data-testid="bank-setup-gate">
      <p className="text-body font-semibold text-fg" data-testid="bank-setup-summary">{t('{saved} of {total} details saved', { saved: savedCount, total: checklist.length })}</p>
      <ul className="divide-y divide-line rounded-xl border border-line">
        {checklist.map((i) => (
          <li key={i.key} className="flex items-start gap-3 px-4 py-3">
            {i.saved ? <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-success-fg" aria-hidden="true" /> : <AlertCircle className="mt-0.5 h-4 w-4 shrink-0 text-warning-fg" aria-hidden="true" />}
            <div className="min-w-0 flex-1">
              <p className="text-body font-medium text-fg">{i.label}</p>
              {i.detail && <p className="text-meta text-fg-muted">{i.detail}</p>}
            </div>
            <span className={`shrink-0 text-body-sm font-medium ${i.saved ? 'text-success-fg' : 'text-warning-fg'}`}>{i.saved ? t('Saved') : t('Missing')}</span>
          </li>
        ))}
      </ul>
      <Link href="/setup?tab=bankWps" className="inline-flex items-center gap-1 text-body-sm font-medium text-primary underline-offset-2 hover:underline">{t('Open in Setup')}<ExternalLink className="h-3.5 w-3.5" aria-hidden="true" /></Link>
      {formats.length === 0 && <Alert tone="error">{c.noFormats}</Alert>}
      {savedFormatUnsupported && <Alert tone="error">{c.unsupportedFormat(savedSettings?.formatId ?? '')}</Alert>}
    </div>
  );

  // ── Step 2: who is in the file, and what the check found
  const counters = (
    <div className="grid grid-cols-3 gap-3">
      {[
        { label: t('Employees to check'), value: peopleInFile === null ? '—' : fx.integer(peopleInFile) },
        { label: t('Paid outside the file'), value: batch?.paymentExclusions ? fx.integer(exclusions.length) : '—' },
        { label: t('Problems'), value: result ? fx.integer(employeeIssues.length) : '—', sub: result ? null : t('Not checked yet'), danger: !!result && employeeIssues.length > 0 },
      ].map((x) => (
        <div key={x.label} className="rounded-xl border border-line p-3">
          <p className={`text-h3 font-semibold tabular-nums ${x.danger ? 'text-danger-fg' : 'text-fg'}`}>{x.value}</p>
          <p className="text-meta text-fg-muted">{x.sub ? `${x.label}: ${x.sub}` : x.label}</p>
        </div>
      ))}
    </div>
  );
  const leftOut = exclusions.length > 0 && (
    <div>
      <p className="mb-2 text-body font-semibold text-fg" id={fid('left-out')}>{t('Paid outside this file ({count})', { count: exclusions.length })}</p>
      <div className="max-h-48 overflow-y-auto rounded-lg border border-line">
        <table className="w-full text-body-sm" aria-labelledby={fid('left-out')}>
          <thead className="sticky top-0 bg-surface"><tr className="border-b border-line text-meta text-fg-muted">
            <th className="px-3 py-2 text-start font-medium">{t('Employee')}</th>
            <th className="px-3 py-2 text-start font-medium">{t('Why')}</th>
            <th className="px-3 py-2 text-end font-medium">{t('Net pay')}</th>
          </tr></thead>
          <tbody className="divide-y divide-line">
            {exclusions.map((x) => (
              <tr key={x.employeeId}>
                <td className="px-3 py-2 text-fg">{employeeLabels?.[x.employeeId] ? <EmployeeLabelText label={employeeLabels[x.employeeId]} /> : <bdi className="font-mono">{x.employeeCode}</bdi>}</td>
                <td className="px-3 py-2 text-fg-muted">{x.reasonCode === 'zero_net' ? t('Net pay is zero') : x.reasonCode === 'paid_outside_bank_file' ? t('Paid in cash or by cheque') : x.reason}</td>
                <td className="px-3 py-2 text-end tabular-nums text-fg"><bdi dir="ltr">{fx.number(x.amount, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}</bdi></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
  const checkLinks = (
    <div className="flex flex-wrap items-center gap-x-5 gap-y-1 text-body-sm">
      {onOpenRecords && recordsCount !== null && <button type="button" className="font-medium text-primary underline-offset-2 hover:underline" onClick={onOpenRecords} data-testid="bank-records-open">{t('View payment records ({count})', { count: recordsCount })}</button>}
      {employeeIds && <button type="button" className="font-medium text-primary underline-offset-2 hover:underline" onClick={() => setBeneficiary({ employeeId: null })}>{t('Edit an employee’s bank details')}</button>}
    </div>
  );
  const warnings = result && result.warnings.length > 0 && (
    <details className="rounded-lg border border-line px-3 py-2 text-body-sm">
      <summary className="cursor-pointer font-medium text-fg">{t('Warnings ({count})', { count: result.warnings.length })}</summary>
      <ul className="mt-2 list-disc space-y-1 ps-5 text-fg-muted">
        {result.warnings.map((w, i) => <li key={`${w.code}-${i}`}>{bankIssueText(t, w)}</li>)}
      </ul>
    </details>
  );
  const checkAside = (
    <div className="space-y-3">
      <p className="flex items-center gap-1.5 text-body font-semibold text-fg"><Info className="h-4 w-4" aria-hidden="true" />{t('What the check looks at')}</p>
      <ul className="space-y-2">
        {['Each IBAN is a valid Saudi IBAN: SA and 22 characters.', 'Each employee has a national ID or Iqama in the right form.', 'Names and addresses fit the bank’s limits. Nothing is cut short.', 'Net pay is above zero for everyone in the file.', 'The file total matches the locked run.'].map((line) => (
          <li key={line} className="flex items-start gap-2"><CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-fg-subtle" aria-hidden="true" />{t(line)}</li>
        ))}
      </ul>
      <p className="border-t border-line pt-3 text-meta">{t('The check uses the suggested file reference and credit date. You confirm both in step 3.')}</p>
    </div>
  );
  const checkBody = <>{counters}{leftOut}{warnings}{checkLinks}</>;
  const checkView = (
    <div className="space-y-3">
      <p className="flex items-center gap-1.5 text-body font-medium text-success-fg"><CheckCircle2 className="h-4 w-4" aria-hidden="true" />{t('Checked {n} employees — no blockers.', { n: result?.employeeCount ?? artifact?.employeeCount ?? peopleInFile ?? 0 })}</p>
      {counters}{leftOut}
    </div>
  );

  // ── The error summary: every blocker, each with where it is cured; it takes focus once (role="alert").
  const summary = result && employeeIssues.length > 0 && !employeesPassed(result) ? (
    <div role="alert" className="rounded-xl border border-danger/40 bg-danger-soft p-4" data-testid="bank-error-summary">
      <p className="text-body font-semibold text-danger-fg">{t('{count, plural, one {# problem stops the file.} other {# problems stop the file.}} Fix each one, then check again.', { count: employeeIssues.length })}</p>
      {employeeIssues.some((i) => sentForApproval.has(Number(i.employeeId))) && (
        <p className="mt-2 rounded-lg bg-warning-soft px-3 py-2 text-body-sm font-medium text-warning-fg" data-testid="bank-fix-pending">{t('Fix sent for approval. Check again once it is approved.')}</p>
      )}
      <IssueList issues={employeeIssues} c={c} labels={employeeLabels} onFix={onFix} t={t} pending={sentForApproval} />
    </div>
  ) : null;

  // ── Step 3: the credit date and the file reference, and what will be frozen
  const refInfo = 'The number your bank and WPS use to tell this file apart. It must never repeat for this establishment, even on a cancelled file.';
  const refReadOnly = !refEditing && !artifact && !!suggestedReference && batchInput.batchReference === suggestedReference && !bErr('batchReference') && !refError;
  const dateError = bErr('paymentDate') ? t('Choose a working day from today on. Fridays and Saturdays aren’t available.')
    : (validationCurrent && validation?.result.errors.some((e) => e.field === 'paymentDate')) ? bankIssueText(t, validation!.result.errors.find((e) => e.field === 'paymentDate')!) : null;
  const refServerError = validationCurrent ? validation?.result.errors.find((e) => e.field === 'batchReference') : undefined;
  const createBody = (
    <div className="space-y-5">
      <Field label={t('Salary credit date (value date)')} required size="md"
        info={t('The day your bank credits salaries to employees. We suggested the next working day; the file keeps this date once created.')}
        hint={t('We suggested the next working day. Past days, Fridays and Saturdays can’t be chosen. Check public holidays with your bank.')} error={dateError}>
        <DateField id={fid('paymentDate')} value={batchInput.paymentDate} onChange={(v) => setBatchField('paymentDate', v)} today={today}
          isSelectable={(d) => isSelectableCreditDate(d, today)} testId="bank-value-date" />
      </Field>
      {refReadOnly ? (
        <div>
          <p className="mb-1.5 flex items-center gap-1.5 text-sm font-medium text-slate-700 dark:text-slate-300">{t('File reference')}<InfoTipLite text={refInfo} /></p>
          <p className="flex items-center gap-3 text-body" data-testid="bank-batch-reference-readonly">
            <bdi dir="ltr" className="font-mono text-fg">{batchInput.batchReference}</bdi>
            <button type="button" className="text-body-sm font-medium text-primary underline-offset-2 hover:underline" onClick={() => { setRefEditing(true); setPendingFocus('batchReference'); }}>{c.change}</button>
          </p>
          <p className="mt-1 text-caption text-fg-muted">{t('Must be new. Never reuse a reference, even after cancelling a file.')} {t('Suggested from the payment batch number.')}</p>
        </div>
      ) : (
        <Field label={t('File reference')} required size="md" info={refInfo}
          hint={`${t('1 to 16 digits.')} ${t('Must be new. Never reuse a reference, even after cancelling a file.')} ${batchInput.batchReference === suggestedReference ? t('Suggested from the payment batch number.') : ''}`.trim()}
          error={refError ?? bErr('batchReference') ?? (refServerError ? bankIssueText(t, refServerError) : null)}>
          <input id={fid('batchReference')} className="input w-full max-w-[320px] font-mono" value={batchInput.batchReference}
            onChange={(e) => setBatchField('batchReference', e.target.value)} inputMode="numeric" dir="ltr" autoComplete="off" />
        </Field>
      )}
    </div>
  );
  const recapRows: [string, React.ReactNode][] = [
    ...(ctx?.companyName ? [[t('Company'), <bdi key="c">{ctx.companyName}</bdi>] as [string, React.ReactNode]] : []),
    [t('Bank'), bankName ? <bdi key="b">{bankName}</bdi> : '—'],
    [t('Debit account'), accountTail ? <bdi dir="ltr" className="font-mono">{accountTail}</bdi> : '—'],
    [t('Employees'), result ? fx.integer(result.employeeCount) : peopleInFile !== null ? fx.integer(peopleInFile) : '—'],
    [t('Amount'), <bdi key="a" dir="ltr">{money(result?.totalAmount ?? batch?.totalAmount ?? 0)}</bdi>],
    [t('Credit date'), dateText(batchInput.paymentDate)],
    [t('File reference'), <bdi key="r" dir="ltr" className="font-mono">{batchInput.batchReference || '—'}</bdi>],
  ];
  const recap = (rows: [string, React.ReactNode][]) => (
    <dl className="space-y-2" data-testid="bank-create-recap">
      {rows.map(([k, v]) => <div key={k} className="grid grid-cols-[8.5rem_minmax(0,1fr)] items-baseline gap-3 text-body-sm"><dt className="text-fg-muted">{k}</dt><dd className="text-end font-medium text-fg tabular-nums">{v}</dd></div>)}
    </dl>
  );
  const createAside = (
    <div className="space-y-3">
      <p className="flex items-center gap-1.5 text-body font-semibold text-fg"><FileText className="h-4 w-4" aria-hidden="true" />{t('The file will be created with these details')}</p>
      {notVerified && <p className="inline-flex items-center gap-1 rounded-full bg-warning-soft px-2 py-0.5 text-meta font-medium text-warning-fg"><AlertTriangle className="h-3 w-3" aria-hidden="true" />{t('Your bank hasn’t confirmed this format yet')}</p>}
      {recap(recapRows)}
      <p className="flex items-start gap-1.5 border-t border-line pt-3 text-meta" data-testid="bank-pays-nobody"><Info className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden="true" />{t('Creating the file moves no money. Your bank pays once you hand it the file.')}</p>
    </div>
  );
  const createView = artifact ? recap([
    [t('Credit date'), dateText((artifact.paymentDate ?? '').slice(0, 10))],
    [t('File reference'), <bdi key="r" dir="ltr" className="font-mono">{artifact.batchReference}</bdi>],
    [t('Employees'), fx.integer(artifact.employeeCount)],
    [t('Amount'), <bdi key="a" dir="ltr">{money(artifact.totalAmount)}</bdi>],
  ]) : null;

  // ── Step 4: the frozen file
  const fileCard = artifact && (
    <div id="bank-wps-generated-export" tabIndex={-1} className="space-y-3 rounded-xl border border-line p-4 outline-none">
      <ul className="space-y-1">
        {artifact.files.map((f) => <li key={f.name} className="flex items-center gap-2 text-body font-medium text-fg"><FileText className="h-4 w-4 text-fg-muted" aria-hidden="true" /><bdi dir="ltr">{f.name}</bdi></li>)}
      </ul>
      {recap([
        ...(ctx?.companyName ? [[t('Company'), <bdi key="c">{ctx.companyName}</bdi>] as [string, React.ReactNode]] : []),
        [t('Employees'), fx.integer(artifact.employeeCount)],
        [t('Amount'), <bdi key="a" dir="ltr">{money(artifact.totalAmount)}</bdi>],
        [t('Credit date'), dateText((artifact.paymentDate ?? '').slice(0, 10))],
        [t('File reference'), <bdi key="r" dir="ltr" className="font-mono">{artifact.batchReference}</bdi>],
      ])}
      <details className="text-meta">
        <summary className="cursor-pointer font-medium text-primary">{t('File fingerprint (SHA-256)')}</summary>
        <ul className="mt-2 space-y-1">{artifact.files.map((f) => <li key={f.name} dir="ltr" className="break-all font-mono text-fg-muted">{f.name}: {f.sha256}</li>)}</ul>
      </details>
      {downloaded && <button type="button" className="text-body-sm font-medium text-primary underline-offset-2 hover:underline" onClick={onDownload}>{t('Download again')}</button>}
    </div>
  );
  const sendAside = (
    <div className="space-y-3">
      <p className="text-body font-medium text-fg">{bankName ? t('Send this file only to {bank}. It is not the Mudad file.', { bank: bankName }) : t('Send this file only to your bank. It is not the Mudad file.')}</p>
      <p className="text-meta" data-testid="bank-zip-note">{c.zipNote}</p>
      {/* The English file names on a line of their own, left to right, never inside an Arabic sentence. */}
      {artifact && artifact.files.length > 0 && (
        <p dir="ltr" className="font-mono text-meta text-fg rtl:text-end" data-testid="bank-file-names">{artifact.files.map((f) => f.name).join(' · ')}</p>
      )}
      <p className="text-meta" data-testid="bank-pays-nobody">{t('Creating the file moves no money. Your bank pays once you hand it the file.')}</p>
    </div>
  );

  // ── What just happened (refusals); the error summary of a blocked check
  const live = (
    <>
      {notice && <Alert tone="ok"><span role="status">{notice}</span></Alert>}
      {actionError && (
        <Alert tone="error">
          <p role="alert">{actionError.message}</p>
          {actionError.issues && actionError.issues.length > 0 && <IssueList issues={actionError.issues} c={c} labels={employeeLabels} onFix={onFix} t={t} />}
        </Alert>
      )}
      {validationStale && <Alert tone="warn">{t('Something changed since the last check. Check again.')}</Alert>}
      {summary}
      {!bankFileProductionAllowed(hasPermission) && <Alert tone="info">{c.noExportPerm}</Alert>}
    </>
  );

  // ── Drawers and dialogs: the setup form, the bank formats (custom templates), one employee's bank details, about this file
  const overlays = (
    <>
      <Modal isOpen={setupOpen} title={t('Bank setup')} size="lg" placement="end" onClose={closeSetup} closeDisabled={busy === 'save'} closeLabel={t('Close')}>
        <div className="space-y-3">
          <Link href="/setup?tab=bankWps" className="inline-flex items-center gap-1 text-body-sm font-medium text-primary underline-offset-2 hover:underline">{t('Open in Setup')}<ExternalLink className="h-3.5 w-3.5" aria-hidden="true" /></Link>
          {settingsForm}
        </div>
      </Modal>
      {/* Custom bank templates: layout configured by the customer, approved by a second person. The template routes stay with
          payroll.export, so the editor is not offered to someone who only prepares the file. */}
      {ctx && bankTemplatesAllowed(hasPermission) && formatsOpen && (
        <Modal isOpen={formatsOpen} title={c.bankFormats} size="xl" placement="end" onClose={() => setFormatsOpen(false)} closeLabel={t('Close')}>
          <CustomBankTemplateEditor
            companyId={ctx.companyId}
            batchId={batchId}
            batchReference={batchInput.batchReference}
            paymentDate={batchInput.paymentDate}
            canManage={canManageSetup}
            canDecide={hasPermission('payroll.approve')}
            onChanged={() => {
              void customBankTemplatesApi.list(ctx.companyId).then((list) => {
                setFormats((prev) => [...prev.filter((f) => !isCustomFormatId(f.id)), ...customFormatsOf(list)]);
              }).catch(() => undefined);
            }}
          />
        </Modal>
      )}
      {/* Per-employee BIC + address lines, submitted through the existing employee approval workflow.
          Any submitted or reloaded change discards the last validation so the batch must be revalidated. */}
      {employeeIds && beneficiary !== null && (
        <Modal isOpen={beneficiary !== null} title={c.employeeBankTitle} size="lg" placement="end" onClose={() => setBeneficiary(null)} closeLabel={t('Close')}>
          <SaudiBankBeneficiaryEditor key={`${batchId}-${beneficiary?.employeeId ?? 'any'}`} employeeIds={employeeIds} employeeLabels={employeeLabels}
            initialEmployeeId={beneficiary?.employeeId ?? undefined} lockEmployee={!!beneficiary?.locked}
            onChange={() => setValidation(null)}
            onSentForApproval={(id) => setSentForApproval((prev) => new Set(prev).add(id))} />
        </Modal>
      )}
      {aboutOpen && (
        <Modal isOpen={aboutOpen} title={t('About this file')} size="md" onClose={() => setAboutOpen(false)} closeLabel={t('Close')}>
          <div className="space-y-3 text-body-sm text-fg-muted" data-testid="bank-notice-details">
            <p className="font-medium text-fg">{t('Creating the file moves no money. Your bank pays once you hand it the file.')}</p>
            <p>{c.disclaimer}</p>
            <p>{c.zipNote}</p>
            <p>{c.legacyNote}</p>
            <p>{t('Saudi bank payroll file: the Payroll payment instruction file (bank format) — the ANB Connect instruction with WPS fields — is the file to send to the bank. Mudad accepts only the WPS file your bank signs — keep the bank’s output file or a Mudad screenshot as evidence before marking the batch Accepted.')}</p>
            {draftIsCustom && <p>{cc.notice}</p>}
            {savedFormat && (
              <dl className="grid gap-x-6 gap-y-2 sm:grid-cols-2">
                <div><dt>{t('Format')}</dt><dd className="font-medium text-fg">{formatName}</dd></div>
                {!isCustomFormatId(savedFormat.id) && <div><dt>{c.bank}</dt><dd className="font-medium text-fg">{t(savedFormat.bank)}</dd></div>}
                {!isCustomFormatId(savedFormat.id) && <div className="sm:col-span-2"><dt>{t('Channel')}</dt><dd className="font-medium text-fg">{t(savedFormat.channel)}</dd></div>}
                <div><dt>{c.acceptance}</dt><dd className="font-medium text-fg">{bankAcceptanceLabel(t, savedFormat.acceptanceStatus)}</dd></div>
                {!isCustomFormatId(savedFormat.id) && <div><dt>{t('Specification reviewed')}</dt><dd className="font-medium text-fg">{savedFormat.reviewedOn}</dd></div>}
                {!isCustomFormatId(savedFormat.id) && savedFormat.sourceUrl && (
                  <div className="min-w-0 sm:col-span-2"><dt>{t('Specification source')}</dt>
                    <dd className="break-all"><a href={savedFormat.sourceUrl} target="_blank" rel="noopener noreferrer" className="inline-flex items-center gap-1 text-primary underline-offset-2 hover:underline" dir="ltr">{savedFormat.sourceUrl}<ExternalLink className="h-3 w-3 shrink-0" aria-hidden="true" /></a></dd>
                  </div>
                )}
              </dl>
            )}
          </div>
        </Modal>
      )}
    </>
  );

  return <>{renderGuide({
    panels: {
      setup: {
        purpose: ctx?.companyName
          ? t('The bank file needs your employer account and establishment details. You enter them once for {company} and every later run uses them.', { company: ctx.companyName })
          : t('The bank file needs your employer account and establishment details. You enter them once and every later run uses them.'),
        body: setupBody,
      },
      check: {
        purpose: t('Check that every employee in the batch can be paid: bank account, ID and amount. Nothing is created or sent yet.'),
        body: checkBody, aside: checkAside, view: checkView,
      },
      create: {
        purpose: t('Choose the salary credit date, then create the file. Once created, the file is frozen.'),
        body: createBody, aside: createAside, view: createView,
      },
      send: {
        purpose: t('Download the file and hand it to your bank, then record it as submitted.'),
        body: fileCard, aside: sendAside, view: fileCard,
      },
    },
    actions: {
      complete_setup: { label: t('Complete bank setup'), onClick: () => setSetupOpen(true) },
      check: { label: busy === 'validate' ? t('Checking…') : t('Check employees'), onClick: onValidate, disabled: !!validateReason, busy: busy === 'validate', reason: validateReason },
      check_again: { label: busy === 'validate' ? t('Checking…') : t('Check again'), onClick: onValidate, disabled: !!validateReason, busy: busy === 'validate', reason: validateReason },
      create_file: { label: busy === 'generate' ? t('Creating…') : t('Create bank file'), onClick: onCreate, disabled: busy !== null && busy !== 'generate', busy: busy === 'generate', reason: busy && busy !== 'generate' ? c.busy : null },
      download: { label: busy === 'download' ? t('Downloading…') : t('Download bank file'), onClick: onDownload, disabled: busy !== null && busy !== 'download', busy: busy === 'download' },
    },
    live,
    liveRef: resultRef,
    overlays,
    data: {
      bankName, formatName, accountTail, company: ctx?.companyName ?? null, ready: true, autoWps: savedSettings ? savedSettings.autoWpsUpload : null,
      file: artifact ? { reference: artifact.batchReference, paymentDate: (artifact.paymentDate ?? '').slice(0, 10), employeeCount: artifact.employeeCount, totalAmount: artifact.totalAmount, files: artifact.files } : null,
    },
    menu: {
      setup: () => setSetupOpen(true),
      employeeBank: employeeIds ? () => setBeneficiary({ employeeId: null }) : undefined,
      about: () => setAboutOpen(true),
      formats: ctx && bankTemplatesAllowed(hasPermission) ? () => setFormatsOpen(true) : undefined,
      download: artifact ? onDownload : undefined,
    },
  })}</>;
}

/** The `i` beside a label that is not a form control (the read-only file reference). */
function InfoTipLite({ text }: { text: string }) {
  return <InfoTip text={text} />;
}

/** The date and the reference are step 3's fields; every other blocker is about the employees (step 2). */
const INPUT_FIELDS = new Set(['batchReference', 'paymentDate']);
/** The employees can be paid: the check passed, or only the file's own inputs were refused. */
function employeesPassed(r: SaudiBankExportValidation): boolean {
  return r.canExport || (r.errors.length > 0 && r.errors.every((e) => INPUT_FIELDS.has(e.field ?? '')));
}

function IssueList({ issues, c, labels, onFix, t, pending }: { issues: SaudiBankExportIssue[]; c: Copy; labels?: Record<number, string>; onFix: (target: BankFixTarget) => void; t: (key: string, params?: Record<string, string | number>) => string; pending?: ReadonlySet<number> }) {
  // Every blocker is listed (scrollable, never cut short): the file is all-or-nothing. Each says, in plain words, what is wrong and where it is cured.
  return (
    <ul className="mt-3 max-h-72 space-y-2 overflow-y-auto pe-1 text-body-sm" data-testid="bank-blockers">
      {issues.map((i, idx) => {
        const target = bankFixTarget(i);
        const name = i.employeeId != null && i.employeeId !== '' ? labels?.[Number(i.employeeId)] || c.employee(String(i.employeeId)) : '';
        const problem = bankIssueText(t, i);
        const sent = i.employeeId != null && i.employeeId !== '' && !!pending?.has(Number(i.employeeId));
        return (
          <li key={`${i.code}-${i.employeeId ?? ''}-${i.field ?? ''}-${idx}`} className="flex flex-wrap items-start justify-between gap-x-3 gap-y-1 rounded-lg bg-surface px-3 py-2" data-code={i.code}>
            <div className="min-w-0 flex-1">
              {name && <p className="font-medium text-fg"><EmployeeLabelText label={name} /></p>}
              <p className="text-fg">{problem}</p>
              {sent && <p className="mt-1 text-meta font-medium text-warning-fg">{t('Fix sent for approval. Check again once it is approved.')}</p>}
            </div>
            {target.kind === 'people' ? (
              <Link href={`/people/${target.employeeId}`} className="btn-secondary btn-sm shrink-0" data-testid="bank-blocker-fix">{c.fixInPeople}</Link>
            ) : target.kind !== 'none' && (
              <button type="button" className="btn-secondary btn-sm shrink-0" data-testid="bank-blocker-fix" onClick={() => onFix(target)}
                aria-label={name ? t('Fix {name}: {problem}', { name: name.replace(/\s*\u00b7\s*/, ' '), problem }) : undefined}>{c.fix}</button>
            )}
          </li>
        );
      })}
    </ul>
  );
}

export default SaudiBankExportPanel;
