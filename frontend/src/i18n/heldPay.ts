/**
 * Held pay for suspended employees (components/payroll/HeldPayPanel.tsx) and the unpaid-leave arrears and reversal
 * findings a payroll run raises (lib/validationFindingText.ts). Terms follow GLOSSARY.md: مسير الرواتب, الاعتماد.
 */
const ar: Record<string, string> = {
  'Held pay could not be loaded.': 'تعذّر تحميل الأجور المحتجزة.',
  'The held pay was added to the next open run as an earning.': 'أُضيف الأجر المحتجز إلى مسير الرواتب المفتوح التالي كاستحقاق.',
  'The held pay was forfeited. The reason is in the audit trail.': 'أُسقط الأجر المحتجز. السبب مسجّل في سجل التدقيق.',
  'Released — paid in {period}': 'صُرف — يُدفع في {period}',
  'Released': 'صُرف',
  'Forfeited': 'أُسقط',
  'Held — awaiting a decision': 'محتجز — بانتظار قرار',
  '{count} held amount(s) for suspended employees': '{count} مبلغ محتجز لموظفين موقوفين',
  'A suspended employee’s pay is worked out and held, not paid and not lost. Finance pays it in the next open run or forfeits it with a reason.':
    'يُحتسب أجر الموظف الموقوف ويُحتجز، فلا يُصرف ولا يضيع. تصرفه المالية في مسير الرواتب المفتوح التالي أو تُسقطه مع ذكر السبب.',
  'Held in {period}': 'محتجز في {period}',
  'Pay in next run': 'الصرف في المسير التالي',
  'Forfeit': 'إسقاط',
  'Why is this pay forfeited? (required)': 'لماذا يُسقط هذا الأجر؟ (مطلوب)',
  'Forfeit held pay': 'إسقاط الأجر المحتجز',
  // Run findings (server codes HELD_PAY_RELEASE_NOT_PAID, LEAVE_ARREARS_CARRIED_FORWARD).
  'Held pay released into this run is not paid by it, because the employee is not in this run. It stays owed. Include the employee in the run and process it again.':
    'الأجر المحتجز الذي صُرف إلى هذا المسير لا يدفعه هذا المسير لأن الموظف ليس ضمنه. ويبقى مستحقًا. أدرج الموظف في المسير ثم عالجه من جديد.',
  'Unpaid-leave arrears from earlier months were not all collected this month, because deductions stay within the company’s share of the wage due. The rest carries to the next run; anything still owed when employment ends is shown on the final settlement.':
    'لم تُحصَّل كل متأخرات الإجازات غير المدفوعة من الأشهر السابقة هذا الشهر، لأن الخصومات تبقى ضمن النسبة التي حددتها الشركة من الأجر المستحق. ويُرحَّل الباقي إلى المسير التالي، وما يبقى مستحقًا عند انتهاء الخدمة يظهر في التسوية النهائية.',
  // Multi-month leave deductions report (views/LeaveSliceSplitReportPage.tsx), read-only.
  'The report could not be loaded.': 'تعذّر تحميل التقرير.',
  'Would be split': 'سيُقسَّم',
  'Still waiting for a payroll run. The amount would be shared across the months by days, adding up to the original exactly.':
    'لم يُعالَج بعد في مسير رواتب. سيُوزَّع المبلغ على الأشهر حسب الأيام، ومجموعه يساوي المبلغ الأصلي تمامًا.',
  'Do not reconcile': 'لا تتطابق',
  'The month-by-month days do not match the deduction, or the leave is no longer approved. HR checks these; they are never split automatically.':
    'أيام الأشهر لا تطابق الخصم، أو أن الإجازة لم تعد معتمدة. تراجعها الموارد البشرية، ولا تُقسَّم تلقائيًا أبدًا.',
  'Already deducted': 'خُصمت مسبقًا',
  'Taken in full by an earlier run in the first month. Left as they are: the total is right, only the timing differs.':
    'خُصمت كاملةً في مسير سابق في الشهر الأول. تُترك كما هي: المجموع صحيح، والاختلاف في التوقيت فقط.',
  'Sick-leave pay-scale rows': 'سجلات سلّم أجر الإجازة المرضية',
  'Their days are unpaid-equivalent days, so they are not split by days. HR reviews them.':
    'أيامها أيام مكافئة لغير المدفوعة، لذا لا تُقسَّم حسب الأيام. تراجعها الموارد البشرية.',
  'Multi-month leave deductions': 'خصومات الإجازات الممتدة لعدة أشهر',
  'Read-only. Nothing on this page changes a deduction; any change waits for the owner’s sign-off.':
    'للاطلاع فقط. لا شيء في هذه الصفحة يغيّر أي خصم؛ وأي تغيير ينتظر موافقة المالك.',
  'None.': 'لا يوجد.',
  '{days} day(s), {amount}, charged to {period}': '{days} يوم، {amount}، محمّلة على {period}',
  '{period}: {days} day(s), {amount}': '{period}: {days} يوم، {amount}',
};

export const heldPay = {
  en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) as Record<string, string>,
  ar,
};
