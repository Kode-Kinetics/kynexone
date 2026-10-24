/**
 * Setup → Guided setup entry and way back: the chooser, drafts, the existing-company summary and the readiness
 * checklist (src/components/companySetup). Terms follow ./companyWizard: draft = مسودة, checklist = قائمة الإعداد.
 */
const ar: Record<string, string> = {
  // Chooser
  'Create a company, or finish setting one up. Your progress is saved as a draft as you go. For an existing company, organisation sections are saved to the company when you press Save & next, and everything else when you press Save all changes. A new company is created only when you press Create company.':
    'أنشئ شركة أو أكمل إعداد شركة قائمة. يُحفظ تقدمك كمسودة أولًا بأول. في الشركة القائمة تُحفظ أقسام الهيكل التنظيمي في الشركة عند الضغط على «حفظ والتالي»، وكل ما عداها عند الضغط على «حفظ كل التغييرات». ولا تُنشأ الشركة الجديدة إلا عند الضغط على «إنشاء الشركة».',
  'Finish setting up your company. Your progress is saved as a draft as you go. Organisation sections are saved to the company when you press Save & next, and everything else when you press Save all changes.':
    'أكمل إعداد شركتك. يُحفظ تقدمك كمسودة أولًا بأول. تُحفظ أقسام الهيكل التنظيمي في الشركة عند الضغط على «حفظ والتالي»، وكل ما عداها عند الضغط على «حفظ كل التغييرات».',
  'Legal details, branches, organisation, work rules and its administrator, step by step. The company and its setup are created together.':
    'البيانات القانونية والفروع والهيكل التنظيمي وقواعد العمل ومسؤول الشركة، خطوة بخطوة. تُنشأ الشركة وإعدادها معًا.',
  'Go to the dashboard': 'الانتقال إلى لوحة المعلومات',
  'The draft could not be discarded. Check your connection and try again.': 'تعذّر حذف المسودة. تحقّق من اتصالك وحاول مرة أخرى.',
  // Drafts
  'Started by {name}, saved {when}': 'بدأها {name}، وحُفظت {when}',
  // Readiness checklist
  'Recommended (optional)': 'موصى به (اختياري)',
  'Next: {item}': 'التالي: {item}',
  'Nothing else to do before payroll.': 'لا شيء آخر مطلوب قبل مسير الرواتب.',
  'Show what is left': 'عرض ما تبقّى',
  'Show the checklist': 'عرض قائمة الإعداد',
  'Hide the checklist': 'إخفاء قائمة الإعداد',
  'Loading the checklist…': 'جارٍ تحميل قائمة الإعداد…',
  'The checklist could not be loaded. Check your connection and try again.': 'تعذّر تحميل قائمة الإعداد. تحقّق من اتصالك وحاول مرة أخرى.',
  // Existing-company summary
  'Adds what is missing and updates what you edit. Organisation sections are saved to the company when you press Save & next; everything else is applied when you press Save all changes.':
    'يضيف ما ينقص ويحدّث ما تعدّله. تُحفظ أقسام الهيكل التنظيمي في الشركة عند الضغط على «حفظ والتالي»، وكل ما عداها يُطبَّق عند الضغط على «حفظ كل التغييرات».',
};

const en: Record<string, string> = Object.fromEntries(Object.keys(ar).map(key => [key, key]));

export const setupGuidedEntry = { en, ar };
