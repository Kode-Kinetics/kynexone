// Launch polish: the sentences that were still English under Arabic on the payroll, compliance, home and offer screens.
// English keys are the English text (see i18n/translations.ts), so the English table is derived from the Arabic one.
// Arabic follows the payroll and compliance usage HR teams read every day: مسير الرواتب (payroll run), إقفال (lock),
// التأمينات الاجتماعية (GOSI), نطاقات (Nitaqat), الأجر الإجمالي (gross).
const ar: Record<string, string> = {
  // Payment readiness on the Payroll dashboard
  '{count} of {total} active employees cannot be paid yet': 'يتعذّر صرف رواتب {count} من أصل {total} من الموظفين النشطين حاليًا',
  'Company setup blocks this payroll': 'إعداد الشركة يمنع صرف هذا المسير',
  'These gaps will block approval when the run is validated. Salary coverage alone does not make payroll ready to pay.': 'ستمنع هذه الفجوات الاعتماد عند التحقق من المسير. تغطية الرواتب وحدها لا تجعل المسير جاهزًا للصرف.',
  'All {total} active employees have what payment requires': 'جميع الموظفين النشطين ({total}) لديهم ما يتطلبه الصرف',
  '{count, plural, one {# employee still has recommended fixes. They do not block approval but will show as validation warnings.} other {# employees still have recommended fixes. They do not block approval but will show as validation warnings.}}': '{count, plural, one {لدى موظف واحد تصحيحات موصى بها. لا تمنع الاعتماد لكنها ستظهر كتنبيهات تحقق.} two {لدى موظفين تصحيحات موصى بها. لا تمنع الاعتماد لكنها ستظهر كتنبيهات تحقق.} few {لدى # موظفين تصحيحات موصى بها. لا تمنع الاعتماد لكنها ستظهر كتنبيهات تحقق.} many {لدى # موظفًا تصحيحات موصى بها. لا تمنع الاعتماد لكنها ستظهر كتنبيهات تحقق.} other {لدى # موظف تصحيحات موصى بها. لا تمنع الاعتماد لكنها ستظهر كتنبيهات تحقق.}}',
  'The company has recommended fixes. They do not block approval.': 'لدى الشركة تصحيحات موصى بها. لا تمنع الاعتماد.',
  'Salary, payroll profile, bank details and required records are in place for this period.': 'الراتب وملف الرواتب والبيانات البنكية والسجلات المطلوبة مكتملة لهذه الفترة.',
  '{count} of {total} active employees have records to fix before next month': 'لدى {count} من أصل {total} من الموظفين النشطين بيانات تحتاج إلى تصحيح قبل الشهر القادم',
  'Company setup needs attention before next month': 'إعداد الشركة يحتاج إلى معالجة قبل الشهر القادم',
  'This run is already locked, so these gaps do not affect its payment. Fix them before the next payroll is processed.': 'هذا المسير مُقفل بالفعل، لذا لا تؤثر هذه الفجوات على صرفه. عالجها قبل معالجة مسير الشهر القادم.',
  'To fix before next month': 'للتصحيح قبل الشهر القادم',
  // Payment prerequisite labels and next actions (sent by the API in English)
  'No salary assigned for this period': 'لا يوجد راتب مُسند لهذه الفترة',
  'Assign a salary in Payroll → Employee Salary': 'أسند راتبًا من الرواتب ← رواتب الموظفين',
  'No payroll profile — no bank IBAN or MOL ID on file': 'لا يوجد ملف رواتب — لا آيبان بنكي ولا رقم وزارة الموارد البشرية مسجّل',
  'Open the employee and add their payroll and bank details': 'افتح ملف الموظف وأضف بيانات الرواتب والحساب البنكي',
  'No bank IBAN on the payroll profile': 'لا يوجد آيبان بنكي في ملف الرواتب',
  'Open the employee and add their IBAN': 'افتح ملف الموظف وأضف رقم الآيبان',
  'IBAN fails the bank format check': 'رقم الآيبان لا يجتاز فحص الصيغة البنكية',
  'Open the employee and correct their IBAN': 'افتح ملف الموظف وصحّح رقم الآيبان',
  'Required statutory details are missing or expired': 'بيانات نظامية مطلوبة ناقصة أو منتهية الصلاحية',
  'Open the employee and complete their readiness checklist': 'افتح ملف الموظف وأكمل قائمة الجاهزية',
  'No MOL ID for WPS (Mudad) reporting': 'لا يوجد رقم وزارة الموارد البشرية للإبلاغ عبر نظام حماية الأجور (مُدد)',
  'Open the employee and add their MOL ID': 'افتح ملف الموظف وأضف رقم وزارة الموارد البشرية',
  'IBAN is not a Saudi account': 'رقم الآيبان ليس لحساب سعودي',
  'Confirm the employee\'s bank account is held in Saudi Arabia': 'تأكد من أن الحساب البنكي للموظف في المملكة العربية السعودية',
  'Nationality not recorded (it decides GOSI treatment)': 'الجنسية غير مسجّلة (وهي تحدد معاملة التأمينات الاجتماعية)',
  'Open the employee and record their nationality': 'افتح ملف الموظف وسجّل جنسيته',
  'No attendance recorded in this period (full salary would be assumed)': 'لا يوجد حضور مسجّل في هذه الفترة (سيُفترض صرف الراتب كاملًا)',
  'Process or import attendance for this period': 'عالج الحضور أو استورده لهذه الفترة',
  'The company has no country, so statutory deductions cannot be worked out': 'لا توجد دولة للشركة، لذا يتعذّر احتساب الاستقطاعات النظامية',
  'Set the country in Setup → Companies': 'حدّد الدولة من الإعدادات ← الشركات',
  'The company has no currency, so the run cannot be posted or locked': 'لا توجد عملة للشركة، لذا يتعذّر ترحيل المسير أو إقفاله',
  'Set the default currency in Setup → Companies': 'حدّد العملة الافتراضية من الإعدادات ← الشركات',
  'Employees are not assigned to an active legal entity': 'الموظفون غير مرتبطين بكيان قانوني نشط',
  'Assign each employee to an active company': 'اربط كل موظف بشركة نشطة',
  'No WPS employer (MOL establishment) ID on the company': 'لا يوجد رقم صاحب العمل في نظام حماية الأجور (المنشأة) للشركة',
  'Add the WPS employer ID in Setup → Companies': 'أضف رقم صاحب العمل في نظام حماية الأجور من الإعدادات ← الشركات',
  'Review in payroll validation': 'راجع في التحقق من الرواتب',
  // Bank file status text sent by the API (payroll pipeline, Bank / WPS)
  'Generated (file ready for the bank)': 'تم إنشاء الملف (جاهز لتسليمه للبنك)',
  'Submitted to the bank': 'سُلّم إلى البنك',
  'Accepted \u2014 evidence attached (not verified by Mudad)': 'مقبول — أُرفق الدليل (لم تتحقق منه منصة مُدد)',
  'Accepted \u2014 no Mudad evidence on record': 'مقبول — لا يوجد دليل من منصة مُدد',
  // Employee overview (new-hire readiness)
  'Employee overview': 'نظرة عامة على الموظف',
  'Recommended next action': 'الإجراء التالي الموصى به',
  'Guidance is based on the employee’s current readiness and access state.': 'تستند هذه التوصية إلى حالة جاهزية الموظف وصلاحيات وصوله الحالية.',
  'Open requirements': 'المتطلبات المفتوحة',
  'Record summary': 'ملخص السجل',
  'Not provided': 'غير مُدخل',
  'Nothing requires attention.': 'لا شيء يتطلب المتابعة.',
  'No readiness blockers are currently reported.': 'لا توجد معوقات جاهزية مسجّلة حاليًا.',
  'Profile completeness': 'اكتمال الملف',
  'Work location': 'موقع العمل',
  'Complete {item}': 'أكمل: {item}',
  'This is the highest-priority open requirement.': 'هذا أعلى المتطلبات المفتوحة أولوية.',
  'Complete this requirement to keep the employee record ready.': 'أكمل هذا المتطلب لتبقى سجلات الموظف جاهزة.',
  'Required by regulation': 'مطلوب بموجب الأنظمة',
  'Needs approval from a different person': 'يحتاج إلى موافقة شخص آخر',
  'Recommended, not required': 'موصى به وليس إلزاميًا',
  'Not valid': 'غير صالح',
  'On an interim number': 'على رقم مؤقت',
  'Cannot be checked yet': 'لا يمكن التحقق منه بعد',
  // ERP journal export formats (the API describes them in English)
  'Generic journal CSV (line-per-row)': 'ملف قيود عام بصيغة CSV (سطر لكل بند)',
  'Neutral line-level export. Debit and Credit are separate columns and exactly one is non-zero per row. Import using AccountingDate (not SystemPostedDate) \u2014 SystemPostedDate is when our system wrote the row, AccountingDate is the accounting month the line belongs to. CompanyCode is per line (the company\'s ERP code); ExternalAccountCode is your ERP account when one is mapped. ExportId + EntryId + DR/CR identify a line uniquely: use them to refuse a second import of the same file. When any line carries a cost centre the file ends with CostCentre (code), CostCentreName and CostCentreErpCode (the ERP cost-centre value: the cost centre\'s ERP segment when set, else its code); they are blank on lines with no cost centre, and absent when no line has one.': 'تصدير محايد على مستوى البنود. المدين والدائن عمودان منفصلان، ويكون أحدهما فقط غير صفري في كل سطر. استورد الملف بالاعتماد على \u2066AccountingDate\u2069 (وليس \u2066SystemPostedDate\u2069)؛ فـ\u2066SystemPostedDate\u2069 هو وقت كتابة نظامنا للسطر، و\u2066AccountingDate\u2069 هو الشهر المحاسبي الذي ينتمي إليه البند. \u2066CompanyCode\u2069 لكل بند (رمز الشركة في نظام تخطيط الموارد)؛ و\u2066ExternalAccountCode\u2069 هو حسابك في النظام عند ربطه. تحدد الأعمدة \u2066ExportId\u2069 و\u2066EntryId\u2069 والمدين/الدائن كل بند على نحو فريد: استخدمها لرفض استيراد الملف نفسه مرة ثانية. وإذا حمل أي بند مركز تكلفة، ينتهي الملف بالأعمدة \u2066CostCentre\u2069 (الرمز) و\u2066CostCentreName\u2069 و\u2066CostCentreErpCode\u2069 (قيمة مركز التكلفة في نظام تخطيط الموارد: مقطعه في النظام إن وُجد وإلا رمزه)؛ وتكون فارغة في البنود بلا مركز تكلفة، وتغيب كليًا إذا لم يحمل أي بند مركز تكلفة.',
  '(template – validate in your ERP test instance)': '(نموذج — تحقّق منه في نسخة الاختبار من نظام تخطيط الموارد)',
  'UTF-8 byte-order mark (for Excel)': 'علامة ترتيب البايتات UTF-8 (لبرنامج Excel)',
  'Set to true to add a UTF-8 byte-order mark so Excel opens Arabic text correctly. Leave it off (false) for an ERP import.': 'اضبطها على true لإضافة علامة ترتيب البايتات UTF-8 ليفتح Excel النص العربي بشكل صحيح. اتركها معطّلة (false) عند الاستيراد إلى نظام تخطيط الموارد.',
  'Voided runs': 'المسيرات المُبطَلة',
  // Pickers (the browser's own month and date controls follow the browser's language, so these are drawn here)
  'Month': 'الشهر',
  'Year': 'السنة',
  'Day': 'اليوم',
  'Hour': 'الساعة',
  'Minute': 'الدقيقة',
  // Pay line names the payroll run stores in English
  'Late/early attendance deduction': 'خصم التأخير والخروج المبكر',
  'Loss of Pay': 'خصم أيام الغياب',
  'Leave deduction': 'خصم الإجازة',
  'Salary advance repayment': 'سداد السلفة',
  'Income tax': 'ضريبة الدخل',
  'Other Deductions': 'استقطاعات أخرى',
  // Validation findings in the run review
  'GOSI cohort not recorded, so the contribution basis is unverified. Enter the GOSI first-registration date on the employee record; the next run picks it up.': 'لم تُسجَّل فئة الاشتراك في التأمينات الاجتماعية، لذا فأساس الاشتراك غير موثّق. أدخل تاريخ التسجيل الأول في التأمينات على سجل الموظف، وسيلتقطه المسير التالي.',
  'Bank details came from an employee import and have not been confirmed by a second person. Confirm them on the employee record before a run is locked.': 'جاءت البيانات البنكية من استيراد الموظفين ولم يؤكدها شخص ثانٍ. أكّدها في سجل الموظف قبل إقفال أي مسير.',
  'A salary change reaches back further than the arrears lookback, so some finalised periods were not settled by this run. Raise a supplementary run, or widen the arrears lookback in the payroll settings.': 'يمتد تغيير في الراتب إلى فترة أبعد من مدة احتساب المتأخرات، فلم يسوِّ هذا المسير بعض الفترات المقفلة. أنشئ مسيرًا إضافيًا أو وسّع مدة احتساب المتأخرات في إعدادات الرواتب.',
  'Records required before payroll are missing. Until they are recorded, exporting the WPS or bank file needs an explicit acknowledgement to include this employee.': 'ثمة سجلات مطلوبة قبل الرواتب غير مكتملة. إلى أن تُسجَّل، يتطلب تصدير ملف حماية الأجور أو البنك إقرارًا صريحًا لإدراج هذا الموظف.',
  'Records required before payroll are missing. The WPS or bank file will refuse this employee until they are recorded.': 'ثمة سجلات مطلوبة قبل الرواتب غير مكتملة. سيرفض ملف حماية الأجور أو البنك هذا الموظف إلى أن تُسجَّل.',
  'Payroll setting': 'إعداد الرواتب',
  // Reconciliation tab
  'Total Gross': 'إجمالي الرواتب قبل الخصم',
  // Saudi Compliance dashboard: one count for the headline and the Action Center
  '{count, plural, one {# action needs attention} other {# actions need attention}}': '{count, plural, one {إجراء واحد يحتاج إلى متابعة} two {إجراءان يحتاجان إلى متابعة} few {# إجراءات تحتاج إلى متابعة} many {# إجراءً يحتاج إلى متابعة} other {# إجراء يحتاج إلى متابعة}}',
  // Home Saudization tile
  'Band not verified': 'النطاق غير موثّق',
};

/** Stored enum values: the key is a machine key, so the English text is listed beside the Arabic. */
const enums: Record<string, [string, string]> = {
  'enum.EmploymentType.FullTime': ['Full-time', 'دوام كامل'],
  'enum.EmploymentType.PartTime': ['Part-time', 'دوام جزئي'],
  'enum.EmploymentType.Contract': ['Contract', 'عقد'],
  'enum.EmploymentType.Temporary': ['Temporary', 'مؤقت'],
  'enum.EmploymentType.Intern': ['Intern', 'متدرب'],
  'enum.EmploymentType.Seasonal': ['Seasonal', 'موسمي'],
  'enum.EmploymentType.Consultant': ['Consultant', 'استشاري'],
  'enum.EmploymentType.Probation': ['Probation', 'تحت التجربة'],
};
for (const [key, [, arabic]] of Object.entries(enums)) ar[key] = arabic;

const en: Record<string, string> = {
  ...Object.fromEntries(Object.keys(ar).filter(key => !(key in enums)).map(key => [key, key])),
  ...Object.fromEntries(Object.entries(enums).map(([key, [english]]) => [key, english])),
};

export const launchPolish = { en, ar };
