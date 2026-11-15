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
  AlertTriangle, CheckCircle2, ExternalLink, Loader2, RefreshCw, Save,
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
import { DatePicker } from '../ui/DateParts';
import { useAuth } from '../../contexts/AuthContext';
import { bankFileProductionAllowed, bankTemplatesAllowed } from '../../lib/payrollPaymentAccess';
import { EmployeeLabelText } from './EmployeeLabelText';
import { SaudiBankBeneficiaryEditor } from './SaudiBankBeneficiaryEditor';
import type { BankFlowProgress } from '../../lib/bankFileSteps';
import { bankFixTarget, maskedTail, type BankFixTarget } from '../../lib/bankFileGuide';
import { CustomBankTemplateEditor } from './CustomBankTemplateEditor';
import { Modal } from '../Modal';
import type { GuideSlots } from './BankFileGuide';

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
  mainAccountNumber: string; mainAccountHelp: string; organizationName: string;
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
  zipNote: 'The download is a zip for transport only. ANB Connect takes header.csv and body.csv as two separate files — extract them before uploading.',
  legacyNote: 'This is your Saudi bank payroll file. The "Payroll register (internal — not a bank/WPS file)" in the batch list is for review only — never send it to a bank or Mudad.',
  autoWpsUpload: 'Ask ANB to upload the WPS file to Mudad for us (auto-WPS)',
  autoWpsHelp: 'When on, ANB builds and signs the WPS file from this instruction and uploads it to Mudad. Requires the national unified number. Check with ANB that auto-WPS is enabled on your account.',
  nationalUnifiedNo: 'National unified number',
  nationalUnifiedNoHelp: 'Exactly 10 digits, as shown in Qiwa (usually starts with 7).',
  unifiedDigits: 'Must be exactly 10 digits (0–9).',
  molDigits: 'Digits only, optionally one hyphen between the labour-office and sequence numbers (for example 7-1234567).',
  molPlaceholder: 'This is a placeholder (all zeros), not a real establishment ID.',
  setupTitle: 'Employer setup (saved per company)',
  setupHelp: 'Enter these once. They are your account and establishment facts as registered with ANB, not login credentials. Nothing is filled in automatically.',
  molEstablishmentId: 'MOL establishment ID',
  molHelp: '2–15 characters: labour-office number and sequence number, as shown in Qiwa (for example 7-1234567).',
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
  zipNote: 'الملف المضغوط للنقل فقط. يستقبل ANB Connect الملفين header.csv و body.csv بشكل منفصل — استخرجهما قبل الرفع.',
  legacyNote: 'هذا هو ملف الرواتب البنكي السعودي. أما "سجل الرواتب (داخلي — ليس ملفًا بنكيًا أو ملف حماية أجور)" في قائمة الدفعات فهو للمراجعة فقط — لا ترسله إلى البنك أو منصة مدد.',
  autoWpsUpload: 'اطلب من البنك العربي الوطني رفع ملف حماية الأجور إلى مدد نيابةً عنا',
  autoWpsHelp: 'عند التفعيل يُعدّ البنك ملف حماية الأجور الموقّع من هذه التعليمات ويرفعه إلى مدد. يتطلب الرقم الوطني الموحد. تأكد من تفعيل الخدمة على حسابك لدى البنك.',
  nationalUnifiedNo: 'الرقم الوطني الموحد',
  nationalUnifiedNoHelp: '10 أرقام بالضبط كما يظهر في منصة قوى (يبدأ عادةً بالرقم 7).',
  unifiedDigits: 'يجب أن يكون 10 أرقام بالضبط (0–9).',
  molDigits: 'أرقام فقط، مع شرطة واحدة اختيارية بين رقم مكتب العمل والرقم التسلسلي (مثل 7-1234567).',
  molPlaceholder: 'هذه قيمة مؤقتة (أصفار)، وليست رقم منشأة حقيقيًا.',
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

function fmtMoney(n: number, currency: string) {
  // Up to 6 fraction digits so an out-of-spec value is visible rather than rounded away.
  return `${currency ? `${currency} ` : ''}${n.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 6 })}`;
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

export function SaudiBankSettingsForm({ c, idPrefix, draft, setField, errors, formats, canManage, saving, otherBusy, dirty, onSubmit, formId, hideSubmit = false }: {
  c: Copy;
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
        {!draftIsCustom && textInput('organizationName', c.organizationName)}
        {!draftIsCustom && textInput('companyName', c.companyName)}
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
            {saving ? c.saving : c.saveSetup}
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

export function SaudiBankExportPanel({ batchId, employeeIds, employeeLabels, onProgress, suggestedReference, renderGuide }: {
  batchId: string; employeeIds?: number[]; employeeLabels?: Record<number, string>;
  /** Draws the guided flow (header, four steps, footer) around what this panel supplies for each step. */
  renderGuide: (slots: GuideSlots) => React.ReactNode;
  /** Pre-fills the batch number of a batch that has no export yet (editable; the server still refuses a reused one). */
  suggestedReference?: string;
  /** Where the batch is in check -> create -> download, for the step flow above the panel. */
  onProgress?: (p: BankFlowProgress) => void;
}) {
  const { locale, t } = useLocale();
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
  // The guided flow's drawers and small disclosures. A drawer never survives a batch change.
  const [setupOpen, setSetupOpen] = useState(false);
  const [formatsOpen, setFormatsOpen] = useState(false);
  const [beneficiary, setBeneficiary] = useState<{ employeeId: number | null } | null>(null);
  const [refEditing, setRefEditing] = useState(false);
  const [detailsOpen, setDetailsOpen] = useState(false);
  const [pendingFocus, setPendingFocus] = useState<string | null>(null);

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
    setBatchInput({ batchReference: suggestedRef.current, paymentDate: '' });
    setValidation(null);
    setArtifact(null);
    setActionError(null);
    setNotice('');
    setShowSettingsErrors(false);
    setShowBatchErrors(false);
    setSetupOpen(false); setFormatsOpen(false); setBeneficiary(null); setRefEditing(false); setDetailsOpen(false); setPendingFocus(null);

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
  const selectedFormat = formats.find((f) => f.id === draft.formatId) ?? null;
  const savedFormatUnsupported = !!savedSettings?.formatId && formats.length > 0 && !formats.some((f) => f.id === savedSettings.formatId);
  const inputsKey = JSON.stringify({ s: savedSettings, b: batchInput });
  const validationCurrent = !!validation && validation.key === inputsKey && !settingsDirty;
  const validationStale = !!validation && !validationCurrent;
  const canGenerate = validationCurrent && validation!.result.canExport && busy === null;
  const existingMatches = !!artifact
    && artifact.batchReference === batchInput.batchReference
    && (artifact.paymentDate ?? '').slice(0, 10) === batchInput.paymentDate;

  // The step flow above this panel follows the same facts the buttons below use.
  const progressValidation: BankFlowProgress['validation'] = busy === 'validate' ? 'running'
    : !validation ? 'none' : validationStale ? 'stale' : validation.result.canExport ? 'passed' : 'blocked';
  const progressGenerated = !!artifact;
  // The bank setup is a gate in front of step 2: complete means saved, valid, and a supported format to make it with.
  const setupComplete = !!savedSettings && savedSettingsValid && !savedFormatUnsupported && formats.length > 0;
  const progressSetup: NonNullable<BankFlowProgress['setup']> = setupComplete ? 'complete' : 'incomplete';
  useEffect(() => {
    // Still loading: the step flow keeps saying it is checking. A failed load or a non-Saudi batch changes nothing it can act on.
    if (load.status === 'loading') { onProgress?.({ gate: 'checking', validation: 'none', generated: false }); return; }
    if (load.status !== 'ready' || !isSaudi) { onProgress?.({ gate: 'ready', validation: 'none', generated: false }); return; }
    onProgress?.({ gate: 'ready', validation: progressValidation, generated: progressGenerated, setup: progressSetup });
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [load.status, isSaudi, progressValidation, progressGenerated, progressSetup]);

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
      setSetupOpen(false);
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
      // A clean check hands over to step 3 (the guide moves focus there); blockers stay on this step, so the result takes focus.
      if (!result.canExport || result.errors.length > 0) requestAnimationFrame(() => resultRef.current?.focus());
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
  // The panel keeps every guard and every action it had; it now hands the guided flow what each step shows (see BankFileGuide).

  const setupFormId = `${uid}-setup-form`;
  // Setup is the current step while it is incomplete (the form is shown in place); once complete it is a one-line row whose Edit opens a drawer.
  const inlineSetup = !setupComplete && !progressGenerated;
  const closeSetup = () => { setSetupOpen(false); setShowSettingsErrors(false); if (savedSettings) setDraft(savedSettings); };

  // Move focus once the field it names has been drawn (a drawer opening, a read-only value turning into an input).
  useEffect(() => {
    if (!pendingFocus) return;
    const timer = window.setTimeout(() => { focusField(pendingFocus); setPendingFocus(null); }, 80);
    return () => window.clearTimeout(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [pendingFocus, setupOpen, refEditing]);

  const onFix = (target: BankFixTarget) => {
    if (target.kind === 'setup') {
      if (inlineSetup) focusField(target.field);
      else { setSetupOpen(true); setPendingFocus(target.field); }
    } else if (target.kind === 'input') {
      if (target.field === 'batchReference') setRefEditing(true);
      setPendingFocus(target.field);
    } else if (target.kind === 'beneficiary') {
      setBeneficiary({ employeeId: employeeIds?.includes(target.employeeId) ? target.employeeId : null });
    }
  };

  if (load.status === 'loading') {
    return <>{renderGuide({ steps: { check: { body: (
      <div className="flex items-center gap-2 text-body-sm text-fg-muted" role="status" aria-busy="true"><Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" /> {c.loading}</div>
    ) } } })}</>;
  }

  if (load.status === 'error') {
    return <>{renderGuide({ steps: { check: { body: (
      <div className="space-y-3">
        <Alert tone="error"><span role="alert">{load.message}</span></Alert>
        <button type="button" className={btn.ghost} onClick={() => setReloadToken((n) => n + 1)}>
          <RefreshCw className="h-4 w-4" aria-hidden="true" /> {c.retry}
        </button>
      </div>
    ) } } })}</>;
  }

  if (ctx && !isSaudi) {
    return <>{renderGuide({ steps: { check: { body: <Alert tone="info">{c.notSaudi(ctx.countryCode)}</Alert> } } })}</>;
  }

  const bErr = (k: BatchKey) => (showBatchErrors ? batchErrors[k] ?? null : null);
  const describedBy = (k: string, err: string | null, hasHelp: boolean) =>
    err ? `${fid(k)}-err` : hasHelp ? `${fid(k)}-help` : undefined;

  // Reason text for any disabled action button — never disabled silently.
  const validateReason = busy ? c.busy : settingsDirty ? c.needSave : !savedSettingsValid ? c.needSave : savedFormatUnsupported ? c.unsupportedFormat(savedSettings?.formatId ?? '') : null;
  const generateReason = busy ? c.busy
    : !validation ? c.needValidate
    : validationStale ? c.staleValidation
    : !validation.result.canExport ? c.blocked
    : null;
  const downloadReason = busy ? c.busy : !artifact ? c.downloadNeedsArtifact : null;

  const result = validationCurrent ? validation!.result : null;
  const canManageSetup = hasPermission('payroll.structure_manage');
  const refReadOnly = !refEditing && !artifact && !!suggestedReference && batchInput.batchReference === suggestedReference && !bErr('batchReference');
  const showDate = (iso: string) => (iso ? <bdi dir="ltr">{iso}</bdi> : '—');

  const settingsForm = (hideSubmit: boolean) => (
    <SaudiBankSettingsForm c={c} idPrefix={uid} formId={hideSubmit ? setupFormId : undefined} hideSubmit={hideSubmit} draft={draft} setField={setField}
      errors={showSettingsErrors ? settingsErrors : {}} formats={formats} canManage={canManageSetup}
      saving={busy === 'save'} otherBusy={busy !== null && busy !== 'save'} dirty={settingsDirty} onSubmit={onSave} />
  );

  // ── Step 2: the inputs the check runs on, and what the check found
  const inputs = (
    <div className="grid gap-3 sm:grid-cols-2">
      {refReadOnly ? (
        <div>
          <p className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-300">{c.batchReference}</p>
          <p className="flex items-center gap-2 text-sm" data-testid="bank-batch-reference-readonly">
            <bdi dir="ltr" className="font-mono text-fg">{batchInput.batchReference}</bdi>
            <button type="button" className={btn.link} onClick={() => { setRefEditing(true); setPendingFocus('batchReference'); }}>{c.change}</button>
          </p>
          <p className="mt-1 text-[11px] text-slate-400">{c.readOnlyRefHelp}</p>
        </div>
      ) : (
        <FieldShell id={fid('batchReference')} label={c.batchReference}
          help={!artifact && suggestedReference && batchInput.batchReference === suggestedReference ? `${c.batchReferenceHelp} ${c.batchReferenceSuggested}` : c.batchReferenceHelp}
          error={bErr('batchReference')}>
          <input id={fid('batchReference')} className={`${inp} ${bErr('batchReference') ? inpErr : ''}`} value={batchInput.batchReference}
            onChange={(e) => setBatchField('batchReference', e.target.value)} inputMode="numeric" dir="ltr" autoComplete="off"
            aria-invalid={bErr('batchReference') ? true : undefined}
            aria-describedby={describedBy('batchReference', bErr('batchReference'), true)} />
        </FieldShell>
      )}
      <FieldShell id={fid('paymentDate')} label={c.paymentDate} help={c.paymentDateHelp} error={bErr('paymentDate')}>
        <span id={fid('paymentDate')} tabIndex={-1} aria-describedby={describedBy('paymentDate', bErr('paymentDate'), true)} className="block outline-none">
          <DatePicker placeholders aria-label={c.paymentDate} value={batchInput.paymentDate} onChange={(v) => setBatchField('paymentDate', v)} />
        </span>
      </FieldShell>
    </div>
  );

  const exclusionsAndWarnings = result && (
    <>
      {(result.exclusions?.length ?? 0) > 0 && (
        <Alert tone="warn">
          <p className="font-medium">{c.excludedTitle}</p>
          <ul className="mt-1 list-disc space-y-0.5 ps-4 text-xs">
            {result.exclusions!.map(x => (
              <li key={x.employeeId}>
                <span className="font-mono">{x.employeeCode}</span> — <span className="tabular-nums" dir="ltr">{fmtMoney(x.amount, result.currency)}</span> — {x.reason}
              </li>
            ))}
          </ul>
        </Alert>
      )}
      {result.warnings.length > 0 && (
        <Alert tone="warn">
          <p className="font-medium">{c.warnings} ({result.warnings.length})</p>
          <ul className="mt-1 list-disc space-y-0.5 ps-4 text-xs">
            {result.warnings.map((w, i) => <li key={`${w.code}-${i}`}><span className="font-mono">{w.code}</span> — {w.message}</li>)}
          </ul>
        </Alert>
      )}
    </>
  );

  const checkBody = (
    <>
      {inputs}
      {artifact && (
        <p className="text-xs text-slate-500 dark:text-slate-400">
          {existingMatches ? c.existingMatches : c.existingDiffers(artifact.batchReference, (artifact.paymentDate ?? '').slice(0, 10))}
        </p>
      )}
      {result && result.errors.length > 0 && <IssueList issues={result.errors} c={c} labels={employeeLabels} onFix={onFix} />}
      {exclusionsAndWarnings}
    </>
  );

  // ── Step 3: what is about to be frozen
  const createBody = (
    <>
      <dl className="grid gap-x-6 gap-y-1 text-body-sm sm:grid-cols-2" data-testid="bank-create-recap">
        <div><dt className="text-slate-400">{c.paymentDate}</dt><dd className="font-medium text-fg">{showDate(batchInput.paymentDate)}</dd></div>
        <div><dt className="text-slate-400">{c.batchReference}</dt><dd className="font-mono text-fg" dir="ltr">{batchInput.batchReference || '—'}</dd></div>
        {result && <div><dt className="text-slate-400">{c.employees}</dt><dd className="tabular-nums text-fg">{result.employeeCount}</dd></div>}
        {result && <div><dt className="text-slate-400">{c.total}</dt><dd className="tabular-nums text-fg" dir="ltr">{fmtMoney(result.totalAmount, result.currency)}</dd></div>}
      </dl>
      <p className="text-meta text-fg-muted">{c.changeInCheck}</p>
      {exclusionsAndWarnings}
      <p className="text-meta text-fg-muted">{c.generatedNote}</p>
    </>
  );

  // ── Step 4: the frozen files
  const downloadBody = artifact && (
    <div id="bank-wps-generated-export" tabIndex={-1} className="rounded-xl border border-teal-200 bg-teal-50/50 p-4 outline-none dark:border-teal-500/20 dark:bg-teal-500/5">
      <p className="text-sm font-semibold text-slate-800 dark:text-white">{c.artifactTitle}</p>
      <dl className="mt-2 grid gap-x-6 gap-y-1 text-xs sm:grid-cols-2">
        <div><dt className="text-slate-400">{c.artifactId}</dt><dd className="break-all font-mono text-slate-700 dark:text-slate-300" dir="ltr">{artifact.id}</dd></div>
        <div><dt className="text-slate-400">{c.format}</dt><dd className="text-slate-700 dark:text-slate-300">{bankFormatLabel(t, formats, artifact.formatId, cc.suffix)}</dd></div>
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
  );

  // ── The live region under the header: what just happened
  const live = (
    <>
      {notice && <Alert tone="ok">{notice}</Alert>}
      {actionError && (
        <Alert tone="error">
          <p role="alert">{actionError.message}</p>
          {actionError.issues && actionError.issues.length > 0 && <IssueList issues={actionError.issues} c={c} labels={employeeLabels} onFix={onFix} />}
        </Alert>
      )}
      {validationStale && <Alert tone="warn">{c.staleValidation}</Alert>}
      {result && (
        <Alert tone={result.canExport && result.errors.length === 0 ? 'ok' : 'error'}>
          <p className="font-medium">{result.canExport && result.errors.length === 0 ? c.validationOk : c.validationBlocked(result.errors.length)}</p>
          <p className="mt-1 text-xs">
            {c.employees}: <span className="tabular-nums">{result.employeeCount}</span> · {c.total}: <span className="tabular-nums" dir="ltr">{fmtMoney(result.totalAmount, result.currency)}</span>
          </p>
          {(result.exclusions?.length ?? 0) > 0 && (
            <p className="mt-1 text-xs">
              {c.excludedSummary(result.exclusions!.length)}: <span className="tabular-nums" dir="ltr">{fmtMoney(result.excludedTotal ?? 0, result.currency)}</span>
              {' '}({c.runNet}: <span className="tabular-nums" dir="ltr">{fmtMoney(result.runNetTotal ?? 0, result.currency)}</span>)
            </p>
          )}
        </Alert>
      )}
    </>
  );

  // ── The footer: the one-line bank setup, the notice, the secondary links
  const accountTail = savedSettings ? maskedTail(isCustomFormatId(savedSettings.formatId) ? savedSettings.debitAccountIban : savedSettings.mainAccountNumber) : '';
  const setupSummary = savedSettings && setupComplete ? (
    <>
      <span>{selectedFormat ? bankFormatLabel(t, formats, savedSettings.formatId, cc.suffix) : savedSettings.formatId}</span>
      {accountTail && <> · {c.account} <bdi dir="ltr">{accountTail}</bdi></>}
    </>
  ) : null;

  const footer = (
    <>
      {!inlineSetup && (
        <div className="flex flex-wrap items-center justify-between gap-2 text-body-sm" data-testid="bank-setup-summary">
          <p className="min-w-0 text-fg">
            <span className="font-semibold">{c.bankSetup}:</span>{' '}
            {setupSummary}{setupSummary ? ' · ' : ''}
            <span className={setupComplete ? 'font-medium text-success-fg' : 'font-medium text-warning-fg'}>{setupComplete ? c.setupComplete : c.setupIncomplete}</span>
          </p>
          <button type="button" className="btn-secondary btn-sm" onClick={() => setSetupOpen(true)}>{c.edit}</button>
        </div>
      )}
      {formats.length === 0 && <Alert tone="error">{c.noFormats}</Alert>}
      {savedFormatUnsupported && <Alert tone="error">{c.unsupportedFormat(savedSettings?.formatId ?? '')}</Alert>}
      {!bankFileProductionAllowed(hasPermission) && <Alert tone="info">{c.noExportPerm}</Alert>}

      <Alert tone="warn">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <p className="font-medium" data-testid="bank-pays-nobody">{c.payNobody}</p>
          <button type="button" className={btn.link} aria-expanded={detailsOpen} aria-controls={fid('notice-details')} onClick={() => setDetailsOpen(v => !v)}>
            {detailsOpen ? c.hideDetails : c.showDetails}
          </button>
        </div>
        {detailsOpen && (
          <div id={fid('notice-details')} className="mt-2 space-y-2 text-xs" data-testid="bank-notice-details">
            <p className="font-semibold">{c.disclaimerTitle}</p>
            <p>{c.disclaimer}</p>
            <p>{c.legacyNote}</p>
            <p>{t('Saudi bank payroll file: the Payroll payment instruction file (bank format) — the ANB Connect instruction with WPS fields — is the file to send to the bank. Mudad accepts only the WPS file your bank signs — keep the bank’s output file or a Mudad screenshot as evidence before marking the batch Accepted.')}</p>
            {draftIsCustom && <p>{cc.notice}</p>}
            <dl className="grid gap-x-6 gap-y-2 sm:grid-cols-2">
              <div><dt className="text-slate-500">{c.company}</dt><dd className="font-medium text-slate-800 dark:text-white">{ctx?.companyName}</dd></div>
              <div><dt className="text-slate-500">{c.runStatus}</dt><dd className="font-medium text-slate-800 dark:text-white">{enumLabel(t, 'PayrollRunStatus', ctx?.runStatus)}</dd></div>
              {selectedFormat && (
                <>
                  {!isCustomFormatId(selectedFormat.id) && <div><dt className="text-slate-500">{c.bank} · {c.channel}</dt><dd className="font-medium text-slate-800 dark:text-white">{t(selectedFormat.bank)} · {t(selectedFormat.channel)}</dd></div>}
                  <div><dt className="text-slate-500">{c.acceptance}</dt><dd className="font-mono">{bankAcceptanceLabel(t, selectedFormat.acceptanceStatus)}</dd></div>
                  {!isCustomFormatId(selectedFormat.id) && <div><dt className="text-slate-500">{c.reviewedOn}</dt><dd>{selectedFormat.reviewedOn}</dd></div>}
                  {!isCustomFormatId(selectedFormat.id) && <div className="min-w-0"><dt className="text-slate-500">{c.source}</dt>
                    <dd className="truncate">
                      <a href={selectedFormat.sourceUrl} target="_blank" rel="noopener noreferrer" className={`${btn.link} inline-flex items-center gap-1`} dir="ltr" title={selectedFormat.sourceUrl}>
                        {selectedFormat.sourceUrl} <ExternalLink className="h-3 w-3 shrink-0" aria-hidden="true" />
                      </a>
                    </dd>
                  </div>}
                </>
              )}
            </dl>
          </div>
        )}
      </Alert>

      <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-body-sm">
        {ctx && bankTemplatesAllowed(hasPermission) && <button type="button" className={btn.link} onClick={() => setFormatsOpen(true)}>{c.bankFormats}</button>}
        {employeeIds && <button type="button" className={btn.link} onClick={() => setBeneficiary({ employeeId: null })}>{c.editEmployeeBank}</button>}
      </div>
    </>
  );

  // ── Drawers: the setup form, the bank formats (custom templates), one employee's bank details
  const overlays = (
    <>
      <Modal isOpen={setupOpen && !inlineSetup} title={c.setupTitle} size="lg" placement="end" onClose={closeSetup} closeDisabled={busy === 'save'} closeLabel={t('Close')}>
        {settingsForm(false)}
      </Modal>
      {/* Custom bank templates: layout configured by the customer, approved by a second person. The template routes stay with
          payroll.export, so the editor is not offered to someone who only prepares the file. */}
      {ctx && bankTemplatesAllowed(hasPermission) && (
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
      {employeeIds && (
        <Modal isOpen={beneficiary !== null} title={c.employeeBankTitle} size="lg" placement="end" onClose={() => setBeneficiary(null)} closeLabel={t('Close')}>
          <SaudiBankBeneficiaryEditor key={`${batchId}-${beneficiary?.employeeId ?? 'any'}`} employeeIds={employeeIds} employeeLabels={employeeLabels}
            initialEmployeeId={beneficiary?.employeeId ?? undefined} onChange={() => setValidation(null)} />
        </Modal>
      )}
    </>
  );

  return <>{renderGuide({
    steps: {
      setup: inlineSetup ? { body: settingsForm(true) } : undefined,
      check: {
        summary: result ? c.checkPassedWith(result.employeeCount, fmtMoney(result.totalAmount, result.currency)) : c.checkPassed,
        reopen: artifact ? 'view' : 'change',
        body: checkBody,
      },
      create: {
        summary: artifact ? c.createdSummary(artifact.batchReference, (artifact.paymentDate ?? '').slice(0, 10)) : undefined,
        body: createBody,
      },
      download: { summary: c.submittedSummary, reopen: 'view', body: downloadBody },
    },
    actions: {
      setup: { label: busy === 'save' ? c.saving : c.saveSetup, form: setupFormId, disabled: busy !== null && busy !== 'save', busy: busy === 'save', reason: busy !== null && busy !== 'save' ? c.busy : null },
      check: { label: busy === 'validate' ? c.validating : c.validate, onClick: onValidate, disabled: !!validateReason, busy: busy === 'validate', reason: validateReason },
      create: { label: busy === 'generate' ? c.generating : c.generate, onClick: onGenerate, disabled: !canGenerate, busy: busy === 'generate', reason: generateReason },
      download: { label: busy === 'download' ? c.downloading : c.download, onClick: onDownload, disabled: !!downloadReason, busy: busy === 'download', reason: downloadReason },
    },
    live,
    liveRef: resultRef,
    footer,
    overlays,
  })}</>;
}

function IssueList({ issues, c, labels, onFix }: { issues: SaudiBankExportIssue[]; c: Copy; labels?: Record<number, string>; onFix: (target: BankFixTarget) => void }) {
  // Every blocker is rendered (scrollable, never cut short): the file is all-or-nothing. Each one says where it is cured.
  return (
    <ul className="max-h-72 space-y-1 overflow-y-auto pe-1 text-xs" data-testid="bank-blockers">
      {issues.map((i, idx) => {
        const target = bankFixTarget(i);
        return (
          <li key={`${i.code}-${i.employeeId ?? ''}-${i.field ?? ''}-${idx}`} className="rounded-md bg-white/60 px-2 py-1.5 dark:bg-black/10">
            <span className="font-mono text-[11px] opacity-80">{i.code}</span>
            {i.employeeId != null && i.employeeId !== '' && <span className="ms-2 font-medium"><EmployeeLabelText label={labels?.[Number(i.employeeId)] || c.employee(String(i.employeeId))} /></span>}
            {i.field && <span className="ms-2 opacity-80">{c.field}: <span className="font-mono" dir="ltr">{i.field}</span></span>}
            <span className="block">{i.message}</span>
            {target.kind === 'people' ? (
              <Link href={`/people/${target.employeeId}`} className={`${btn.link} mt-0.5 inline-block`} data-testid="bank-blocker-fix">{c.fixInPeople}</Link>
            ) : target.kind !== 'none' && (
              <button type="button" className={`${btn.link} mt-0.5`} data-testid="bank-blocker-fix" onClick={() => onFix(target)}>{c.fix}</button>
            )}
          </li>
        );
      })}
    </ul>
  );
}

export default SaudiBankExportPanel;
