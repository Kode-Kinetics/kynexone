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
  // ── W6: what a GL posting will do, before it does it ──
  'Payment batch': 'دفعة الدفع',
  'Journal': 'القيد',
  'Remittance date': 'تاريخ السداد',
  'GOSI, tax and loan remittances': 'سداد التأمينات والضريبة وأقساط القروض',
  'Post the net pay settlement to the general ledger?': 'ترحيل تسوية صافي الرواتب إلى دفتر الأستاذ؟',
  'Post the statutory remittance to the general ledger?': 'ترحيل السداد النظامي إلى دفتر الأستاذ؟',
  'This posts a journal to the general ledger. It is reversed with a contra journal, not deleted.': 'يرحّل هذا الإجراء قيدًا إلى دفتر الأستاذ. ويُعكس بقيد عكسي ولا يُحذف.',
  'This posts a journal to the general ledger. It is reversed with a contra journal, not deleted. The amounts come from the run and appear on the GL Journal tab once posted.': 'يرحّل هذا الإجراء قيدًا إلى دفتر الأستاذ. ويُعكس بقيد عكسي ولا يُحذف. تُحتسب المبالغ من المسير وتظهر في تبويب قيود دفتر الأستاذ بعد الترحيل.',
  'Settle net pay to GL': 'تسوية صافي الرواتب في دفتر الأستاذ',
  'Post settlement': 'ترحيل التسوية',
  'Post remittance': 'ترحيل السداد',

  // ── W7: names and the English that was left ──
  '{name} — a valid IBAN is on file': '{name} — لديه آيبان صالح مسجّل',
  'They will be left out of the bank file and their wages must be paid and recorded separately.': 'سيُستبعدون من ملف البنك ويجب دفع أجورهم وتسجيلها بشكل منفصل.',
  'Choose GOSI, TAX, or LOAN before reversing a remittance.': 'اختر التأمينات أو الضريبة أو القروض قبل عكس السداد.',

  // ── W8: Lock publishes the payslips ──
  'Locking also publishes the payslips to the employees: each payslip that exists when you lock becomes visible to that employee in Self-Service, and any generated later is published straight away.': 'يؤدي القفل أيضًا إلى نشر قسائم الرواتب للموظفين: تصبح كل قسيمة موجودة وقت القفل ظاهرة للموظف في الخدمة الذاتية، وتُنشر أي قسيمة تُنشأ لاحقًا فورًا.',

  // ── W5: the bank file as four steps ──
  'Saudi bank payroll file: the Payroll payment instruction file (bank format) — the ANB Connect instruction with WPS fields — is the file to send to the bank. Mudad accepts only the WPS file your bank signs — keep the bank’s output file or a Mudad screenshot as evidence before marking the batch Accepted.': 'ملف رواتب البنك السعودي: ملف تعليمات دفع الرواتب (بصيغة البنك) — تعليمات ANB Connect بحقول حماية الأجور — هو الملف الذي يُرسل إلى البنك. لا يقبل «مدد» إلا ملف حماية الأجور الذي يوقّعه بنكك — احتفظ بملف المخرجات من البنك أو بلقطة شاشة من «مدد» كدليل قبل وضع علامة «مقبول» على الدفعة.',
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
