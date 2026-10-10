// Where a notification leads. The ESS notification rows carry only a title and body (no entity
// link), and the titles come from tenant-editable templates in English or Arabic. This reads the
// title conservatively: a match opens the matching list; no match means the row is not presented
// as something that opens. Pure, tested in tests/notificationTarget.test.ts. Owned by lane A1.

export type NotificationTarget =
  | { route: 'ApplyLeave'; params: { segment: 'history' } }
  | { route: 'Payslips' }
  | { route: 'Overtime' }
  | { route: 'HRRequests' }
  | { route: 'Letters' }
  | { route: 'Documents' }
  | { route: 'AttendanceHistory' }
  | { route: 'Profile' };

const RULES: { test: RegExp; target: NotificationTarget }[] = [
  { test: /payslip|salary slip|قسيمة|كشف الراتب/i, target: { route: 'Payslips' } },
  { test: /overtime|عمل إضافي|العمل الإضافي|ساعات إضافية/i, target: { route: 'Overtime' } },
  { test: /\bleave\b|إجازة|اجازة/i, target: { route: 'ApplyLeave', params: { segment: 'history' } } },
  { test: /letter|certificate|your document is ready|document request|خطاب|شهادة|تعريف/i, target: { route: 'Letters' } },
  { test: /hr (replied|request)|request.*hr|exit\/re-entry|طلب.*الموارد البشرية|الموارد البشرية.*طلب|خروج وعودة/i, target: { route: 'HRRequests' } },
  { test: /employee details|your profile|بياناتك|ملفك/i, target: { route: 'Profile' } },
  { test: /punch|attendance|clock|بصمة|الحضور|حضور/i, target: { route: 'AttendanceHistory' } },
  { test: /document|iqama|passport|expir|مستند|إقامة|جواز|انتهاء/i, target: { route: 'Documents' } },
];

export function notificationTarget(title?: string | null, body?: string | null): NotificationTarget | null {
  const subject = (title ?? '').trim();
  if (!subject) return null;
  for (const rule of RULES) if (rule.test.test(subject)) return rule.target;
  // The body only decides when the title is generic (e.g. "Update").
  const text = (body ?? '').trim();
  if (!text) return null;
  for (const rule of RULES) if (rule.test.test(text)) return rule.target;
  return null;
}
