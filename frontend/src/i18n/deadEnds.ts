/**
 * Dead ends closed in the 2026-11 hotfix train: documents HR must verify (G-004) and the off-cycle run that pays an approved
 * final settlement (G-006). Terms follow ./employeeWorkspace and ./payApprovals: verify = تحقق, reject = رفض, off-cycle run = دورة رواتب
 * خارج الدورة, final settlement = التسوية النهائية, Payroll Manager = مدير الرواتب.
 */
const ar: Record<string, string> = {
  // G-004 — HR verifies uploaded documents.
  'To verify': 'للتحقق',
  'Verify this document?': 'هل تتحقق من هذا المستند؟',
  '{document} will count as verified. The employee is told, and any readiness rule that needs a verified document is met.':
    'سيُعدّ {document} موثّقًا. يُبلَّغ الموظف، وتتحقق أي قاعدة جاهزية تشترط مستندًا موثّقًا.',
  'Verify document': 'التحقق من المستند',
  'Reject this document': 'رفض هذا المستند',
  'Reject document': 'رفض المستند',
  'Rejecting…': 'جارٍ الرفض…',
  'Reason the employee will read': 'السبب الذي سيقرؤه الموظف',
  'Say what is wrong and what to upload instead. At least 3 characters.': 'اذكر ما الخطأ وما الذي ينبغي رفعه بدلًا منه. 3 أحرف على الأقل.',
  'The documents waiting for verification could not be loaded.': 'تعذّر تحميل المستندات التي تنتظر التحقق.',
  'Documents employees uploaded that HR has not checked yet. Open the file, then verify it, or reject it with a reason the employee will read.':
    'مستندات رفعها الموظفون ولم تراجعها الموارد البشرية بعد. افتح الملف ثم تحقق منه، أو ارفضه مع ذكر سبب يقرؤه الموظف.',
  'Nothing is waiting for verification.': 'لا يوجد ما ينتظر التحقق.',
  'Every uploaded document has been checked.': 'روجعت كل المستندات المرفوعة.',
  '{count, plural, one {# document is waiting} other {# documents are waiting}}':
    '{count, plural, one {مستند واحد ينتظر} two {مستندان ينتظران} few {# مستندات تنتظر} many {# مستندًا ينتظر} other {# مستند ينتظر}}',
  'Uploaded': 'تاريخ الرفع',
  'Decision': 'القرار',
  'Open file': 'فتح الملف',
  'Page {page} of {pages}': 'صفحة {page} من {pages}',
  'Reason: {reason}': 'السبب: {reason}',
  'Waiting for HR to verify': 'بانتظار تحقق الموارد البشرية',
  'Verified by HR': 'موثّق من الموارد البشرية',
  'Rejected by HR': 'مرفوض من الموارد البشرية',
  'Accepted by HR': 'مقبول من الموارد البشرية',
  'Expired': 'منتهي الصلاحية',
  'Documents I gave HR': 'المستندات التي قدّمتها للموارد البشرية',
  'You have not given HR any documents yet.': 'لم تقدّم للموارد البشرية أي مستندات بعد.',
  'HR did not leave a reason. Ask HR, then upload a new copy.': 'لم تترك الموارد البشرية سببًا. اسألها ثم ارفع نسخة جديدة.',
  'Upload a corrected copy under My employee details.': 'ارفع نسخة مصحّحة من «بياناتي الوظيفية».',

  // G-006 — the Payroll Manager creates the off-cycle run that pays an approved final settlement.
  'Off-cycle runs to create': 'دورات رواتب خارج الدورة بانتظار الإنشاء',
  'A leaver is paid through an off-cycle run. Each approved settlement below has none yet; the labour law sets the date it must be paid by.':
    'يُصرف مستحق المغادر عبر دورة رواتب خارج الدورة. لكل تسوية معتمدة أدناه لا توجد دورة بعد، ويحدد نظام العمل موعد وجوب صرفها.',
  'Due by {date}': 'يستحق الصرف بحلول {date}',
  'Create off-cycle run': 'إنشاء دورة خارج الدورة',
  'Creating…': 'جارٍ الإنشاء…',
  'Create the off-cycle run?': 'هل تنشئ الدورة خارج الدورة؟',
  'This creates an off-cycle payroll run, in the company of this settlement, that pays only {name}’s final settlement of {amount}. It pays no recurring salary. You then process, approve and lock it as usual.':
    'تُنشأ دورة رواتب خارج الدورة، في شركة هذه التسوية، تصرف التسوية النهائية لـ {name} بمبلغ {amount} فقط. ولا تصرف أي راتب متكرر. ثم تعالجها وتعتمدها وتقفلها كالمعتاد.',
  'A Payroll Manager creates this run.': 'يُنشئ مدير الرواتب هذه الدورة.',
  'Off-cycle run created': 'أُنشئت الدورة خارج الدورة',
  'Open the off-cycle run': 'فتح الدورة خارج الدورة',
  'Payroll has to create the off-cycle run, due by {date}.': 'على قسم الرواتب إنشاء الدورة خارج الدورة، ويستحق الصرف بحلول {date}.',
};

export const deadEnds = { ar, en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) as Record<string, string> };
