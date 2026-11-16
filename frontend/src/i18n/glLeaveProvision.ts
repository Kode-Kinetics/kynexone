// Annual leave provision section (components/gl/LeaveProvisionSection.tsx, shown under Setup → GL & rates →
// End-of-service provision): the monthly switch, the provision figures and the employee drill-down. Keys already
// translated elsewhere (Employee, Try again, From month, Hide employees, Provision in the books, …) are reused.
const ar: Record<string, string> = {
  'Nobody has been measured yet.': 'لم يُقَس أي موظف بعد.',
  'Annual leave provision': 'مخصص الإجازة السنوية',
  'Loading the annual leave provision…': 'جارٍ تحميل مخصص الإجازة السنوية…',
  'The annual leave provision could not be loaded.': 'تعذّر تحميل مخصص الإجازة السنوية.',
  "Each month, when payroll is locked, the value of every employee's unused annual leave is measured, and only the change is booked: expense {expense}, provision {provision}.":
    'في كل شهر، عند قفل الرواتب، تُقاس قيمة الإجازة السنوية غير المستخدمة لكل موظف، ويُقيَّد الفرق فقط: المصروف {expense}، والمخصص {provision}.',
  'Annual leave provisioning is on for {company} since {date}.': 'مخصص الإجازة السنوية مفعّل لـ {company} منذ {date}.',
  'Annual leave provisioning is off for {company}. Payroll journals do not include an annual leave provision.':
    'مخصص الإجازة السنوية متوقف لـ {company}. لا تتضمن قيود الرواتب مخصصًا للإجازة السنوية.',
  'Scheduled: annual leave provisioning starts on {date}.': 'مجدول: يبدأ مخصص الإجازة السنوية في {date}.',
  'Scheduled: annual leave provisioning stops on {date}.': 'مجدول: يتوقف مخصص الإجازة السنوية في {date}.',
  'Unused days are valued at the day rate a final settlement uses, {basis}, so the provision and the leave payout on exit agree. Leave taken lowers the provision the following month.':
    'تُقيَّم الأيام غير المستخدمة بسعر اليوم الذي تستخدمه التسوية النهائية، أي {basis}، فيتطابق المخصص مع بدل الإجازة عند انتهاء الخدمة. وتخفض الإجازة المأخوذة المخصص في الشهر التالي.',
  'The first payroll locked after you switch this on books everything owed for unused leave to date in one entry (the opening catch-up), so that month will look like a large one-off.':
    'أول مسير رواتب يُقفل بعد التفعيل يقيّد كل المستحق عن الإجازة غير المستخدمة حتى تاريخه في قيد واحد (تسوية الرصيد الافتتاحي)، لذلك سيبدو ذلك الشهر كمبلغ كبير لمرة واحدة.',
  'Start annual leave provisioning': 'بدء مخصص الإجازة السنوية',
  'Stop annual leave provisioning': 'إيقاف مخصص الإجازة السنوية',
  'Annual leave provisioning will start from {month}.': 'سيبدأ مخصص الإجازة السنوية اعتبارًا من {month}.',
  'Annual leave provisioning will stop from {month}.': 'سيتوقف مخصص الإجازة السنوية اعتبارًا من {month}.',
  'Credits minus debits on {account} for this company.': 'الدائن ناقص المدين في الحساب {account} لهذه الشركة.',
  'Value of unused leave': 'قيمة الإجازة غير المستخدمة',
  'Unused leave days': 'أيام الإجازة غير المستخدمة',
  'Days the employees measured at the latest month end could still take or be paid for.':
    'الأيام التي كان بإمكان الموظفين المقاسين في آخر نهاية شهر أخذها أو تقاضي بدلها.',
  'Unused days': 'الأيام غير المستخدمة',
  'Day rate': 'أجر اليوم',
  'Value': 'القيمة',
};

export const glLeaveProvision = { ar, en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) };
