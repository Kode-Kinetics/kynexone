// Payroll ↔ attendance: the run's attendance check before Process (Payroll → Validation, and the Process refusal).
// English keys are the English text (see i18n/translations.ts). Arabic follows the attendance and payroll usage HR
// teams read every day: بصمة (punch), خصم (deduction), تأخير (late arrival), انصراف مبكر (early exit).
const en: Record<string, string> = {
  'Deductions with no attendance record': 'Deductions with no attendance record',
  'Deductions do not match the day': 'Deductions do not match the day',
  'The punches for this day have changed': 'The punches for this day have changed',
  'A punch now belongs to another day': 'A punch now belongs to another day',
  '{count} attendance day(s) recomputed. Process the run again.': '{count} attendance day(s) recomputed. Process the run again.',
  '{count} attendance day(s) recomputed; {remaining} still differ (a locked period, or the day changed again). Review them before processing.': '{count} attendance day(s) recomputed; {remaining} still differ (a locked period, or the day changed again). Review them before processing.',
  'The days could not be recomputed. {reason}': 'The days could not be recomputed. {reason}',
  'The attendance check could not be loaded. {reason}': 'The attendance check could not be loaded. {reason}',
  '{days} attendance day(s) for {employees} employee(s) have deductions that no longer match their punches. This run cannot be processed until they are recomputed.': '{days} attendance day(s) for {employees} employee(s) have deductions that no longer match their punches. This run cannot be processed until they are recomputed.',
  'Recomputing…': 'Recomputing…',
  'Recompute these days': 'Recompute these days',
  'Each day is worked out again from its punches with today’s rules. Paid and locked days are never changed.': 'Each day is worked out again from its punches with today’s rules. Paid and locked days are never changed.',
  'Show the days': 'Show the days',
  'Why': 'Why',
  'Review attendance and recompute': 'Review attendance and recompute',
};

const ar: Record<string, string> = {
  'Deductions with no attendance record': 'خصومات بلا سجل حضور',
  'Deductions do not match the day': 'الخصومات لا تطابق اليوم',
  'The punches for this day have changed': 'تغيّرت بصمات هذا اليوم',
  'A punch now belongs to another day': 'بصمة أصبحت تتبع يومًا آخر',
  '{count} attendance day(s) recomputed. Process the run again.': 'أُعيد احتساب {count} من أيام الحضور. عالج المسير مرة أخرى.',
  '{count} attendance day(s) recomputed; {remaining} still differ (a locked period, or the day changed again). Review them before processing.': 'أُعيد احتساب {count} من أيام الحضور، وما زال {remaining} منها مختلفًا (فترة مقفلة، أو تغيّر اليوم مجددًا). راجعها قبل المعالجة.',
  'The days could not be recomputed. {reason}': 'تعذّرت إعادة احتساب الأيام. {reason}',
  'The attendance check could not be loaded. {reason}': 'تعذّر تحميل فحص الحضور. {reason}',
  '{days} attendance day(s) for {employees} employee(s) have deductions that no longer match their punches. This run cannot be processed until they are recomputed.': 'لدى {employees} من الموظفين {days} من أيام الحضور خصوماتها لم تعد تطابق بصماتها. لا يمكن معالجة هذا المسير حتى يُعاد احتسابها.',
  'Recomputing…': 'جارٍ إعادة الاحتساب…',
  'Recompute these days': 'أعد احتساب هذه الأيام',
  'Each day is worked out again from its punches with today’s rules. Paid and locked days are never changed.': 'يُحتسب كل يوم من جديد من بصماته وفق القواعد الحالية. لا تتغيّر الأيام المدفوعة أو المقفلة أبدًا.',
  'Show the days': 'عرض الأيام',
  'Why': 'السبب',
  'Review attendance and recompute': 'مراجعة الحضور وإعادة الاحتساب',
};

export const payrollAttendance = { en, ar };
