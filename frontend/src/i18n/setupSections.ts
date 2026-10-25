/**
 * Setup as the one home for configuration (src/lib/setupModuleSections.ts): the modules' settings sections, their
 * navigation groups and descriptions. Terms follow the modules they came from: الإجازات، الورديات، العمل الإضافي، السلف،
 * المزايا، المستخدمون، الصلاحيات، الإعداد.
 */
const ar: Record<string, string> = {
  // Navigation groups
  'Time & leave': 'الوقت والإجازات',
  'Talent & employee services': 'المواهب وخدمات الموظفين',
  'Users & access': 'المستخدمون والوصول',
  'Every setting in one place: company structure, leave, pay, users and access, and the workspace.': 'كل الإعدادات في مكان واحد: هيكل الشركة والإجازات والرواتب والمستخدمون والوصول ومساحة العمل.',

  // Each module's door to its settings in Setup (components/setup/ModuleSettingsLink.tsx)
  'Leave settings': 'إعدادات الإجازات',
  'Shift settings': 'إعدادات الورديات',
  'Overtime settings': 'إعدادات العمل الإضافي',
  'Attendance settings': 'إعدادات الحضور',
  'Benefit settings': 'إعدادات المزايا',
  'Loan settings': 'إعدادات القروض والسلف',
  'Performance settings': 'إعدادات الأداء',
  'Letter settings': 'إعدادات الخطابات',
  'Engagement settings': 'إعدادات الاستبيانات',
  'Saudi compliance settings': 'إعدادات الامتثال السعودي',
  'Onboarding settings': 'إعدادات تهيئة الموظفين',

  // Get started
  'Opening balances': 'الأرصدة الافتتاحية',
  'Load leave, loan and end-of-service balances when going live.': 'حمّل أرصدة الإجازات والقروض ومكافأة نهاية الخدمة عند بدء التشغيل.',

  // Time & leave
  'The kinds of leave people can take, with their entitlement and pay rules.': 'أنواع الإجازات المتاحة للموظفين، مع استحقاقها وقواعد صرف أجرها.',
  'Who gets which leave, accrual, carry-forward and encashment.': 'من يستحق كل إجازة، والاستحقاق التراكمي والترحيل والصرف النقدي.',
  'Public holidays for each country and company.': 'العطلات الرسمية لكل دولة وشركة.',
  'Shifts': 'الورديات',
  'Shift times, breaks and grace periods.': 'أوقات الورديات وفترات الاستراحة وفترات السماح.',
  'Shift policy': 'سياسة الورديات',
  'Rotation, rest-day and roster rules.': 'قواعد التناوب وأيام الراحة وجداول المناوبات.',
  'Overtime policies': 'سياسات العمل الإضافي',
  'Overtime rates, caps and approval rules.': 'معدلات العمل الإضافي وحدوده وقواعد اعتماده.',
  'Attendance devices': 'أجهزة الحضور',
  'Biometric devices and other punch sources, and their connection keys.': 'أجهزة البصمة ومصادر تسجيل الحضور الأخرى، ومفاتيح ربطها.',

  // Pay & benefits
  'Benefit plans': 'خطط المزايا',
  'The benefits on offer and their eligibility rules.': 'المزايا المتاحة وقواعد الأهلية لها.',
  'Loan types': 'أنواع القروض',
  'The kinds of loan employees can request.': 'أنواع القروض التي يمكن للموظفين طلبها.',
  'Loan policies': 'سياسات القروض',
  'Loan limits by grade, instalments and eligibility.': 'حدود القروض حسب الدرجة، والأقساط والأهلية.',
  'Salary advance policy': 'سياسة السلف على الراتب',
  'How much can be advanced, how often, and how it is repaid.': 'الحد الأقصى للسلفة، وعدد مرات طلبها، وطريقة سدادها.',
  'Bonus types': 'أنواع المكافآت',
  'The kinds of bonus paid through payroll.': 'أنواع المكافآت التي تُصرف عبر الرواتب.',
  'The layout of the payslips employees receive.': 'تصميم كشوف الرواتب التي يتسلمها الموظفون.',

  // Talent & employee services
  'The tasks a new joiner and their team complete, as reusable checklists.': 'المهام التي ينجزها الموظف الجديد وفريقه، في قوائم قابلة لإعادة الاستخدام.',
  'Review scorecards': 'نماذج التقييم',
  'The review forms and rating scales used in performance cycles.': 'نماذج المراجعة ومقاييس التقييم المستخدمة في دورات الأداء.',
  'Letter templates': 'قوالب الخطابات',
  'Salary certificates, experience letters and other HR letters.': 'شهادات الراتب وخطابات الخبرة وغيرها من خطابات الموارد البشرية.',
  'Survey privacy': 'خصوصية الاستبيانات',
  'The smallest group whose survey results can be shown.': 'أصغر مجموعة يمكن عرض نتائج استبيانها.',

  // Payroll & compliance
  'Tax and contribution rules applied during payroll.': 'قواعد الضرائب والاشتراكات المطبقة عند احتساب الرواتب.',
  'Country and company rules that compliance checks run against.': 'قواعد الدولة والشركة التي تُجرى عليها فحوصات الامتثال.',
  'Qiwa, GOSI, Mudad and WPS details for Saudi companies.': 'بيانات قوى والتأمينات الاجتماعية ومدد ونظام حماية الأجور للشركات السعودية.',
  'Country payroll keys': 'قيم الرواتب الخاصة بكل دولة',
  'Extra per-country payroll values, such as weekend days.': 'قيم إضافية للرواتب لكل دولة، مثل أيام عطلة نهاية الأسبوع.',

  // Users & access
  'Who can sign in, their roles and companies.': 'من يمكنه تسجيل الدخول، وأدواره وشركاته.',
  'Roles': 'الأدوار',
  'Roles and what each one may do.': 'الأدوار وما يحق لكل منها فعله.',
  'Permission matrix': 'مصفوفة الصلاحيات',
  'Every role against every permission, on one grid.': 'كل دور مقابل كل صلاحية، في جدول واحد.',
  'Permissions': 'الصلاحيات',
  'The catalogue of permissions the system knows.': 'قائمة الصلاحيات المعرّفة في النظام.',
  'Permission grantors': 'مانحو الصلاحيات',
  'Who may grant which permissions to others.': 'من يحق له منح كل صلاحية للآخرين.',
  'Approval authorities': 'صلاحيات الاعتماد',
  'Who approves what, and up to which amount.': 'من يعتمد ماذا، وحتى أي مبلغ.',
  'Approval delegations': 'تفويضات الاعتماد',
  'Approvers standing in for others while they are away.': 'المعتمدون الذين ينوبون عن غيرهم أثناء غيابهم.',
  'Passwords, two-step verification and session limits.': 'كلمات المرور والتحقق بخطوتين وحدود الجلسات.',
  'Sign-in and access log': 'سجل تسجيل الدخول والوصول',
  'Sign-ins, failed attempts and access changes.': 'عمليات تسجيل الدخول والمحاولات الفاشلة وتغييرات الوصول.',

  // Workspace
  'Language and region': 'اللغة والمنطقة',
  'Languages, calendar, date and number formats, and currency.': 'اللغات والتقويم وتنسيقات التاريخ والأرقام والعملة.',
  'Branding': 'الهوية',
  'Your logo, names and portal title.': 'شعارك وأسماؤك وعنوان البوابة.',
  'Field help text': 'نصوص مساعدة الحقول',
  'The hints shown beside form fields.': 'التلميحات المعروضة بجانب حقول النماذج.',
  'Modules': 'الوحدات',
  'Switch modules on or off for your organisation.': 'شغّل الوحدات أو أوقفها لمنشأتك.',
  'Plan and billing': 'الباقة والفواتير',
  'Your subscription, usage and invoices.': 'اشتراكك واستخدامك وفواتيرك.',
};

export const setupSections = {
  en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) as Record<string, string>,
  ar,
};
