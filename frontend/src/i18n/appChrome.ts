/**
 * App chrome: sidebar and bottom-nav entries, the Kody launcher, the ⌘K palette and the shared CSV
 * import/export toolbar. Spread FIRST into each locale, so an existing app-wide entry for the same key wins.
 */
const ar: Record<string, string> = {
  // Navigation entries that had no Arabic.
  'Group Overview': 'نظرة على المجموعة',
  'Insights & Reports': 'التحليلات والتقارير',
  'Tax Policies': 'السياسات الضريبية',
  'GOSI Filing': 'ملفات التأمينات الاجتماعية',
  'Parallel Run Variance': 'فروقات التشغيل الموازي',
  'New Hires': 'الموظفون الجدد',
  'HR Letters': 'خطابات الموارد البشرية',
  'Payslip Templates': 'قوالب قسائم الرواتب',
  'Compliance Profiles': 'ملفات الامتثال',
  'Opening Balances': 'الأرصدة الافتتاحية',
  // Phone bottom navigation.
  'Home': 'الرئيسية',
  'Time': 'الوقت',

  // Kody launcher.
  'Open Kody, HR assistant': 'فتح Kody، مساعد الموارد البشرية',

  // ⌘K palette: a module is described by the access it needs, in words (lib/permissionAreas.ts).
  'Needs {areas} access': 'يتطلب صلاحية {areas}',
  'Open module': 'فتح الوحدة',
  'Team': 'الفريق',
  'Loans': 'القروض',
  'Contracts': 'العقود',
  'Compensation': 'التعويضات',
  'Reports': 'التقارير',
  'User management': 'إدارة المستخدمين',
  'Security': 'الأمان',
  'Organization setup': 'إعداد المنظمة',
  'Finance': 'المالية',
  'Search Employees': 'البحث عن الموظفين',
  'Jump to employee search and records': 'الانتقال إلى البحث عن الموظفين وسجلاتهم',
  'Review Approvals': 'مراجعة الاعتمادات',
  'Open approval center': 'فتح مركز الاعتمادات',
  'View Reports': 'عرض التقارير',
  'Open reports and analytics': 'فتح التقارير والتحليلات',
  'Search modules, employees, reports…': 'ابحث في الوحدات والموظفين والتقارير…',
  'Close search': 'إغلاق البحث',
  'Recent searches': 'عمليات البحث الأخيرة',
  'Clear all': 'مسح الكل',
  'Remove from history': 'إزالة من السجل',
  'Users': 'المستخدمون',
  'Searching across employees, users, and reports…': 'جارٍ البحث في الموظفين والمستخدمين والتقارير…',
  'No matches found. Try a module name like payroll, leave, or reports.': 'لا توجد نتائج. جرّب اسم وحدة مثل الرواتب أو الإجازات أو التقارير.',

  // CSV import/export toolbar (components/ImportExportToolbar.tsx).
  'Import CSV': 'استيراد CSV',
  'CSV import template': 'القالب',
  'Export': 'تصدير',
  'Importing…': 'جارٍ الاستيراد…',
  'Checking…': 'جارٍ التحقق…',
  'Exporting…': 'جارٍ التصدير…',
  'Export {entity} as CSV': 'تصدير {entity} بصيغة CSV',
  'Import {entity} from CSV': 'استيراد {entity} من ملف CSV',
  'Download a blank CSV import template': 'تنزيل قالب استيراد CSV فارغ',
  'The export failed. Try again.': 'تعذّر التصدير. حاول مرة أخرى.',
  'The template could not be downloaded. Try again.': 'تعذّر تنزيل القالب. حاول مرة أخرى.',
  'Import failed — {reason}': 'تعذّر الاستيراد — {reason}',
  'The import failed. Check the file and try again.': 'تعذّر الاستيراد. تحقّق من الملف وحاول مرة أخرى.',
  'This file was already imported — nothing was imported again.': 'سبق استيراد هذا الملف، ولم يُستورد أي شيء مرة أخرى.',
  'No answer from the server. Press Confirm import again — this file will not be imported twice.': 'لم يصل رد من الخادم. اضغط «تأكيد الاستيراد» مرة أخرى، ولن يُستورد هذا الملف مرتين.',
  'Review import — {entity}': 'مراجعة الاستيراد — {entity}',
  'Import results — {entity}': 'نتائج الاستيراد — {entity}',
  'Download preview': 'تنزيل المعاينة',
  'Confirm import': 'تأكيد الاستيراد',
  'Checking rows…': 'جارٍ التحقق من الصفوف…',
  'Lands as': 'الحالة بعد الاستيراد',
  'Why': 'السبب',
  '(no name)': '(بلا اسم)',
  'Refused': 'يلزم التصحيح',
  'Existing · fill missing': 'موجود · استكمال الناقص',
  'Existing employee — only missing details are filled in': 'موظف موجود — تُستكمل البيانات الناقصة فقط',
  'Inactive · needs info': 'غير نشط · يحتاج إلى بيانات',
  'Download report': 'تنزيل التقرير',
  'View the {count} incomplete': 'عرض غير المكتملين ({count})',
  // Entity names the toolbar's callers pass (entityName).
  'Cost Centers': 'مراكز التكلفة',
  'Leave Requests': 'طلبات الإجازة',
  'Job Openings': 'الوظائف الشاغرة',
  'Salary Structures': 'هياكل الرواتب',
  'BenefitClaim': 'مطالبات المزايا',
};

/** English is the key, except where one English word needs a context-specific key. */
const enOverrides: Record<string, string> = {
  'CSV import template': 'Template',
  'BenefitClaim': 'Benefit claims',
};

const en: Record<string, string> = Object.fromEntries(Object.keys(ar).map((key) => [key, enOverrides[key] ?? key]));

export const appChrome = { en, ar };
