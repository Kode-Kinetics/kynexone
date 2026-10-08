/**
 * Strings of HR's selfie review on the Attendance page (views/AttendancePage.tsx raw punch log and
 * components/attendance/SelfieViewerModal.tsx). Spread into the en and ar dictionaries in translations.ts;
 * add keys here, never there. Arabic uses تسجيل الحضور for a punch, never بصمة (fingerprint).
 */
export const attendanceEvidence: { en: Record<string, string>; ar: Record<string, string> } = {
  en: {
    'Selfie': 'Selfie',
    'View selfie': 'View selfie',
    'View selfie for the punch by {employee} at {time}': 'View selfie for the punch by {employee} at {time}',
    'Selfie on the punch by {employee} at {time}': 'Selfie on the punch by {employee} at {time}',
    'Loading the selfie…': 'Loading the selfie…',
    'The selfie the employee took with this punch. Opening it is recorded in the audit log.': 'The selfie the employee took with this punch. Opening it is recorded in the audit log.',
    'There is no stored selfie for this punch. It may have been deleted under the retention rule.': 'There is no stored selfie for this punch. It may have been deleted under the retention rule.',
    'You cannot open this selfie. HR cannot review the selfie on their own punch, and opening one needs the selfie-review permission.': 'You cannot open this selfie. HR cannot review the selfie on their own punch, and opening one needs the selfie-review permission.',
    'The selfie could not be loaded. Close this window and try again.': 'The selfie could not be loaded. Close this window and try again.',
    'Selfie of the employee': 'Selfie of the employee',
  },
  ar: {
    'Selfie': 'الصورة الذاتية',
    'View selfie': 'عرض الصورة الذاتية',
    'View selfie for the punch by {employee} at {time}': 'عرض الصورة الذاتية المرفقة بتسجيل حضور {employee} في {time}',
    'Selfie on the punch by {employee} at {time}': 'الصورة الذاتية المرفقة بتسجيل حضور {employee} في {time}',
    'Loading the selfie…': 'جارٍ تحميل الصورة الذاتية…',
    'The selfie the employee took with this punch. Opening it is recorded in the audit log.': 'الصورة الذاتية التي التقطها الموظف مع تسجيل الحضور هذا. يُسجَّل فتحها في سجل التدقيق.',
    'There is no stored selfie for this punch. It may have been deleted under the retention rule.': 'لا توجد صورة ذاتية محفوظة لتسجيل الحضور هذا. ربما حُذفت وفق قاعدة الاحتفاظ.',
    'You cannot open this selfie. HR cannot review the selfie on their own punch, and opening one needs the selfie-review permission.': 'لا يمكنك فتح هذه الصورة. لا يراجع موظف الموارد البشرية الصورة المرفقة بتسجيل حضوره هو، ويتطلب فتحها صلاحية مراجعة الصور الذاتية.',
    'The selfie could not be loaded. Close this window and try again.': 'تعذّر تحميل الصورة الذاتية. أغلق هذه النافذة وحاول مرة أخرى.',
    'Selfie of the employee': 'الصورة الذاتية للموظف',
  },
};
