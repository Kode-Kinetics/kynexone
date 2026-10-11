// End-of-service provision panel (components/gl/EosbProvisionPanel.tsx): the monthly provision switch, the
// provision figures and employee drill-down, the actuarial true-up, and the move of old 2104/2105
// absence-deduction balances. Keys already translated elsewhere (Amount, Month, Reason, Reject, Try again, …)
// are reused, not redefined here.
const ar: Record<string, string> = {
  'End-of-service provision': 'مخصص مكافأة نهاية الخدمة',
  'Choose a company above to see its end-of-service provision.': 'اختر شركة من الأعلى لعرض مخصص مكافأة نهاية الخدمة الخاص بها.',
  'Loading the end-of-service provision…': 'جارٍ تحميل مخصص مكافأة نهاية الخدمة…',
  'The end-of-service provision could not be loaded.': 'تعذّر تحميل مخصص مكافأة نهاية الخدمة.',
  'That could not be saved.': 'تعذّر الحفظ.',
  'Each month, when payroll is locked, the end-of-service amount every employee has earned so far is measured, and only the change is booked: expense {expense}, provision {provision}.':
    'في كل شهر، عند قفل الرواتب، يُقاس مبلغ نهاية الخدمة الذي استحقه كل موظف حتى تاريخه، ويُقيَّد الفرق فقط: المصروف {expense}، والمخصص {provision}.',
  'Monthly provisioning is on for {company} since {date}.': 'المخصص الشهري مفعّل لـ {company} منذ {date}.',
  'Monthly provisioning is off for {company}. Payroll journals do not include an end-of-service provision.':
    'المخصص الشهري متوقف لـ {company}. لا تتضمن قيود الرواتب مخصصًا لمكافأة نهاية الخدمة.',
  'Scheduled: provisioning starts on {date}.': 'مجدول: يبدأ المخصص في {date}.',
  'Scheduled: provisioning stops on {date}.': 'مجدول: يتوقف المخصص في {date}.',
  'This setting comes from the group default.': 'هذا الإعداد مأخوذ من الإعداد الافتراضي للمجموعة.',
  'Measured as if the employer ended the contract at each month end (the full Article 84 award). If someone resigns and receives less, the unused provision is released when their settlement is approved.':
    'يُقاس كما لو أنهى صاحب العمل العقد في نهاية كل شهر (المكافأة الكاملة وفق المادة 84). إذا استقال موظف واستحق أقل، يُعكس المخصص غير المستخدم عند اعتماد تسويته.',
  'From month': 'اعتبارًا من شهر',
  'Start monthly provisioning': 'بدء المخصص الشهري',
  'Stop monthly provisioning': 'إيقاف المخصص الشهري',
  'Monthly provisioning will start from {month}.': 'سيبدأ المخصص الشهري اعتبارًا من {month}.',
  'Monthly provisioning will stop from {month}.': 'سيتوقف المخصص الشهري اعتبارًا من {month}.',
  'Only someone who manages company rates can switch provisioning on or off.': 'لا يمكن تشغيل المخصص أو إيقافه إلا لمن يدير معدلات الشركة.',
  'Provision in the books': 'المخصص المسجّل في الدفاتر',
  'Credits minus debits on {account} for this company, including approved actuarial adjustments.':
    'الدائن ناقص المدين في الحساب {account} لهذه الشركة، شاملًا التسويات الاكتوارية المعتمدة.',
  "Employees' measured obligation": 'الالتزام المقاس للموظفين',
  'The latest month-end measurement for each employee still provided for.': 'آخر قياس في نهاية الشهر لكل موظف ما زال له مخصص.',
  'Actuarial adjustments': 'التسويات الاكتوارية',
  'Approved true-ups from an actuarial report, on top of the statutory measurement.': 'تسويات معتمدة من تقرير اكتواري، إضافةً إلى القياس النظامي.',
  'No payroll has been locked since provisioning started, so no employee has been measured yet.':
    'لم يُقفل أي مسير رواتب منذ بدء المخصص، لذلك لم يُقَس أي موظف بعد.',
  'No employee has been measured yet.': 'لم يُقَس أي موظف بعد.',
  'Hide employees': 'إخفاء الموظفين',
  '{count, plural, one {Show the # employee behind these figures} other {Show the # employees behind these figures}}':
    '{count, plural, one {عرض الموظف الذي تستند إليه هذه الأرقام} two {عرض الموظفَين اللذين تستند إليهما هذه الأرقام} few {عرض # موظفين تستند إليهم هذه الأرقام} many {عرض # موظفًا تستند إليهم هذه الأرقام} other {عرض # موظف تستند إليهم هذه الأرقام}}',
  'Last measured': 'آخر قياس',
  'Years of service': 'سنوات الخدمة',
  'Wage it is measured on': 'الأجر المعتمد في القياس',
  'Obligation': 'الالتزام',
  'Provided in the books': 'المخصص في الدفاتر',
  'Adjust to an actuarial valuation': 'التسوية وفق تقييم اكتواري',
  'Enter the difference the actuary reported: a positive amount raises the provision, a negative amount lowers it. Nothing is posted until a second person approves.':
    'أدخل الفرق الذي أورده الخبير الاكتواري: المبلغ الموجب يرفع المخصص والسالب يخفضه. لا يُرحّل شيء حتى يعتمده شخص آخر.',
  'Actuarial report reference': 'مرجع التقرير الاكتواري',
  'Request adjustment': 'طلب التسوية',
  'Sent for approval. A second person must approve it before it posts.': 'أُرسل للاعتماد. يجب أن يعتمده شخص آخر قبل الترحيل.',
  'No actuarial adjustment has been requested.': 'لم تُطلب أي تسوية اكتوارية.',
  'Move balances booked under the old absence-deduction accounts': 'نقل الأرصدة المسجّلة في حسابات خصومات الغياب القديمة',
  'Attendance and unpaid-leave deductions used to be booked as amounts owed (2104 and 2105). Nobody is owed them: they reduce payroll cost, and new payrolls now book them that way. What is still sitting in the old accounts is only moved when you ask and a second person approves.':
    'كانت خصومات الحضور والإجازات غير المدفوعة تُسجَّل كمبالغ مستحقة الدفع (2104 و2105). لا أحد يستحقها: فهي تخفض تكلفة الرواتب، وتُسجَّل الرواتب الجديدة الآن بهذه الطريقة. ولا يُنقل ما بقي في الحسابات القديمة إلا بطلبك وباعتماد شخص آخر.',
  'Loading the old absence-deduction balances…': 'جارٍ تحميل أرصدة خصومات الغياب القديمة…',
  'The old absence-deduction balances could not be loaded.': 'تعذّر تحميل أرصدة خصومات الغياب القديمة.',
  'Nothing to move: the old absence-deduction accounts are empty for this company.': 'لا شيء للنقل: حسابات خصومات الغياب القديمة فارغة لهذه الشركة.',
  '{amount} still recorded as owed': 'ما زال {amount} مسجّلًا كمبلغ مستحق',
  '{amount} debit balance': 'رصيد مدين بقيمة {amount}',
  'Suggested destination: {account}': 'الحساب المقترح: {account}',
  'A move is waiting for approval': 'يوجد نقل بانتظار الاعتماد',
  'Move this balance': 'نقل هذا الرصيد',
  'Accounting date': 'التاريخ المحاسبي',
  'Different destination account (optional)': 'حساب وجهة مختلف (اختياري)',
  'For example, a retained-earnings account code': 'مثلًا رمز حساب الأرباح المبقاة',
  'Request the move': 'طلب النقل',
  '{amount} sits in these accounts without a company (posted before companies were recorded on journals). It is not moved from here; correct it with a manual journal in your ERP.':
    'يوجد {amount} في هذه الحسابات دون شركة (رُحّل قبل تسجيل الشركات على القيود). لا يُنقل من هنا؛ صحّحه بقيد يدوي في نظام تخطيط الموارد لديك.',
  'No move has been requested.': 'لم يُطلب أي نقل.',
  'Approved and posted': 'معتمد ومرحّل',
  'Raise the provision by {amount} for {period} (report {reference})': 'رفع المخصص بمقدار {amount} عن {period} (التقرير {reference})',
  'Lower the provision by {amount} for {period} (report {reference})': 'خفض المخصص بمقدار {amount} عن {period} (التقرير {reference})',
  'Move {amount} from {source} to {target} on {date}': 'نقل {amount} من {source} إلى {target} بتاريخ {date}',
  'Requested by {name}: {reason}': 'طلبه {name}: {reason}',
  'Decided by {name} on {date}': 'قرّره {name} في {date}',
  'You requested this, so someone else must approve it.': 'أنت من طلب هذا، لذا يجب أن يعتمده شخص آخر.',
  'Posting…': 'جارٍ الترحيل…',
  'Approve and post': 'اعتماد وترحيل',
  'Why reject it? (required)': 'لماذا يُرفض؟ (مطلوب)',
  'Confirm rejection': 'تأكيد الرفض',
};

export const glEosb = { ar, en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) };
