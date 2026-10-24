/**
 * Company setup wizard, Step 7 review (src/components/companySetup/ReviewStep.tsx).
 * Terms follow ./companyWizard.ts: company super admin = المسؤول الأعلى للشركة; draft = مسودة; Step 4 = الخطوة 4.
 */
const ar: Record<string, string> = {
  // Decision card
  "Ready to create {company}": "جاهزة لإنشاء {company}",
  "Ready to update {company}": "جاهزة لتحديث {company}",
  "Check what will be set up below, then press Create company at the bottom. Nothing is created until you do.": "راجع ما سيُعدّ أدناه، ثم اضغط «إنشاء الشركة» في أسفل الصفحة. لن يُنشأ أي شيء قبل ذلك.",
  "Check the changes below, then press Save all changes at the bottom. Nothing changes until you do.": "راجع التغييرات أدناه، ثم اضغط «حفظ كل التغييرات» في أسفل الصفحة. لن يتغيّر أي شيء قبل ذلك.",
  "{count, plural, one {# thing to fix} other {# things to fix}} before you can create {company}": "{count, plural, one {أمر واحد يلزم إصلاحه} two {أمران يلزم إصلاحهما} few {# أمور يلزم إصلاحها} many {# أمرًا يلزم إصلاحه} other {# أمر يلزم إصلاحه}} قبل أن تتمكن من إنشاء {company}",
  "{count, plural, one {# thing to fix} other {# things to fix}} before you can update {company}": "{count, plural, one {أمر واحد يلزم إصلاحه} two {أمران يلزم إصلاحهما} few {# أمور يلزم إصلاحها} many {# أمرًا يلزم إصلاحه} other {# أمر يلزم إصلاحه}} قبل أن تتمكن من تحديث {company}",
  "Nothing has been created yet.": "لم يُنشأ أي شيء بعد.",
  "Nothing has been changed yet.": "لم يتغيّر أي شيء بعد.",
  "Fix the first one": "أصلح الأول",
  "Fix this": "أصلحه الآن",
  "Things to fix": "أمور يلزم إصلاحها",
  "The check did not pass, but named nothing to fix. Check again; if it repeats, ask your group super admin.": "لم ينجح التحقق، لكنه لم يحدد شيئًا لإصلاحه. تحقق مرة أخرى؛ وإذا تكرر ذلك فاطلب المساعدة من المسؤول الأعلى للمجموعة.",
  "Organisation rows you saved in Step 4 are already in the company.": "صفوف الهيكل التنظيمي التي حفظتها في الخطوة 4 موجودة في الشركة بالفعل.",
  // Counts and row labels
  "Select a count to see the rows behind it.": "اختر عددًا لعرض الصفوف التي يمثلها.",
  "Show everything": "عرض الكل",
  "{count} already set up": "{count} مُعدّ بالفعل",
  "{count} saved in Step 4": "{count} محفوظ في الخطوة 4",
  "{count} to fix": "{count} للإصلاح",
  "Saved in Step 4": "محفوظ في الخطوة 4",
  "Needs a fix": "يحتاج إلى إصلاح",
  "Already saved to the company in Step 4.": "حُفظ في الشركة بالفعل في الخطوة 4.",
  "Already saved to the company in Step 4. Save all changes does not change these again.": "حُفظت في الشركة بالفعل في الخطوة 4. لن يغيّرها «حفظ كل التغييرات» مرة أخرى.",
  "Already set up. It is used as it is.": "مُعدّ بالفعل، ويُستخدم كما هو.",
  "Kept as it is. To remove it, use its Setup tab.": "يبقى كما هو. لإزالته استخدم تبويبه في الإعداد.",
  "Keep my value ({value})": "احتفظ بقيمتي ({value})",
  "Worth knowing. These do not stop you.": "للعلم، وهذه لا تمنعك من المتابعة.",
  // Waiting and failure
  "Checking your setup against the company’s records…": "جارٍ التحقق من الإعداد مقابل سجلات الشركة…",
  "We could not check your setup.": "تعذّر التحقق من الإعداد.",
  "Nothing has been created. Check your connection and try again.": "لم يُنشأ أي شيء. تحقق من الاتصال وحاول مرة أخرى.",
  "Nothing has been changed. Check your connection and try again.": "لم يتغيّر أي شيء. تحقق من الاتصال وحاول مرة أخرى.",
};

export const setupReview = {
  en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) as Record<string, string>,
  ar,
};
