// Payroll → Bank / WPS Files and Payment Tracking, the UX waves: why something is missing (W2), the page-size controls (W4), the
// bank-file steps (W5), the confirm before a GL post (W6), names and the Arabic gaps (W7) and the Lock wording (W8). English keys
// are the English text (see i18n/translations.ts); Arabic follows glSetup.ts / payrollGlUi.ts terms and is draft until an Arabic
// reviewer approves it (gate G-RELEASE).
const ar: Record<string, string> = {
  // ── W2: why the bank-file panel is not there ──
  'Checking whether the bank file can be prepared…': 'جارٍ التحقق من إمكانية إعداد ملف البنك…',
  'You cannot prepare the bank file': 'لا يمكنك إعداد ملف البنك',
  'Preparing the Saudi bank file needs the Payroll export permission. Ask an administrator to give it to your role, then come back to this batch.': 'يتطلب إعداد ملف البنك السعودي صلاحية «تصدير الرواتب». اطلب من مسؤول النظام منحها لدورك ثم عُد إلى هذه الدفعة.',
  'Bank file export is switched off for this company': 'تصدير ملف البنك متوقف لهذه الشركة',
  'The platform team switches bank file export on for each company. Ask them to turn it on, then reload this batch.': 'يفعّل فريق المنصة تصدير ملف البنك لكل شركة على حدة. اطلب منهم تفعيله ثم أعد تحميل هذه الدفعة.',
  'Could not check whether the bank file can be prepared': 'تعذّر التحقق من إمكانية إعداد ملف البنك',
  'Choose a payroll run to see its payment batches.': 'اختر مسير رواتب لعرض دفعات الدفع الخاصة به.',
  'The payment records need the Payroll export permission. Ask an administrator to give it to your role.': 'تتطلب سجلات الدفع صلاحية «تصدير الرواتب». اطلب من مسؤول النظام منحها لدورك.',

  // ── W4: page size ──
  'Set up': 'الإعداد',
  'Pay out': 'الدفع',
  'Report and ledger': 'التقارير والدفاتر',
  'Settle and check': 'التسوية والفحص',
  'Hide stages': 'إخفاء المراحل',
  'Show stages': 'عرض المراحل',
  'Stage {stage}: {state}': 'المرحلة {stage}: {state}',
  'Payment records': 'سجلات الدفع',
  // ── W5: the bank file as four steps ──
  'Steps to a bank file': 'خطوات إعداد ملف البنك',
  'Choose the payment batch': 'اختيار دفعة الدفع',
  'Check the batch': 'التحقق من الدفعة',
  'Download and mark submitted': 'التنزيل وتسجيل الإرسال',
  'Lock the run first: a payment batch can only be created once the run is Locked.': 'اقفل المسير أولًا: لا يمكن إنشاء دفعة دفع إلا بعد أن يصبح المسير مقفلًا.',
  'Create a payment batch for this run, or open one from the list.': 'أنشئ دفعة دفع لهذا المسير أو افتح إحدى الدفعات من القائمة.',
  'Needs the Payroll export permission. Ask an administrator.': 'تتطلب صلاحية «تصدير الرواتب». اطلب ذلك من مسؤول النظام.',
  'Bank file export is switched off for this company. Ask the platform team.': 'تصدير ملف البنك متوقف لهذه الشركة. اطلب من فريق المنصة تفعيله.',
  'The availability check failed. Use Retry in the bank file section.': 'فشل التحقق من الإتاحة. استخدم «إعادة المحاولة» في قسم ملف البنك.',
  'The check found blockers. Fix them in the bank file section, then check again.': 'وجد التحقق عوائق. عالجها في قسم ملف البنك ثم تحقق مجددًا.',
  'Waits for a successful check.': 'بانتظار نجاح التحقق.',
  'Waits for the bank file.': 'بانتظار ملف البنك.',
  'Download the files, send them to your bank, then record the batch as submitted in Payment Tracking.': 'نزّل الملفات وأرسلها إلى بنكك ثم سجّل الدفعة «مُرسلة» في تتبع المدفوعات.',
  'Open Payment Tracking': 'فتح تتبع المدفوعات',
};

export const payrollUx = { ar, en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) };
