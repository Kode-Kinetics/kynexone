/**
 * People list and the shared shell around it (sidebar footer, top bar, two-step banner): sentences that were
 * English under Arabic. Spread FIRST into each locale (next to appChrome), so an existing app-wide entry for the
 * same key wins and this file only fills the gaps. Terms follow GLOSSARY.md (القسم, المسمى الوظيفي, الحالة).
 */
const ar: Record<string, string> = {
  // Page header and views
  'Loading employee records…': 'جارٍ تحميل سجلات الموظفين…',
  'Employee records unavailable': 'سجلات الموظفين غير متاحة',
  '{count} employee records': 'عدد سجلات الموظفين: {count}',
  'Former employees — retained for statutory audit': 'الموظفون السابقون — تُحفظ سجلاتهم لأغراض التدقيق النظامي',
  'Employee directory view': 'عرض دليل الموظفين',
  'Current': 'الحاليون',
  'Ex-Employees': 'الموظفون السابقون',
  'Add Employee': 'إضافة موظف',

  // Load and action failures shown in the list's alert
  'Could not load employees from the API.': 'تعذّر تحميل الموظفين من الخادم.',
  'Could not load employee detail from the API.': 'تعذّر تحميل بيانات الموظف من الخادم.',
  'Could not load organization setup data.': 'تعذّر تحميل بيانات إعداد المنشأة.',
  'Could not export the selected employees.': 'تعذّر تصدير الموظفين المحددين.',
  'The bulk action could not be completed.': 'تعذّر إتمام الإجراء الجماعي.',
  'Could not change employee status.': 'تعذّر تغيير حالة الموظف.',
  'Could not activate employee.': 'تعذّر تفعيل الموظف.',
  'Could not submit the transfer request.': 'تعذّر إرسال طلب النقل.',

  // Filters
  'Dismiss': 'إغلاق',
  'Search employees': 'البحث عن موظفين',
  'Search employee code, name, email': 'ابحث برمز الموظف أو الاسم أو البريد الإلكتروني',
  'Status filter': 'تصفية حسب الحالة',
  'All statuses': 'كل الحالات',
  'Readiness filter': 'تصفية حسب الجاهزية',
  'All readiness': 'كل مستويات الجاهزية',
  'Needs info': 'بيانات ناقصة',
  'Blocked only': 'غير المكتملة فقط',
  'Needs attention': 'يحتاج إلى متابعة',
  'Ready': 'جاهز',
  'Refresh': 'تحديث',
  'Clear': 'مسح',

  // Selection bar
  '{count} selected': 'تم تحديد {count}',
  '{count} selected (all matching this filter)': 'تم تحديد {count} (كل ما يطابق هذه التصفية)',
  'Select all {total} matching this filter': 'تحديد كل الموظفين المطابقين لهذه التصفية ({total})',
  'Clear selection': 'إلغاء التحديد',
  'Activate': 'تفعيل',
  'Deactivate': 'إلغاء التفعيل',
  'Export selected': 'تصدير المحدد',
  'Delete': 'حذف',
  'Dismiss result': 'إغلاق النتيجة',

  // Table
  'Select all on this page': 'تحديد كل ما في هذه الصفحة',
  'Select {name}': 'تحديد {name}',
  'Employee': 'الموظف',
  'Department': 'القسم',
  'Designation': 'المسمى الوظيفي',
  'Branch': 'الفرع',
  'Status': 'الحالة',
  'Profile': 'الملف الشخصي',
  'Loading live employees...': 'جارٍ تحميل الموظفين…',
  'No employees match this filter.': 'لا يوجد موظفون يطابقون هذه التصفية.',
  'No employees yet': 'لا يوجد موظفون بعد',
  'Adjust or clear the filters to see other records.': 'عدّل التصفية أو امسحها لعرض سجلات أخرى.',
  'Create the first employee to begin onboarding.': 'أضف أول موظف لبدء إجراءات الالتحاق.',
  'Profile {pct}% complete — open the activation checklist': 'اكتمال الملف الشخصي {pct}% — افتح قائمة التفعيل',
  'Page {page} of {total}': 'صفحة {page} من {total}',
  'Prev': 'السابق',
  'Next': 'التالي',

  // Employment statuses shown in the status column and filter
  'Pre-boarding': 'قبل الالتحاق',
  'Probation': 'تحت التجربة',
  'Confirmed': 'مثبَّت',
  'Suspended': 'موقوف',
  'Resigned': 'مستقيل',
  'Notice period': 'فترة الإشعار',
  'Absconded': 'منقطع عن العمل',
  'Inactive': 'غير نشط',
  'Blacklisted': 'مدرج في القائمة السوداء',
  'Terminated': 'منتهية خدمته',
  'Retired': 'متقاعد',

  // Profile readiness badge
  'Incomplete': 'غير مكتمل',
  'Incomplete · {count}': 'غير مكتمل · {count}',
  'Needs attention · {count}': 'يحتاج إلى متابعة · {count}',
  'Cannot be activated yet — open the checklist to see what is missing': 'لا يمكن التفعيل بعد — افتح قائمة التحقق لمعرفة ما ينقص',
  'Can be activated, but items are still outstanding — open the checklist': 'يمكن التفعيل، لكن بعض البنود لا تزال معلقة — افتح قائمة التحقق',
  'Renewal due': 'التجديد مستحق',
  'A required ID or document is expiring soon or has expired': 'هوية أو مستند مطلوب قارب على الانتهاء أو انتهت صلاحيته',
  'Nothing outstanding': 'لا توجد بنود معلقة',
  'Not evaluated': 'لم يُقيَّم',
  'Readiness could not be determined for this employee — open the checklist': 'تعذّر تحديد جاهزية هذا الموظف — افتح قائمة التحقق',

  // Shell: logo caption and the role under the signed-in name
  'Workforce Platform': 'منصة إدارة القوى العاملة',
  'Admin': 'مدير النظام',
  'Manager': 'مدير',
  'Payroll Manager': 'مدير الرواتب',
  'Finance Approver': 'معتمد مالي',
  'Compliance Officer': 'مسؤول الامتثال',
  'Member': 'عضو',

  // Two-step sign-in banner
  'Two-step sign-in': 'التحقق بخطوتين',
  'Two-step sign-in is required for your role': 'التحقق بخطوتين مطلوب لدورك الوظيفي',
  'From {date} you will need a code from an authenticator app to sign in.': 'اعتبارًا من {date} ستحتاج إلى رمز من تطبيق المصادقة لتسجيل الدخول.',
  'Soon you will need a code from an authenticator app to sign in.': 'ستحتاج قريبًا إلى رمز من تطبيق المصادقة لتسجيل الدخول.',
  'Setting it up takes about a minute; you will sign in again afterwards.': 'يستغرق الإعداد نحو دقيقة، وستسجّل الدخول من جديد بعده.',
  'Remind me later': 'ذكّرني لاحقًا',
  'Could not start setup. Please try again.': 'تعذّر بدء الإعداد. حاول مرة أخرى.',
};

export const peopleList = {
  en: Object.fromEntries(Object.keys(ar).map((key) => [key, key])) as Record<string, string>,
  ar,
};
