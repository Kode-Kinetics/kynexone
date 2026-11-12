// Payroll → GL Journal for a posted run (one block per journal), the void dialog's cash elections, and the permission
// messages on payment actions. Finance Arabic follows glSetup.ts / payrollCostCentres.ts (مدين، دائن، مركز التكلفة، قيد)
// and the owner's terms: قيد الاستحقاق، تسوية صافي الرواتب، سداد التأمينات. English keys are the English text (see
// i18n/translations.ts). Arabic is draft until the owner or an Arabic reviewer approves it (gate G-RELEASE). Words already
// in the shared dictionary (Account, Description, Debit, Credit, Period, Total, Total debits, Total credits, Balanced,
// Out of balance, Cost centre, ERP code, Net pay, Cancel, Working…) are reused, not redefined.
const ar: Record<string, string> = {
  // ── Journal titles, by the event that posted them ──
  'Payroll accrual': 'قيد استحقاق الرواتب',
  'Net pay settlement': 'تسوية صافي الرواتب',
  'Net pay settlement reversal': 'عكس تسوية صافي الرواتب',
  'GOSI remittance': 'سداد التأمينات',
  'Tax remittance': 'سداد الضريبة',
  'Loan remittance': 'سداد أقساط القروض',
  'Statutory remittance': 'سداد المستحقات النظامية',
  'GOSI remittance reversal': 'عكس سداد التأمينات',
  'Tax remittance reversal': 'عكس سداد الضريبة',
  'Loan remittance reversal': 'عكس سداد أقساط القروض',
  'Statutory remittance reversal': 'عكس سداد المستحقات النظامية',
  'Disbursed funds reclassification': 'إعادة تصنيف المبالغ المصروفة',
  'Void': 'قيد الإلغاء',
  'Payroll adjustment': 'تسوية على الرواتب',

  // ── Posted run: layout ──
  'Entry date': 'تاريخ القيد',
  'Posted by': 'رحّله',
  'Other journals posted for this run': 'قيود أخرى رُحِّلت لهذا المسير',
  'The totals above are the payroll accrual only. Settlement, remittance and void entries are separate journals, listed below, each balanced on its own.': 'الإجماليات أعلاه تخص قيد استحقاق الرواتب وحده. قيود التسوية والسداد والإلغاء قيود مستقلة مدرجة أدناه، وكلٌّ منها متوازن بذاته.',
  'Journal lines for {title}': 'بنود قيد {title}',
  'Reversed': 'معكوس',
  'This run was posted without cost centres.': 'رُحِّل هذا المسير بدون مراكز تكلفة.',

  // ── Void dialog ──
  'Void {period}': 'إلغاء مسير {period}',
  "Voiding never deletes. The run's status becomes Voided, its payslips are voided, and contra-entries are written for any general-ledger rows it already posted. The period is then free for a replacement run. This is irreversible and the reason is recorded against your identity.": 'لا يحذف الإلغاء أي بيانات. تصبح حالة المسير «ملغى» وتُلغى قسائم الرواتب، وتُسجَّل قيود عكسية لأي بنود في دفتر الأستاذ رُحِّلت من قبل، ثم تُتاح الفترة لمسير بديل. هذا الإجراء لا رجعة فيه ويُسجَّل السبب باسمك.',
  'Recorded against your identity on the audit trail.': 'يُسجَّل باسمك في سجل التدقيق.',
  'Void run': 'إلغاء المسير',
  'The action was refused.': 'رُفض الإجراء.',
  'Cash has already left the bank for this run': 'خرجت أموال هذا المسير من الحساب البنكي بالفعل',
  'A void must not silently put that cash back on the books. For each payment below, say what happened to the money.': 'يجب ألا يُعيد الإلغاء هذه الأموال إلى الدفاتر تلقائيًا. حدّد لكل دفعة أدناه ما جرى للمبلغ.',
  'Statutory remittances': 'المستحقات النظامية المسدَّدة',
  '{amount} credited to {account}': '{amount} دائن على حساب {account}',
  'What happened to this money?': 'ما الذي جرى لهذه الأموال؟',
  'Funds recalled': 'استُردّت الأموال',
  'The bank returned the money. The payment journal is reversed and cash goes back up.': 'أعاد البنك المبلغ. يُعكس قيد الدفع ويرتفع رصيد النقد.',
  'Funds already disbursed': 'صُرفت الأموال ولم تُسترد',
  'The money is gone and has not come back. It stays out of cash and is recorded as recoverable.': 'خرج المبلغ ولم يعد. يبقى خارج النقد ويُسجَّل مبلغًا مستحق الاسترداد.',
  'Recall reference': 'مرجع الاسترداد',
  'Bank recall or return reference': 'مرجع استرداد البنك أو إعادة المبلغ',
  'Required when the funds were recalled.': 'مطلوب عند استرداد الأموال.',
  'Choose what happened to every payment above to continue.': 'اختر ما جرى لكل دفعة أعلاه للمتابعة.',

  // ── Payment actions without payroll.export ──
  "You don't have permission to create payment batches — ask an administrator.": 'ليس لديك صلاحية إنشاء دفعات الرواتب — اطلب ذلك من مسؤول النظام.',
  "You don't have permission to generate or update WPS files — ask an administrator.": 'ليس لديك صلاحية إنشاء ملفات نظام حماية الأجور أو تحديثها — اطلب ذلك من مسؤول النظام.',
  "You don't have permission to settle net pay to the general ledger — ask an administrator.": 'ليس لديك صلاحية تسوية صافي الرواتب في دفتر الأستاذ — اطلب ذلك من مسؤول النظام.',
  "You don't have permission to remit statutory amounts — ask an administrator.": 'ليس لديك صلاحية سداد المستحقات النظامية — اطلب ذلك من مسؤول النظام.',
  'Only an administrator can create payment batches, generate WPS files, settle net pay and remit. You can still review them here.': 'يقتصر إنشاء دفعات الرواتب وملفات نظام حماية الأجور وتسوية صافي الرواتب والسداد على مسؤول النظام. ويمكنك الاطلاع عليها هنا.',
  'Needs an administrator': 'يتطلب مسؤول النظام',
};

export const payrollGlUi = { ar, en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) };
