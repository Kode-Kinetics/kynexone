// Who approves a loan payment batch (components/loans/LoanPoliciesTab.tsx, LoanPaymentsTab.tsx): a company chooses a
// separate approver (the default) or lets the Finance user who prepared the batch approve it with a recorded reason,
// optionally up to a limit. Only an Admin changes it; a borrower never approves a batch paying their own loan.
const ar: Record<string, string> = {
  'Who approves loan payment batches': 'من يعتمد دفعات صرف القروض',
  'Approval of payment batches': 'اعتماد دفعات الصرف',
  'A different Finance user approves (recommended)': 'يعتمدها مستخدم مالي آخر (موصى به)',
  'The Finance user who prepared it may approve it, with a recorded reason': 'يجوز للمستخدم المالي الذي أعدّها اعتمادها مع تسجيل السبب',
  'Largest batch one person may approve alone': 'أكبر دفعة يجوز لشخص واحد اعتمادها منفردًا',
  'Zero means no limit; larger batches still need a second approver. Every self-approval is audited. An employee can never approve a batch that pays their own loan.':
    'الصفر يعني بلا حد، وتظل الدفعات الأكبر بحاجة إلى معتمد ثانٍ. يُسجَّل كل اعتماد ذاتي في سجل التدقيق. لا يجوز لموظف أبدًا اعتماد دفعة تصرف قرضه.',
  'Only an Admin can change this setting. It is kept as it is when you publish a new version.':
    'لا يغيّر هذا الإعداد إلا المسؤول. ويبقى كما هو عند نشر إصدار جديد.',
  'Why are you approving your own batch?': 'لماذا تعتمد الدفعة التي أعددتها بنفسك؟',
  "You created this batch. Your company's loan policy lets you approve it yourself; you will be asked for a reason that is kept in the audit trail.":
    'أنت من أنشأ هذه الدفعة. تسمح لك سياسة القروض في شركتك باعتمادها بنفسك، وسيُطلب منك سبب يُحفظ في سجل التدقيق.',
  'You created this batch. Another Finance user must approve it.': 'أنت من أنشأ هذه الدفعة. يجب أن يعتمدها مستخدم مالي آخر.',
  'Give the reason you are approving this batch yourself (at least 10 characters). It is kept in the audit trail.':
    'اذكر سبب اعتمادك لهذه الدفعة بنفسك (10 أحرف على الأقل). يُحفظ السبب في سجل التدقيق.',
};

export const loanBatchApprover = { ar, en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) };
