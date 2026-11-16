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
};

export const heldPay = {
  en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) as Record<string, string>,
  ar,
};
