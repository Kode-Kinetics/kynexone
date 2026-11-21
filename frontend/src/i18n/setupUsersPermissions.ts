// Setup > Users and Setup > Permissions (views/UserManagementPage.tsx): the toolbar placeholders and the permission count line.
// "Details", "Collapse all", "Expand all", "Reload" and "Create User" are already defined elsewhere and reused as they are.
const ar: Record<string, string> = {
  'Search name or email…': 'ابحث بالاسم أو البريد الإلكتروني…',
  'Name / Email': 'الاسم / البريد الإلكتروني',
  'Last Login': 'آخر تسجيل دخول',
  'No users found.': 'لا يوجد مستخدمون.',
  'Filter permissions…': 'تصفية الصلاحيات…',
  '{count, plural, one {# permission} other {# permissions}} in {modules, plural, one {# module} other {# modules}}': '{count, plural, one {# صلاحية} other {# صلاحيات}} في {modules, plural, one {# وحدة} other {# وحدات}}',
};

export const setupUsersPermissions = { ar, en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) };
