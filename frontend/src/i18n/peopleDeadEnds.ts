/**
 * People dead ends (hotfix): who approves an employee transfer and where, and the older mobile profile change request.
 * Terms follow the glossary: موافقة (approve), الموارد البشرية (HR), المدير الحالي / المدير الجديد (current / new manager).
 */
const ar: Record<string, string> = {
  // Approval Center panel and the employee Transfers panel
  'Transfers waiting for you': 'عمليات النقل بانتظار قرارك',
  'Approve the step you hold. The transfer then moves to the next person, and is applied after HR approves it.':
    'وافق على الخطوة التي بين يديك. ثم ينتقل طلب النقل إلى الشخص التالي، ويُطبَّق بعد موافقة الموارد البشرية.',
  'Approve this transfer?': 'هل تريد الموافقة على طلب النقل هذا؟',
  '{employee} moves to {target}. You are approving as the current manager.': 'سيُنقل {employee} إلى {target}. أنت توافق بصفتك المدير الحالي.',
  '{employee} moves to {target}. You are approving as the new manager.': 'سيُنقل {employee} إلى {target}. أنت توافق بصفتك المدير الجديد.',
  '{employee} moves to {target}. You are approving as HR.': 'سيُنقل {employee} إلى {target}. أنت توافق بصفتك الموارد البشرية.',
  'Approve transfer': 'الموافقة على النقل',
  'Approved and applied to the employee record.': 'تمت الموافقة وطُبِّق النقل على سجل الموظف.',
  'Approved. The transfer moved on to the next step.': 'تمت الموافقة. انتقل طلب النقل إلى الخطوة التالية.',
  'Waiting for the current manager: {name}': 'بانتظار المدير الحالي: {name}',
  'Waiting for the current manager (HR can decide it)': 'بانتظار المدير الحالي (يمكن للموارد البشرية البتّ فيه)',
  'Waiting for the new manager: {name}': 'بانتظار المدير الجديد: {name}',
  'Waiting for the new manager (HR can decide it)': 'بانتظار المدير الجديد (يمكن للموارد البشرية البتّ فيه)',
  'Waiting for HR approval': 'بانتظار موافقة الموارد البشرية',
  'Applied': 'مُطبَّق',
  'Transfer request': 'طلب نقل',
  '{from} to {to}, from {date}': 'من {from} إلى {to}، اعتبارًا من {date}',
  'Employment transfers': 'عمليات نقل الموظف',
  'No transfer requests are recorded.': 'لا توجد طلبات نقل مسجّلة.',

  // HR review inbox: the older mobile request
  'Personal details from the mobile app': 'بيانات شخصية من تطبيق الجوال',
  'ess.profileChange.field.preferredName': 'الاسم المفضّل',
  'ess.profileChange.field.personalEmail': 'البريد الإلكتروني الشخصي',
  'ess.profileChange.field.phone': 'رقم الجوال',
  'ess.profileChange.field.maritalStatus': 'الحالة الاجتماعية',
  'ess.profileChange.field.emergencyContactName': 'اسم جهة الاتصال في الطوارئ',
  'ess.profileChange.field.emergencyContactPhone': 'رقم جهة الاتصال في الطوارئ',

  // Employee: My requests
  'Changes to my details': 'التغييرات على بياناتي',
  'Personal details': 'البيانات الشخصية',
  'Sent on {date}': 'أُرسل في {date}',
  'HR note': 'ملاحظة الموارد البشرية',
  'Waiting for HR': 'بانتظار الموارد البشرية',
  'Waiting for Finance': 'بانتظار المالية',
  'Approved and applied': 'تمت الموافقة والتطبيق',
  'Sent back by HR': 'أعادها قسم الموارد البشرية',
};

const enOnly: Record<string, string> = {
  'ess.profileChange.field.preferredName': 'Preferred name',
  'ess.profileChange.field.personalEmail': 'Personal email',
  'ess.profileChange.field.phone': 'Phone',
  'ess.profileChange.field.maritalStatus': 'Marital status',
  'ess.profileChange.field.emergencyContactName': 'Emergency contact name',
  'ess.profileChange.field.emergencyContactPhone': 'Emergency contact phone',
};

export const peopleDeadEnds = {
  en: { ...Object.fromEntries(Object.keys(ar).map(key => [key, key])), ...enOnly } as Record<string, string>,
  ar,
};
