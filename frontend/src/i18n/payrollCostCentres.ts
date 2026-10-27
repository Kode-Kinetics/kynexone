// Cost accounting v1 (WP-F phase 3): the Payroll lock preview (GL Journal), the uncosted approval acknowledgement and the
// "Payroll journal by cost centre" report. Spelled "cost centre"; finance Arabic follows costCentres.ts and glSetup.ts
// (مركز التكلفة، قيد، مدين/دائن). English keys are the English text (see i18n/translations.ts). Arabic is draft until the
// owner or an Arabic reviewer approves it (gate G-RELEASE). Words already in the shared dictionary (Account, Debit, Credit,
// Description, Code, Why, Fix, Group by, Locked, Cancel, Close, Cost centre, Open Cost accounting) are reused, not redefined.
const ar: Record<string, string> = {
  // ── Run card and tab ──
  'Review & lock': 'مراجعة وقفل',
  'Review and lock': 'مراجعة وقفل',
  'Preview — not posted yet': 'معاينة — لم تُرحَّل بعد',
  'Total debits': 'إجمالي المدين',
  'Total credits': 'إجمالي الدائن',
  'Blocked: no cost centre': 'موظفون يمنعون القفل (بلا مركز تكلفة)',
  'Not costed (optional accounts)': 'موظفون بلا مركز تكلفة (حسابات اختيارية)',
  'Journal is balanced — Debits = Credits': 'القيد متوازن — المدين = الدائن',
  'Journal is out of balance — investigate before posting': 'القيد غير متوازن — تحقّق قبل الترحيل',

  // ── Blockers ──
  'Lock is blocked: {count, plural, one {# problem} other {# problems}}': 'يتعذّر قفل مسير الرواتب: {count, plural, one {مشكلة واحدة} two {مشكلتان} few {# مشكلات} many {# مشكلة} other {# مشكلة}}',
  'Re-check': 'إعادة الفحص',
  'Blocked employees': 'الموظفون الذين يمنعون القفل',
  'Fix employee': 'تعديل الموظف',
  'Open department': 'فتح القسم',
  'Open parent department': 'فتح القسم الأعلى',
  'Open position': 'فتح المنصب',
  'Set company default': 'تحديد المركز الافتراضي للشركة',
  '{action} {code}. Opens in a new tab': '{action} {code}. يفتح في علامة تبويب جديدة',
  '{action}. Opens in a new tab': '{action}. يفتح في علامة تبويب جديدة',
  '…and {n} more. See all in Cost accounting readiness.': '…والمزيد ({n}). اعرض الكل في جاهزية محاسبة التكلفة.',
  'Nothing set at any level': 'لم يُحدَّد أي مركز تكلفة على أي مستوى',
  "The employee's cost centre is inactive, deleted or in another company": 'مركز تكلفة الموظف غير نشط أو محذوف أو يتبع شركة أخرى',
  "The position's cost centre is inactive, deleted or in another company": 'مركز تكلفة المنصب غير نشط أو محذوف أو يتبع شركة أخرى',
  "The department's cost centre is inactive, deleted or in another company": 'مركز تكلفة القسم غير نشط أو محذوف أو يتبع شركة أخرى',
  "The parent department's cost centre is inactive, deleted or in another company": 'مركز تكلفة القسم الأعلى غير نشط أو محذوف أو يتبع شركة أخرى',
  "The company default's cost centre is inactive, deleted or in another company": 'مركز التكلفة الافتراضي للشركة غير نشط أو محذوف أو يتبع شركة أخرى',
  "The cost centre's functional class is reserved and cannot be used for posting yet": 'التصنيف الوظيفي لمركز التكلفة محجوز ولا يمكن استخدامه في الترحيل حاليًا',
  'A rounding difference must be posted to an account that requires a cost centre. Set a default cost centre for the company, then lock again.': 'يجب ترحيل فرق التقريب إلى حساب يتطلب مركز تكلفة. حدّد مركز تكلفة افتراضيًا للشركة ثم أعد قفل مسير الرواتب.',
  '{count, plural, one {# employee has no cost centre; their Optional-account lines post uncosted.} other {# employees have no cost centre; their Optional-account lines post uncosted.}}': '{count, plural, zero {لا يوجد موظفون بلا مركز تكلفة.} one {لموظف واحد لا يوجد مركز تكلفة؛ تُرحَّل أسطره على الحسابات الاختيارية بلا مركز تكلفة.} two {لموظفين اثنين لا يوجد مركز تكلفة؛ تُرحَّل أسطرهما على الحسابات الاختيارية بلا مركز تكلفة.} few {لدى # موظفين لا يوجد مركز تكلفة؛ تُرحَّل أسطرهم على الحسابات الاختيارية بلا مركز تكلفة.} many {لدى # موظفًا لا يوجد مركز تكلفة؛ تُرحَّل أسطرهم على الحسابات الاختيارية بلا مركز تكلفة.} other {لدى # موظف لا يوجد مركز تكلفة؛ تُرحَّل أسطرهم على الحسابات الاختيارية بلا مركز تكلفة.}}',
  '{count, plural, one {# employee falls back to the company default cost centre ({amount}).} other {# employees fall back to the company default cost centre ({amount}).}}': '{count, plural, zero {لا يوجد موظفون على المركز الافتراضي للشركة ({amount}).} one {موظف واحد يُحمَّل على مركز التكلفة الافتراضي للشركة ({amount}).} two {موظفان يُحمَّلان على مركز التكلفة الافتراضي للشركة ({amount}).} few {# موظفين يُحمَّلون على مركز التكلفة الافتراضي للشركة ({amount}).} many {# موظفًا يُحمَّلون على مركز التكلفة الافتراضي للشركة ({amount}).} other {# موظف يُحمَّلون على مركز التكلفة الافتراضي للشركة ({amount}).}}',

  // ── The table ──
  'Payroll journal grouped by cost centre': 'قيد الرواتب مجمّعًا حسب مركز التكلفة',
  'Payroll journal grouped by account': 'قيد الرواتب مجمّعًا حسب الحساب',
  'Missing cost centre': 'مركز التكلفة مفقود',
  'Not costed': 'بلا مركز تكلفة',
  'Not costed (balance-sheet lines)': 'بدون مركز تكلفة (أسطر الميزانية)',
  'Not recorded (run locked before cost centres)': 'غير مسجَّل (قُفل مسير الرواتب قبل مراكز التكلفة)',
  'Net {amount}': 'الصافي {amount}',
  'Search lines': 'بحث في الأسطر',
  'Expand all': 'توسيع الكل',
  'Collapse all': 'طي الكل',
  'Show more': 'عرض المزيد',
  'No journal lines match.': 'لا توجد أسطر مطابقة.',
  'This run has no journal lines.': 'لا توجد أسطر قيد لهذا المسير.',
  'This run has {n} lines. Showing up to {m} per group; search or group to narrow.': 'يحتوي هذا المسير على {n} سطرًا. يُعرض حتى {m} لكل مجموعة؛ استخدم البحث أو التجميع للتضييق.',
  'Cost accounting is off for this company. Lines post without cost centres.': 'محاسبة التكلفة متوقفة لهذه الشركة. تُرحَّل الأسطر بلا مراكز تكلفة.',

  // ── Lock ──
  'Lock run': 'قفل مسير الرواتب',
  'Locking…': 'جارٍ القفل…',
  'Fix the problems above to lock.': 'عالج المشكلات أعلاه لتتمكن من قفل المسير.',
  'Lock {period} for {company}? This posts {n} journal lines. A locked run cannot be edited; correct it by voiding.': 'قفل {period} لشركة {company}؟ سيُرحَّل {n} سطرًا إلى قيود اليومية. لا يمكن تعديل المسير المقفل؛ يتم التصحيح بإلغائه.',
  'Go to approvals': 'الانتقال إلى الاعتماد',
  'You do not have permission to lock payroll runs.': 'ليست لديك صلاحية قفل مسيرات الرواتب.',
  'Failed to lock the payroll run. Resolve any blocking validation errors first.': 'تعذّر قفل مسير الرواتب. عالج أخطاء التحقق المانعة أولًا.',

  // ── Approve: the uncosted acknowledgement ──
  '{count, plural, one {# employee posts lines on Optional accounts without a cost centre} other {# employees post lines on Optional accounts without a cost centre}}': '{count, plural, zero {لا يوجد موظفون تُرحَّل أسطرهم بلا مركز تكلفة} one {موظف واحد تُرحَّل أسطره على الحسابات الاختيارية بلا مركز تكلفة} two {موظفان تُرحَّل أسطرهما على الحسابات الاختيارية بلا مركز تكلفة} few {# موظفين تُرحَّل أسطرهم على الحسابات الاختيارية بلا مركز تكلفة} many {# موظفًا تُرحَّل أسطرهم على الحسابات الاختيارية بلا مركز تكلفة} other {# موظف تُرحَّل أسطرهم على الحسابات الاختيارية بلا مركز تكلفة}}',
  'Those lines will post uncosted. Review the cost-centre assignments first, then state the count to approve.': 'ستُرحَّل هذه الأسطر بلا مركز تكلفة. راجع تعيينات مراكز التكلفة أولًا ثم أكّد العدد للاعتماد.',
  'I have reviewed this and confirm that exactly {count} employees post uncosted.': 'راجعتُ ذلك وأؤكد أن عدد الموظفين الذين تُرحَّل أسطرهم بلا مركز تكلفة هو {count} بالضبط.',
  // ── Screen 7: Payroll journal by cost centre ──
  'Payroll journal by cost centre': 'قيد الرواتب حسب مركز التكلفة',
  'Headcount': 'عدد الموظفين',
  'Employer cost': 'تكلفة صاحب العمل',
  'Net': 'الصافي',
  'Journal net': 'صافي القيد',
  'Cleared against accrual': 'المسوّى مقابل المستحق',
  'Grand total': 'الإجمالي الكلي',
  'Run': 'المسير',
  'Select a payroll run…': 'اختر مسير رواتب…',
  'All periods': 'كل الفترات',
  'Preview — this run is not locked': 'معاينة — هذا المسير غير مقفل',
  'Reconciles with the journal.': 'يتطابق مع قيد اليومية.',
  'Does not reconcile with the journal.': 'لا يتطابق مع قيد اليومية.',
  'Difference: {amount}': 'الفرق: {amount}',
  'Reconciliation with the journal is available once the run is locked.': 'تتوفر مطابقة قيد اليومية بعد قفل المسير.',
  'No costed lines for this run.': 'لا توجد أسطر موزعة على مراكز التكلفة لهذا المسير.',
  'Includes cost centres rolled up to protect small groups.': 'يتضمن مراكز تكلفة جُمعت لحماية المجموعات الصغيرة.',
  'Unassigned (no cost centre)': 'بلا مركز تكلفة',
  'Show accounts for {cc}': 'عرض حسابات {cc}',
  'Amounts in {currency}.': 'المبالغ بعملة {currency}.',
};

export const payrollCostCentres = { ar, en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) };
