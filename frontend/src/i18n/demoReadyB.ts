/**
 * Strings added by the demo-readiness fixes, stream B (one file per agent, so no two edits collide).
 * Spread into the en and ar dictionaries in translations.ts; add keys here, never there. Keys are the English text.
 */
const pairs: Record<string, [string, string]> = {
  // Recruitment section menu (the tab strip's accessible name)
  'Recruitment sections': ['Recruitment sections', 'أقسام التوظيف'],
  // Draft requisition edit: the failure note now names the renamed list action
  'Your changes were saved as a draft, but sending the requisition for approval failed. Use Submit for approval on the list to send it.': [
    'Your changes were saved as a draft, but sending the requisition for approval failed. Use Submit for approval on the list to send it.',
    'تم حفظ تعديلاتك كمسودة، لكن تعذّر إرسال طلب التوظيف للاعتماد. استخدم «إرسال للموافقة» في القائمة لإرساله.'],
  // Onboarding tab with no checklist templates
  'Onboarding checklists are not set up': ['Onboarding checklists are not set up', 'قوائم مهام التهيئة غير مُعدّة بعد'],
  'Create a checklist template': ['Create a checklist template', 'إنشاء قالب قائمة مهام'],
  'Onboarding tasks come from a checklist template, and this company has none. Create a checklist template in Setup, then come back here to generate tasks for a new hire.': [
    'Onboarding tasks come from a checklist template, and this company has none. Create a checklist template in Setup, then come back here to generate tasks for a new hire.',
    'تأتي مهام التهيئة من قالب قائمة مهام، وهذه الشركة لا تملك أي قالب. أنشئ قالب قائمة مهام في الإعدادات، ثم عد إلى هنا لإنشاء المهام لموظف جديد.'],
  'Onboarding tasks come from a checklist template, and this company has none. Ask an administrator to create a checklist template in Setup.': [
    'Onboarding tasks come from a checklist template, and this company has none. Ask an administrator to create a checklist template in Setup.',
    'تأتي مهام التهيئة من قالب قائمة مهام، وهذه الشركة لا تملك أي قالب. اطلب من أحد المسؤولين إنشاء قالب قائمة مهام في الإعدادات.'],
  // Date control group name (the shared date control had no Arabic name for it)
  'Target joining date': ['Target joining date', 'تاريخ الانضمام المستهدف'],
};

export const demoReadyB: { en: Record<string, string>; ar: Record<string, string> } = {
  en: Object.fromEntries(Object.entries(pairs).map(([key, [en]]) => [key, en])),
  ar: Object.fromEntries(Object.entries(pairs).map(([key, [, ar]]) => [key, ar])),
};
