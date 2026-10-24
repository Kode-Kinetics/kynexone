/**
 * Company setup wizard, step 5 parts (components/companySetup/RulesStep.tsx and rules/*), More settings
 * (MoreSettingsLinks.tsx) and the way back from a settings page (setup/SetupWorkspaceReturn.tsx).
 * Terms follow ./companyWizard: draft = مسودة; country pack = حزمة الدولة; company super admin = المسؤول الأعلى للشركة.
 */
const ar: Record<string, string> = {
  // Parts and their headers
  'Working week & numbering': 'أسبوع العمل والترقيم',
  'Checked on the review step and saved together with the company.': 'يُتحقق منها في خطوة المراجعة وتُحفظ مع الشركة.',
  'Leave & holidays': 'الإجازات والعطلات',
  'Statutory leave, public holidays and the leave policies employees request against.': 'الإجازات النظامية والعطلات الرسمية وسياسات الإجازات التي يطلب الموظفون بموجبها.',
  'End of service': 'نهاية الخدمة',
  'Gratuity rates and the other rules that follow the company’s country.': 'نسب مكافأة نهاية الخدمة والقواعد الأخرى التابعة لدولة الشركة.',
  'Some open here, beside the wizard. The others open their own page, with a way back to this step.': 'بعضها يُفتح هنا بجانب المعالج، والبقية تُفتح في صفحتها الخاصة مع طريق للعودة إلى هذه الخطوة.',
  // How each part saves
  'Saves as you edit': 'يُحفظ أثناء التعديل',
  'Saved when you press Create company': 'يُحفظ عند الضغط على «إنشاء الشركة»',
  'Saved when you press Save all changes': 'يُحفظ عند الضغط على «حفظ كل التغييرات»',
  'Each setting saves on its own screen': 'كل إعداد يُحفظ في شاشته الخاصة',
  'Saved · {summary}': 'تم الحفظ · {summary}',
  '{count} added': '{count, plural, one {أُضيف #} other {أُضيفت #}}',
  '{count} updated': '{count, plural, one {حُدِّث #} other {حُدِّثت #}}',
  '{count} removed': '{count, plural, one {أُزيل #} other {أُزيلت #}}',
  // Leave & holidays
  'From the {country} country pack': 'من حزمة دولة {country}',
  'The statutory minimums. Added only if they do not exist yet; existing ones are never changed.': 'الحدود الدنيا النظامية. تُضاف فقط إن لم تكن موجودة، ولا يُغيَّر الموجود أبدًا.',
  'No holiday calendar will be added. Add one below, or later in Setup → Holiday calendars, before the first leave request.': 'لن يُضاف تقويم عطلات. أضف تقويمًا أدناه، أو لاحقًا من الإعداد ← تقويمات العطلات، قبل أول طلب إجازة.',
  'The legal minimums cannot be lowered here. Add company extras in Leave policies below.': 'لا يمكن خفض الحدود الدنيا النظامية هنا. أضف مزايا الشركة الإضافية في سياسات الإجازات أدناه.',
  'No leave policies will be added. Employees cannot request leave until you add them below or in Setup → Leave policies.': 'لن تُضاف سياسات إجازات. لا يستطيع الموظفون طلب إجازة حتى تضيفها أدناه أو من الإعداد ← سياسات الإجازات.',
  'Leave policies, holidays and leave types': 'سياسات الإجازات والعطلات وأنواع الإجازات',
  'This company’s own leave types and policies. Holiday calendars can be this company’s or shared by the workspace.': 'أنواع الإجازات وسياساتها الخاصة بهذه الشركة. ويمكن أن يكون تقويم العطلات خاصاً بهذه الشركة أو مشتركاً في مساحة العمل.',
  'This company’s own leave types and policies, added after Create. Workspace-wide holiday calendars apply now.': 'أنواع الإجازات وسياساتها الخاصة بهذه الشركة، وتُضاف بعد الإنشاء. وتسري تقويمات العطلات العامة لمساحة العمل من الآن.',
  'This company’s own leave policies, holidays and leave types. Changes save as you edit.': 'سياسات الإجازات والعطلات وأنواع الإجازات الخاصة بهذه الشركة. تُحفظ التغييرات أثناء التعديل.',
  'Only an Admin or HR Manager with leave access can change these lists. Ask your group super admin, or continue: the country pack above is still applied.': 'لا يغيّر هذه القوائم إلا مسؤول النظام أو مدير الموارد البشرية الذي لديه صلاحية الإجازات. راجع المسؤول الأعلى للمجموعة، أو تابع: حزمة الدولة أعلاه ستُطبَّق.',
  'Leave lists': 'قوائم الإجازات',
  'New holiday calendar': 'تقويم عطلات جديد',
  'New calendar': 'تقويم جديد',
  // End of service
  'Country rules for {country}': 'قواعد الدولة: {country}',
  'End-of-service rates, WPS and document alerts. They apply to every company registered in {country}.': 'نسب نهاية الخدمة وحماية الأجور وتنبيهات المستندات. تنطبق على كل شركة مسجلة في {country}.',
  'Country rules are shared by every company, so only a group administrator can change them. The country pack above is still applied.': 'قواعد الدولة مشتركة بين كل الشركات، لذا لا يغيّرها إلا مسؤول المجموعة. حزمة الدولة أعلاه ستُطبَّق.',
  // More settings
  'Time & attendance': 'الوقت والحضور',
  'Pay & benefits': 'الرواتب والمزايا',
  'Opens here': 'يُفتح هنا',
  'Opens its page': 'يُفتح في صفحته',
  'Saving the draft…': 'جارٍ حفظ المسودة…',
  'Attendance rules and HR approval rules have no screen of their own yet. Ask your group super admin to set these up.': 'ليس لقواعد الحضور وقواعد موافقات الموارد البشرية شاشة خاصة بعد. اطلب من المسؤول الأعلى للمجموعة إعدادها.',
  // Unsaved editor forms
  'Save your changes to {editor}?': 'هل تريد حفظ تغييراتك على {editor}؟',
  'The {editor} form is still open and its changes are not saved. Keep editing to save them there, or discard them and continue.': 'نموذج {editor} ما زال مفتوحًا وتغييراته غير محفوظة. تابع التعديل لحفظها هناك، أو تجاهلها وتابع.',
  'Keep editing': 'متابعة التعديل',
  'Your changes to {editor} are not saved. Leave and discard them?': 'تغييراتك على {editor} غير محفوظة. هل تريد المغادرة وتجاهلها؟',
  // The way back
  'Back to company setup': 'العودة إلى إعداد الشركة',
  'Hide the way back to company setup': 'إخفاء رابط العودة إلى إعداد الشركة',
  'Hide': 'إخفاء',
};

export const companyWizardRules = {
  en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) as Record<string, string>,
  ar,
};
