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
  'Loan settings': 'إعدادات القروض',
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

  // Pay & benefits (benefit plans panel, Benefits page empty state)
  'No benefit plans yet': 'لا توجد خطط مزايا بعد',
  'Plans are set up in Setup. Once one exists, employees can be enrolled here.': 'تُنشأ الخطط في الإعدادات. وبعد إنشاء خطة يمكن تسجيل الموظفين هنا.',
  'Set up benefit plans': 'إعداد خطط المزايا',

  // Talent & employee services and Payroll & compliance (HR letters hint, onboarding task generation)
  'Restore the bilingual defaults in Setup → Letter templates.': 'استعد القوالب الافتراضية ثنائية اللغة من الإعداد ← قوالب الخطابات.',
  'Ask an Admin or HR Manager to restore the bilingual defaults in Setup.': 'اطلب من المسؤول أو مدير الموارد البشرية استعادة القوالب الافتراضية ثنائية اللغة من الإعداد.',
  'Generate tasks from the selected checklist': 'إنشاء مهام من قائمة المهام المحددة',

  // ── Users & access, Workspace and Opening balances (components/setup/accessWorkspaceSetupPanels.tsx) ──────────────────
  // Sign-in security: the one editor of the password, lockout, session and two-step policy
  'Password policy': 'سياسة كلمة المرور',
  'Minimum length': 'الحد الأدنى للطول',
  'Fewest characters a password may have. 10-12+ recommended; applies to new passwords only.': 'أقل عدد من الأحرف تتكوّن منه كلمة المرور. يُنصح بـ 10 إلى 12 فأكثر، ويسري على كلمات المرور الجديدة فقط.',
  'Password expiry': 'انتهاء صلاحية كلمة المرور',
  '(days, 0 = never)': '(بالأيام، 0 = لا تنتهي)',
  'Days until staff must change their password. 0 turns off forced rotation.': 'عدد الأيام قبل أن يُطلب من الموظفين تغيير كلمة المرور. القيمة 0 تلغي التغيير الإلزامي.',
  'Password history count': 'عدد كلمات المرور السابقة المحفوظة',
  'How many previous passwords are remembered and blocked from reuse. 0 allows reuse.': 'عدد كلمات المرور السابقة التي تُحفظ ويُمنع إعادة استخدامها. القيمة 0 تسمح بإعادة الاستخدام.',
  'Require uppercase letter': 'اشتراط حرف كبير (A–Z)',
  'Require lowercase letter': 'اشتراط حرف صغير (a–z)',
  'Require digit': 'اشتراط رقم',
  'Require special character': 'اشتراط رمز خاص',
  'Wrong-password tries allowed before the account is temporarily locked. 5 is a common default.': 'عدد محاولات كلمة المرور الخاطئة المسموح بها قبل قفل الحساب مؤقتًا. القيمة 5 شائعة.',
  'How long a locked account stays locked before it can try again. Admins can unlock earlier from Users.': 'المدة التي يبقى فيها الحساب مقفلًا قبل أن يحاول صاحبه الدخول مجددًا. يمكن للمسؤولين فتحه مبكرًا من المستخدمين.',
  'Remembered sign-in': 'تذكّر تسجيل الدخول',
  '(days)': '(بالأيام)',
  'The longest a sign-in is remembered without asking for the password again. Shorter is safer, and means more sign-ins.': 'أطول مدة يُحفظ فيها تسجيل الدخول دون طلب كلمة المرور من جديد. المدة الأقصر أكثر أمانًا لكنها تعني تسجيل دخول أكثر تكرارًا.',
  'Allow multiple concurrent sessions': 'السماح بجلسات متزامنة متعددة',
  'Save Settings': 'حفظ الإعدادات',
  'Saved!': 'تم الحفظ',
  'The sign-in security settings could not be loaded.': 'تعذّر تحميل إعدادات أمان تسجيل الدخول.',
  'The sign-in security settings could not be saved. Please try again.': 'تعذّر حفظ إعدادات أمان تسجيل الدخول. حاول مرة أخرى.',
  'Trusted browser days must be between 0 (off) and 90.': 'يجب أن تتراوح مدة المتصفحات الموثوقة بين 0 (متوقف) و90 يومًا.',
  'Settings for the whole organisation can only be viewed and changed by an administrator with access to all companies.': 'لا يمكن عرض إعدادات المنشأة كلها أو تغييرها إلا من مسؤول لديه صلاحية الوصول إلى كل الشركات.',
  'Only an administrator with access to all companies can change settings for the whole organisation.': 'لا يغيّر إعدادات المنشأة كلها إلا مسؤول لديه صلاحية الوصول إلى كل الشركات.',
  // Plan and billing
  'Your subscription is inactive or has expired, so some actions are refused. Contact your account manager or support to renew it.': 'اشتراكك غير نشط أو منتهي، ولذلك تُرفض بعض الإجراءات. تواصل مع مدير حسابك أو الدعم لتجديده.',
  'The subscription could not be loaded.': 'تعذّر تحميل الاشتراك.',
  'The usage figures could not be loaded.': 'تعذّر تحميل أرقام الاستخدام.',
  'The invoices could not be loaded.': 'تعذّر تحميل الفواتير.',
  // Modules, language and region, branding
  'Could not load your modules. Refresh to try again.': 'تعذّر تحميل وحداتك. حدّث الصفحة وحاول مرة أخرى.',
  'The language and region settings could not be loaded.': 'تعذّر تحميل إعدادات اللغة والمنطقة.',
  'The settings could not be saved. Please try again.': 'تعذّر حفظ الإعدادات. حاول مرة أخرى.',
  'The branding could not be loaded.': 'تعذّر تحميل إعدادات الهوية.',
  'Save Branding': 'حفظ الهوية',
  'Configure Branding': 'إعداد الهوية',
  'No branding configured. System defaults are active.': 'لم تُعدّ الهوية بعد. تُستخدم الإعدادات الافتراضية للنظام.',
  // Country payroll keys, field help text
  'The country payroll keys could not be loaded.': 'تعذّر تحميل مفاتيح الرواتب للدول.',
  'The rule could not be deleted. Please try again.': 'تعذّر حذف القاعدة. حاول مرة أخرى.',
  'The rule could not be added. Please try again.': 'تعذّرت إضافة القاعدة. حاول مرة أخرى.',
  'Weekend days are also set in Setup → Country rules.': 'تُحدَّد أيام عطلة نهاية الأسبوع أيضًا في الإعداد ← قواعد الدول.',
  'Open Country rules': 'فتح قواعد الدول',
  'The custom help texts could not be loaded.': 'تعذّر تحميل نصوص المساعدة المخصصة.',
  // Links that used to name Tenant Admin
  'Workspace-wide display and calendar settings, in Setup': 'إعدادات العرض والتقويم لمساحة العمل كلها، في الإعداد',
  'Turn on the payroll module in Setup → Modules to track runs here.': 'فعّل وحدة الرواتب من الإعداد ← الوحدات لمتابعة المسيرات هنا.',
};

export const setupSections = {
  en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) as Record<string, string>,
  ar,
};
