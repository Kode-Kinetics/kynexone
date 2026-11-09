/**
 * Attendance Live Dashboard, brought to the screen standard: the page purpose, the `i` on the punch fields, and the
 * empty states. Terms follow ./GLOSSARY.md (attendance = الحضور والانصراف) and ./attendanceBulk (punch = بصمة,
 * record = تسجيل). Native-speaker review is pending, as for the glossary.
 */
const ar: Record<string, string> = {
  'See who is in, late or missing on a day, and record the punches that are missing.':
    'اطّلع على من حضر ومن تأخر ومن لم تُسجَّل بصمته في يوم محدد، وسجّل البصمات الناقصة.',
  'Where this punch comes from: web, mobile or kiosk. It is kept with the record and shown as the Source in Raw Punch Logs.':
    'مصدر هذه البصمة: الويب أو الجوال أو الكشك. يُحفظ مع السجل ويظهر في عمود المصدر ضمن سجلات البصمة الخام.',
  'In, out, or the start or end of a break. Daily hours and late time are worked out from these punches when attendance is processed.':
    'دخول أو خروج أو بداية استراحة أو نهايتها. تُحتسب ساعات اليوم ودقائق التأخير من هذه البصمات عند معالجة الحضور والانصراف.',
  'The time of the in punch, in the company time zone. Leave it empty to record only an out time.':
    'وقت بصمة الدخول بتوقيت الشركة. اتركه فارغًا إذا أردت تسجيل بصمة الخروج فقط.',
  'The time of the out punch. If it is the same as or earlier than the in time, it is recorded on the next day.':
    'وقت بصمة الخروج. إذا كان مساويًا لوقت الدخول أو أبكر منه فيُسجَّل في اليوم التالي.',
  'Nothing recorded for this day yet': 'لا يوجد حضور وانصراف لهذا اليوم بعد',
  'Punches appear here once they are processed.': 'تظهر البصمات هنا بعد معالجتها.',
  'Change the date': 'تغيير التاريخ',
  'No records match': 'لا توجد سجلات مطابقة',
  'No attendance with status {status} on {date}.': 'لا يوجد حضور وانصراف بحالة {status} في {date}.',
  'No devices connected': 'لا توجد أجهزة متصلة',
  'Devices send punches automatically. Without one, punches are entered by hand.': 'ترسل الأجهزة البصمات تلقائيًا. وبدونها تُدخل البصمات يدويًا.',
  'Add a device': 'إضافة جهاز',
  'Devices are added by your HR administrator.': 'يضيف مسؤول الموارد البشرية الأجهزة.',
  'No exceptions on this day.': 'لا توجد استثناءات في هذا اليوم.',
  // Wave 1 follow-up: phone cards, KPI filters, the record panel's run chip.
  'Punch check': 'فحص البصمات',
  'Attendance records': 'سجلات الحضور والانصراف',
  '{minutes}m late': 'متأخر {minutes} د',
  '{count, plural, one {# result} other {# results}}':
    '{count, plural, zero {لا توجد نتائج} one {نتيجة واحدة} two {نتيجتان} few {# نتائج} many {# نتيجة} other {# نتيجة}}',
  'No attendance with a missing punch on {date}.': 'لا توجد سجلات ببصمة ناقصة في {date}.',
  'Recording {done} of {total}': 'جارٍ التسجيل: {done} من {total}',
};

// Round 2: result banner after a mass entry, refusal wording, no-punch rows, KPI filter hint.
Object.assign(ar, {
  'Review records': 'مراجعة السجلات',
  'Review {count, plural, one {# record} other {# records}}':
    '{count, plural, one {مراجعة سجل واحد} two {مراجعة سجلين} few {مراجعة # سجلات} many {مراجعة # سجلًا} other {مراجعة # سجل}}',
  'Process now': 'معالجة الآن',
  '{count, plural, one {# entry} other {# entries}} recorded for {date}':
    '{count, plural, one {تم تسجيل إدخال واحد لـ {date}} two {تم تسجيل إدخالين لـ {date}} few {تم تسجيل # إدخالات لـ {date}} many {تم تسجيل # إدخالًا لـ {date}} other {تم تسجيل # إدخال لـ {date}}}',
  'You are on the Live Dashboard. The records below update once the punches are processed.':
    'أنت في لوحة المتابعة المباشرة. تتحدّث السجلات أدناه بعد معالجة البصمات.',
  'The punch was not saved': 'لم تُحفظ البصمة',
  'Check the details and try again.': 'راجع البيانات ثم حاول مرة أخرى.',
  'Select a count to show only those records.': 'اختر رقمًا لعرض تلك السجلات فقط.',
  'Show only: {label}': 'عرض فقط: {label}',
  'The time of the in punch. Leave it empty to record only an out time.': 'وقت بصمة الدخول. اتركه فارغًا إذا أردت تسجيل بصمة الخروج فقط.',
});

export const attendanceDashboard = { ar, en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) as Record<string, string> };
