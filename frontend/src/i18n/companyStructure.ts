/**
 * Setup → Company structure (Companies, Branches, Locations, Departments): permission reasons, company-tab copy that
 * used to be English only, and the field checks run before a save. Terms as in ./setupExperience and ./companyWizard:
 * الشركة (company), الفرع (branch), الموقع (location), الإدارة (department), المنطقة الزمنية (time zone),
 * الدولة (country), المدينة (city), نطاق السياج الجغرافي (geofence).
 */
const ar: Record<string, string> = {
  'Changing this needs the organization.write permission.': 'يتطلب التعديل صلاحية organization.write.',
  'The companies could not be loaded.': 'تعذّر تحميل الشركات.',
  'The grades could not be loaded.': 'تعذّر تحميل الدرجات.',
  'The pay scale lines could not be loaded, so saving is turned off to keep them. Close this window and open the grade again.': 'تعذّر تحميل بنود سلّم الرواتب، لذلك أُوقف الحفظ حفاظاً عليها. أغلق هذه النافذة وافتح الدرجة من جديد.',
  'You do not have permission to remove or restore this.': 'ليست لديك صلاحية حذف هذا العنصر أو استعادته.',
  // Companies tab: validation and save messages
  'Registration number': 'رقم التسجيل',
  'Default currency': 'العملة الافتراضية',
  'Email domain': 'نطاق البريد الإلكتروني',
  '{label} is required.': '{label} مطلوب.',
  '{label} is invalid.': '{label} غير صالح.',
  'Legal name (English) is required': 'الاسم القانوني (بالإنجليزية) مطلوب',
  'Country is required — it decides this entity’s statutory rules, leave entitlements and the identity documents its employees must hold.': 'الدولة مطلوبة، فهي تحدد القواعد النظامية لهذه الشركة واستحقاقات الإجازات ووثائق الهوية المطلوبة من موظفيها.',
  'Registration number is required — it identifies this legal entity on statutory filings.': 'رقم التسجيل مطلوب، فهو يعرّف هذه الشركة في الإقرارات النظامية.',
  'Default currency is required': 'العملة الافتراضية مطلوبة',
  'Email domain looks invalid. Use a bare domain like "acme.sa" (no "@", scheme, or path), or leave it blank.': 'نطاق البريد الإلكتروني غير صالح. استخدم نطاقاً مجرداً مثل "acme.sa" (بدون "@" أو بروتوكول أو مسار)، أو اتركه فارغاً.',
  'Enter a bare domain like "acme.sa" — no "@", scheme, or path.': 'أدخل نطاقاً مجرداً مثل "acme.sa" بدون "@" أو بروتوكول أو مسار.',
  'Another company in this workspace already uses this registration number.': 'توجد شركة أخرى في مساحة العمل هذه تستخدم رقم التسجيل نفسه.',
  'Cannot deactivate the only active company — activate another company first.': 'لا يمكن إيقاف الشركة النشطة الوحيدة. فعّل شركة أخرى أولاً.',
  // Companies tab: banner, badges and statutory profile
  '{name} has no country set — set it in Setup → Companies before adding employees.': 'لم تُحدَّد دولة لشركة {name}. حدّدها في الإعداد ← الشركات قبل إضافة الموظفين.',
  '{count} companies have no country set — set each one here before adding employees.': 'لم تُحدَّد دولة لـ {count} شركات. حدّد دولة كل شركة هنا قبل إضافة الموظفين.',
  'A company’s country decides its statutory rules, leave entitlements and the identity documents its employees must hold. Until it is set, employees of that entity cannot be added.': 'تحدد دولة الشركة قواعدها النظامية واستحقاقات الإجازات ووثائق الهوية المطلوبة من موظفيها. وما لم تُحدَّد، لا يمكن إضافة موظفين لهذه الشركة.',
  'Missing: {names}.': 'الشركات الناقصة: {names}.',
  'Set country for {name}': 'تحديد الدولة لشركة {name}',
  'Draft — awaiting platform approval': 'مسودة، بانتظار اعتماد المنصة',
  'Loading statutory profile…': 'جارٍ تحميل الملف النظامي…',
  'Statutory Pack Profile': 'الملف النظامي للدولة',
  'Social Insurance': 'التأمينات الاجتماعية',
  'EOSB Formula': 'معادلة مكافأة نهاية الخدمة',
  'WPS Format': 'صيغة حماية الأجور',
  'Nationalization Scheme': 'برنامج التوطين',
  'Locale / Currency': 'اللغة والإعدادات المحلية / العملة',
  'Calendar / RTL': 'التقويم / اتجاه الكتابة',
  'The statutory rule history is tenant-wide. Configure company-specific values under GL & rates.': 'سجل القواعد النظامية يشمل المنشأة كلها. اضبط القيم الخاصة بكل شركة ضمن دفتر الأستاذ العام والمعدلات.',
  // Companies tab: edit dialog
  'Edit Company': 'تعديل الشركة',
  'Legal Name (EN)': 'الاسم القانوني (بالإنجليزية)',
  'Legal Name (AR)': 'الاسم القانوني (بالعربية)',
  'Tax Number': 'الرقم الضريبي',
  'Default Currency': 'العملة الافتراضية',
  // Branch and location checks
  'Country is required.': 'الدولة مطلوبة.',
  'City is required.': 'المدينة مطلوبة.',
  'Time zone is required.': 'المنطقة الزمنية مطلوبة.',
  'Select time zone': 'اختر المنطقة الزمنية',
  'Latitude must be between -90 and 90.': 'يجب أن يكون خط العرض بين -90 و90.',
  'Longitude must be between -180 and 180.': 'يجب أن يكون خط الطول بين -180 و180.',
  'Latitude and longitude must be given together, or both left blank.': 'يجب إدخال خط العرض وخط الطول معاً، أو تركهما فارغين.',
  'Geofence radius must be greater than zero.': 'يجب أن يكون نطاق السياج الجغرافي أكبر من صفر.',
  'Changing this needs the Admin or HR Manager role and the organization.write permission.': 'يتطلب التعديل دور مسؤول النظام أو مدير الموارد البشرية وصلاحية organization.write.',
};

export const companyStructure = {
  en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) as Record<string, string>,
  ar,
};
