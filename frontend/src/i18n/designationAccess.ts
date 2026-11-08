// Designation-driven access (Setup → Designations → Access roles). Terms follow the rest of Setup and User Management:
// المسمى الوظيفي، الدور، إدارة المستخدمين. English keys are the English text (see i18n/translations.ts).
const ar: Record<string, string> = {
  'Access roles': 'أدوار الوصول',
  'The existing roles people get when they are given this designation, and lose when they move to another one. Roles you give by hand are never removed. Administrators and roles that can manage security are not offered; give those from User Management.':
    'الأدوار الموجودة التي يحصل عليها الأشخاص عند إسناد هذا المسمى الوظيفي إليهم، ويفقدونها عند انتقالهم إلى مسمى آخر. أما الأدوار التي تمنحها يدويًا فلا تُسحب أبدًا. لا يُعرض دور المسؤول ولا الأدوار التي تتيح إدارة الأمان؛ تُمنح هذه الأدوار من إدارة المستخدمين.',
  'No roles can be given automatically.': 'لا توجد أدوار يمكن منحها تلقائيًا.',
};

export const designationAccess = {
  en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) as Record<string, string>,
  ar,
};
