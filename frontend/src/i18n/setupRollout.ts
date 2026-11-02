/**
 * Setup rollout of the one add/remove rule to the rest of Setup (leave, loans, templates, roles, users...).
 * Same vocabulary as orgRowRemoval: never used = deleted, used = turned off ("متوقف"), restorable ("استعادة").
 */
const ar: Record<string, string> = {
  'Profiles for this company': 'ملفات هذه الشركة',
  'No profiles to show.': 'لا توجد ملفات للعرض.',
  // Country rules section (two tabs) and the Setup taxonomy
  'country rule': 'قاعدة الدولة',
  'Statutory settings': 'الإعدادات النظامية',
  'Payroll keys': 'مفاتيح الرواتب',
  'Per country: statutory settings (work week, WPS/SIF, end of service, document alerts) and payroll keys such as weekend days.': 'لكل دولة: الإعدادات النظامية (أسبوع العمل، حماية الأجور/SIF، نهاية الخدمة، تنبيهات المستندات) ومفاتيح الرواتب مثل أيام عطلة نهاية الأسبوع.',
  'This is the default template. Set another template as default to remove it.': 'هذا هو القالب الافتراضي. اجعل قالباً آخر هو الافتراضي لتتمكن من إزالته.',
  // Leave types
  'leave type': 'نوع الإجازة',
  'Edit leave type': 'تعديل نوع الإجازة',
  'Save changes': 'حفظ التغييرات',
  'The code is fixed once a leave type is created, because balances and requests refer to it.': 'يتثبّت الرمز بعد إنشاء نوع الإجازة لأن الأرصدة والطلبات تشير إليه.',
  'This is a statutory leave type. Turning it off does not change what the law requires: statutory floors still apply to everyone who holds it.': 'هذا نوع إجازة نظامي. إيقافه لا يغيّر ما يوجبه النظام: تظل الحدود النظامية سارية على كل من يملكه.',

  // Loan types
  'loan type': 'نوع القرض',
  'Edit loan type': 'تعديل نوع القرض',

  'Show turned-off': 'عرض الموقوف',
  'bonus type': 'نوع المكافأة',
  'payslip template': 'قالب قسيمة الراتب',
  'notification template': 'قالب الإشعار',
  // Placement
  'You cannot open {section}, so this is the first section available to you.': 'لا يمكنك فتح {section}، لذا هذا أول قسم متاح لك.',
  'Looking for sign-ins, failed attempts or access changes?': 'تبحث عن عمليات الدخول أو المحاولات الفاشلة أو تغييرات الصلاحيات؟',
  'Looking for who changed a setting?': 'تبحث عمّن غيّر إعدادًا؟',
  // Capabilities
  'Delegations are not available yet.': 'التفويضات غير متاحة بعد.',
  'Nothing in the approval flow reads a delegation today, so a new one could not take effect. Existing delegations are listed below and can be cancelled. To cover an absence, change the approver on the approval workflow\'s step.': 'لا يقرأ مسار الاعتماد أي تفويض حاليًا، لذا لن يسري تفويض جديد. التفويضات القائمة مدرجة أدناه ويمكن إلغاؤها. لتغطية غياب، غيّر المعتمِد في خطوة مسار الاعتماد.',
  'Approval limits are not available yet.': 'حدود الاعتماد غير متاحة بعد.',
  'An amount limit has nothing to be compared against today, so a new one could not be enforced. Existing entries are listed below. For approval seniority, use ordered steps on the approval workflow.': 'لا يوجد ما تُقارَن به حدود المبالغ حاليًا، لذا لا يمكن تطبيق حد جديد. الإدخالات القائمة مدرجة أدناه. للتدرج في الاعتماد استخدم خطوات مرتبة في مسار الاعتماد.',
  'shift': 'الوردية',
  'onboarding checklist': 'قائمة التهيئة',
  'compliance profile': 'ملف الامتثال',
  // Roles and users
  'Role holders will be signed out: {count} hold {name}': '{count, plural, one {شخص واحد يملك {name} وسيُسجَّل خروجه؛ يعود بدونه.} two {شخصان يملكان {name} وسيُسجَّل خروجهما؛ يعودان بدونه.} few {# أشخاص يملكون {name} وسيُسجَّل خروجهم؛ يعودون بدونه.} many {# شخصًا يملكون {name} وسيُسجَّل خروجهم؛ يعودون بدونه.} other {# شخص يملكون {name} وسيُسجَّل خروجهم؛ يعودون بدونه.}}',
  'Role still held: {count} hold {name}': '{count, plural, one {شخص واحد يملك {name}.} two {شخصان يملكان {name}.} few {# أشخاص يملكون {name}.} many {# شخصًا يملكون {name}.} other {# شخص يملكون {name}.}} أعد تعيينهم إلى دور آخر أولًا ثم أزل هذا الدور.',
  'Turn off role': 'إيقاف الدور',
  'Nobody holds {name} right now. Turn it off?': 'لا أحد يملك {name} حاليًا. هل تريد إيقافه؟',
  'The role could not be changed.': 'تعذّر تغيير الدور.',
  'Remove role': 'إزالة الدور',
  'Remove {name}? Only a role nobody holds can be removed. If people hold it, you are told how many and can reassign them first.': 'هل تريد إزالة {name}؟ لا يُزال إلا دور لا يملكه أحد. إن كان أشخاص يملكونه فسنخبرك بعددهم ويمكنك إعادة تعيينهم أولًا.',
  'The role could not be removed.': 'تعذّرت إزالة الدور.',
  'Open Users': 'فتح المستخدمين',
  'Built-in role: it cannot be turned off or removed.': 'دور مدمج: لا يمكن إيقافه أو إزالته.',
  'Deleted (restorable)': 'محذوف (يمكن استعادته)',
  'You cannot delete your own account.': 'لا يمكنك حذف حسابك.',
  'This is the last active administrator. Make someone else an administrator first.': 'هذا آخر مسؤول نشط. عيّن مسؤولًا آخر أولًا.',
  'Delete user': 'حذف المستخدم',
  'The account could not be restored.': 'تعذّرت استعادة الحساب.',
};

/** Messages whose English is a plural pattern: the key stays a readable sentence, the value carries the grammar. */
const enPlural: Record<string, string> = {
  'Role holders will be signed out: {count} hold {name}': '{count, plural, one {# person holds {name} and will be signed out; they sign back in without it.} other {# people hold {name} and will be signed out; they sign back in without it.}}',
  'Role still held: {count} hold {name}': '{count, plural, one {# person holds {name}.} other {# people hold {name}.}} Reassign them to another role first, then remove it.',
};

export const setupRollout = {
  en: { ...(Object.fromEntries(Object.keys(ar).map(key => [key, key])) as Record<string, string>), ...enPlural },
  ar,
};
