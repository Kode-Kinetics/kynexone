// Saudi Compliance dashboard: the action items, module names and GOSI findings the API sends as English sentences.
// English keys are the English text the API sends (see lib/complianceCopy.ts), so the English table is derived from the Arabic one.
// Arabic follows the usage of the Saudi regulators: «قوى» (Qiwa), نظام حماية الأجور (WPS), التأمينات الاجتماعية (GOSI),
// وزارة الموارد البشرية والتنمية الاجتماعية (MHRSD), الآيبان (IBAN).

/** One Arabic plural template: the six CLDR forms in the order zero-less Arabic needs them. */
const plural = (one: string, two: string, few: string, many: string, other: string) =>
  `{count, plural, one {${one}} two {${two}} few {${few}} many {${many}} other {${other}}}`;

const ar: Record<string, string> = {
  // Modules
  'QIWA': 'قوى',
  'WPS': 'حماية الأجور',

  // Qiwa
  'QIWA integration is not enabled': 'تكامل «قوى» غير مفعّل',
  'The QIWA integration module is not active for this tenant. Enable it to begin syncing employees with the Ministry of Human Resources.': 'وحدة التكامل مع «قوى» غير مفعّلة لهذه المنشأة. فعّلها لبدء مزامنة الموظفين مع وزارة الموارد البشرية.',
  'Contact your platform administrator to activate the QIWA module.': 'تواصل مع مسؤول المنصة لتفعيل وحدة «قوى».',
  'QIWA API credentials are not configured': 'لم تُضبط بيانات اعتماد واجهة «قوى»',
  'OAuth2 client credentials have not been saved. Employee sync cannot proceed until credentials are provided.': 'لم تُحفظ بيانات اعتماد العميل للمصادقة. لا يمكن متابعة مزامنة الموظفين قبل إدخالها.',
  'Go to Saudi Compliance → Configure → QIWA and enter your Client ID and Secret from the Qiwa Developer Portal.': 'انتقل إلى الامتثال السعودي ← الإعداد ← «قوى» وأدخل معرّف العميل والرمز السري من بوابة مطوري «قوى».',
  'QIWA connection is not configured': 'اتصال «قوى» غير مضبوط',
  'Establishment ID has not been set. QIWA sync requires a valid MOL-issued establishment number.': 'لم يُحدَّد رقم المنشأة. تتطلب مزامنة «قوى» رقم منشأة صالحًا صادرًا عن وزارة الموارد البشرية.',
  'Navigate to Saudi Compliance → Configure → QIWA and enter the Establishment ID.': 'انتقل إلى الامتثال السعودي ← الإعداد ← «قوى» وأدخل رقم المنشأة.',
  'QIWA connection is in an error state': 'اتصال «قوى» في حالة خطأ',
  'The QIWA integration has encountered an error (status: {status}). Sync will be suspended until this is resolved.': 'واجه تكامل «قوى» خطأً (الحالة: ⁦{status}⁩). ستتوقف المزامنة إلى أن تُعالج المشكلة.',
  'Review the QIWA configuration for credential or establishment ID issues.': 'راجع إعدادات «قوى» بحثًا عن مشكلات في بيانات الاعتماد أو رقم المنشأة.',
  'Qiwa data check only: nothing is sent to Qiwa': 'فحص بيانات «قوى» فقط: لا يُرسَل شيء إلى «قوى»',
  'This server checks your employee records against what Qiwa requires. It does not send anything to Qiwa or MHRSD, so a passed check is not a Qiwa filing.': 'يفحص هذا الخادم سجلات موظفيك وفق متطلبات «قوى». ولا يرسل أي شيء إلى «قوى» أو وزارة الموارد البشرية، فاجتياز الفحص لا يعني تقديمًا فعليًا في «قوى».',
  'Record contract and employee changes in Qiwa itself, and keep the Qiwa confirmation as evidence before relying on Qiwa status for an inspection.': 'سجّل تغييرات العقود والموظفين في «قوى» نفسها، واحتفظ بتأكيد «قوى» دليلًا قبل الاعتماد على حالتها في أي تفتيش.',
  '{count, plural, one {# employee is blocked from QIWA sync} other {# employees are blocked from QIWA sync}}': plural('موظف واحد محجوب من مزامنة «قوى»', 'موظفان محجوبان من مزامنة «قوى»', '# موظفين محجوبون من مزامنة «قوى»', '# موظفًا محجوبًا من مزامنة «قوى»', '# موظف محجوب من مزامنة «قوى»'),
  'These employees are missing required fields (e.g. National ID, Date of Birth, Job Title) and cannot be submitted to QIWA.': 'تنقص هؤلاء الموظفين حقول مطلوبة (مثل رقم الهوية الوطنية وتاريخ الميلاد والمسمى الوظيفي)، ولا يمكن إرسالهم إلى «قوى».',
  'Open the People module and complete the missing QIWA fields for each blocked employee.': 'افتح وحدة الموظفين وأكمل حقول «قوى» الناقصة لكل موظف محجوب.',
  '{count, plural, one {# QIWA sync attempt failed and needs retrying} other {# QIWA sync attempts failed and need retrying}}': plural('تعذّرت محاولة مزامنة واحدة مع «قوى» وتحتاج إلى إعادة', 'تعذّرت محاولتا مزامنة مع «قوى» وتحتاجان إلى إعادة', 'تعذّرت # محاولات مزامنة مع «قوى» وتحتاج إلى إعادة', 'تعذّرت # محاولة مزامنة مع «قوى» وتحتاج إلى إعادة', 'تعذّرت # محاولة مزامنة مع «قوى» وتحتاج إلى إعادة'),
  'One or more recent employee sync operations to QIWA did not complete successfully. This may indicate a credential or API issue.': 'لم تكتمل عملية مزامنة واحدة أو أكثر مع «قوى» مؤخرًا. قد يدل ذلك على مشكلة في بيانات الاعتماد أو الواجهة البرمجية.',
  'Review sync logs and retry failed records. Check credentials if the failure rate is high.': 'راجع سجلات المزامنة وأعد محاولة السجلات الفاشلة. تحقق من بيانات الاعتماد إن كان معدل الفشل مرتفعًا.',

  // WPS
  '{count, plural, one {# employee has a missing or invalid IBAN} other {# employees have a missing or invalid IBAN}}': plural('لدى موظف واحد آيبان مفقود أو غير صالح', 'لدى موظفين آيبان مفقود أو غير صالح', 'لدى # موظفين آيبان مفقود أو غير صالح', 'لدى # موظفًا آيبان مفقود أو غير صالح', 'لدى # موظف آيبان مفقود أو غير صالح'),
  'Saudi WPS requires a valid Saudi IBAN (SA + 22 digits) for every employee. Payroll cannot be disbursed via WPS for these employees.': 'يشترط نظام حماية الأجور آيبان سعوديًا صالحًا (SA + 22 رقمًا) لكل موظف. لا يمكن صرف الرواتب عبر نظام حماية الأجور لهؤلاء الموظفين.',
  'Go to People → Employee → Payroll Profile → Payment Details and add the correct IBAN.': 'انتقل إلى الموظفون ← الموظف ← ملف الرواتب ← بيانات الدفع وأضف الآيبان الصحيح.',
  '{count, plural, one {# payroll run is awaiting approval} other {# payroll runs are awaiting approval}}': plural('مسير رواتب واحد بانتظار الاعتماد', 'مسيرا رواتب بانتظار الاعتماد', '# مسيرات رواتب بانتظار الاعتماد', '# مسيرًا للرواتب بانتظار الاعتماد', '# مسير رواتب بانتظار الاعتماد'),
  'Payroll runs that are not yet Locked or Paid have not been submitted through WPS. Delays beyond the 10th of the month may result in non-compliance.': 'مسيرات الرواتب التي لم تُقفل أو تُصرف بعد لم تُرسل عبر نظام حماية الأجور. وقد يُعد التأخر بعد اليوم العاشر من الشهر مخالفة.',
  'Navigate to Payroll and lock or approve the pending runs.': 'انتقل إلى الرواتب وأقفل المسيرات المعلّقة أو اعتمدها.',

  // GOSI
  'GOSI employer ID is not set': 'رقم صاحب العمل في التأمينات الاجتماعية غير مُدخل',
  'The company\'s GOSI employer ID is required for contribution filing. Without it, GOSI deductions cannot be attributed correctly.': 'رقم صاحب العمل في التأمينات الاجتماعية مطلوب لتقديم الاشتراكات، وبدونه لا يمكن نسبة استقطاعات التأمينات الاجتماعية نسبةً صحيحة.',
  'Go to Saudi Compliance → Configure → GOSI and enter the GOSI Employer ID.': 'انتقل إلى الامتثال السعودي ← الإعداد ← التأمينات الاجتماعية وأدخل رقم صاحب العمل.',
  '{count, plural, one {# employee is blocked from GOSI calculation} other {# employees are blocked from GOSI calculation}}': plural('موظف واحد محجوب من احتساب التأمينات الاجتماعية', 'موظفان محجوبان من احتساب التأمينات الاجتماعية', '# موظفين محجوبون من احتساب التأمينات الاجتماعية', '# موظفًا محجوبًا من احتساب التأمينات الاجتماعية', '# موظف محجوب من احتساب التأمينات الاجتماعية'),
  'These employees are missing a GOSI reference number or basic salary and will be excluded from GOSI contribution deductions.': 'ينقص هؤلاء الموظفين رقم مرجعي في التأمينات الاجتماعية أو راتب أساسي، وسيُستثنون من استقطاعات اشتراكات التأمينات الاجتماعية.',
  'Open the People module and ensure each employee has a GOSI Reference and an active salary structure.': 'افتح وحدة الموظفين وتأكد أن لكل موظف رقمًا مرجعيًا في التأمينات الاجتماعية وهيكل راتب فعالًا.',
  '{count, plural, one {# GCC employee — contribution rates pending legal confirmation} other {# GCC employees — contribution rates pending legal confirmation}}': plural('موظف خليجي واحد — نسب الاشتراك بانتظار التأكيد القانوني', 'موظفان خليجيان — نسب الاشتراك بانتظار التأكيد القانوني', '# موظفين خليجيين — نسب الاشتراك بانتظار التأكيد القانوني', '# موظفًا خليجيًا — نسب الاشتراك بانتظار التأكيد القانوني', '# موظف خليجي — نسب الاشتراك بانتظار التأكيد القانوني'),
  'GCC national contribution rates are seeded at the Saudi baseline pending bilateral treaty verification. Confirm applicable rates with your legal team before payroll processing.': 'نسب اشتراك مواطني دول الخليج مضبوطة مؤقتًا على النسب السعودية إلى حين التحقق من الاتفاقيات الثنائية. أكّد النسب المطبقة مع فريقك القانوني قبل معالجة الرواتب.',
  'Review GCC employee GOSI rates under Compliance → GOSI Contribution Rules and update if required.': 'راجع نسب التأمينات الاجتماعية للموظفين الخليجيين في الامتثال ← قواعد اشتراكات التأمينات الاجتماعية وحدّثها عند الحاجة.',
  '{count, plural, one {# GOSI variance detected in the last payroll run} other {# GOSI variances detected in the last payroll run}}': plural('فرق واحد في التأمينات الاجتماعية رُصد في آخر مسير رواتب', 'فرقان في التأمينات الاجتماعية رُصدا في آخر مسير رواتب', '# فروق في التأمينات الاجتماعية رُصدت في آخر مسير رواتب', '# فرقًا في التأمينات الاجتماعية رُصد في آخر مسير رواتب', '# فرق في التأمينات الاجتماعية رُصد في آخر مسير رواتب'),
  'Actual GOSI deductions in the most recent run differ from expected rule-based amounts. This may indicate a rule change that was not applied retroactively.': 'تختلف استقطاعات التأمينات الاجتماعية الفعلية في أحدث مسير عن المبالغ المتوقعة وفق القواعد. وقد يدل ذلك على تغيير في القواعد لم يُطبَّق بأثر رجعي.',
  'Run the GOSI variance report for the most recent payroll period and investigate discrepancies.': 'شغّل تقرير فروق التأمينات الاجتماعية لأحدث فترة رواتب وافحص الفروقات.',

  // GOSI card: finding codes and warnings
  'GOSI first-registration date not recorded': 'تاريخ التسجيل الأول في التأمينات الاجتماعية غير مسجّل',
  'GOSI reference number missing': 'الرقم المرجعي في التأمينات الاجتماعية مفقود',
  'Basic salary missing': 'الراتب الأساسي مفقود',
  'Nationality not recorded': 'الجنسية غير مسجّلة',
  'GCC contribution rates pending confirmation': 'نسب اشتراك الخليجيين بانتظار التأكيد',
  'Company GOSI employer ID is not set.': 'رقم صاحب العمل في التأمينات الاجتماعية للشركة غير مُدخل.',
  '{count, plural, one {# employee is missing a GOSI reference number.} other {# employees are missing a GOSI reference number.}}': plural('ينقص موظفًا واحدًا الرقم المرجعي في التأمينات الاجتماعية.', 'ينقص موظفين الرقم المرجعي في التأمينات الاجتماعية.', 'ينقص # موظفين الرقم المرجعي في التأمينات الاجتماعية.', 'ينقص # موظفًا الرقم المرجعي في التأمينات الاجتماعية.', 'ينقص # موظف الرقم المرجعي في التأمينات الاجتماعية.'),
  '{count, plural, one {# GCC employee — contribution rates pending legal confirmation.} other {# GCC employees — contribution rates pending legal confirmation.}}': plural('موظف خليجي واحد — نسب الاشتراك بانتظار التأكيد القانوني.', 'موظفان خليجيان — نسب الاشتراك بانتظار التأكيد القانوني.', '# موظفين خليجيين — نسب الاشتراك بانتظار التأكيد القانوني.', '# موظفًا خليجيًا — نسب الاشتراك بانتظار التأكيد القانوني.', '# موظف خليجي — نسب الاشتراك بانتظار التأكيد القانوني.'),
};

const en: Record<string, string> = Object.fromEntries(Object.keys(ar).map(key => [key, key]));

export const complianceActions = { en, ar };
