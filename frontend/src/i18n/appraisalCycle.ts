/**
 * Appraisal cycle completion (views/PerformancePage.tsx and components/performance/ReviewHrActions.tsx): HR's waive,
 * exclude and correct-after-publication steps, closing a cycle, and the honest wording of what an approved
 * recommendation does. Terms follow GLOSSARY.md: التقييم, المعايرة, النشر, الاعتماد.
 */
const ar: Record<string, string> = {
  // Recommendations: what approval does (and does not) do.
  'Approved recommendations are a record for HR to act on. Nothing in pay or payroll changes automatically.':
    'التوصيات المعتمدة سجلّ تتصرّف بناءً عليه الموارد البشرية. لا يتغيّر شيء في الأجر أو مسير الرواتب تلقائياً.',
  'Approving records the decision only. Nothing in pay or payroll changes automatically: HR applies an approved increment, promotion or bonus through a compensation cycle or an employee change, each with its own approval.':
    'الاعتماد يسجّل القرار فقط، ولا يتغيّر شيء في الأجر أو مسير الرواتب تلقائياً: تطبّق الموارد البشرية الزيادة أو الترقية أو المكافأة المعتمدة عبر دورة تعويضات أو طلب تعديل على بيانات الموظف، ولكلٍّ منهما اعتماده الخاص.',

  // Continuous feedback: a private note is its author's alone.
  'Private (only visible to you)': 'خاص (يظهر لك وحدك)',

  // Calibration board: the thresholds the server applies.
  '{manager}: avg {avg} — Possible leniency (avg above 85)': '{manager}: المتوسط {avg} — احتمال تساهل (المتوسط أعلى من 85)',
  '{manager}: avg {avg} — Possible severity (avg below 50)': '{manager}: المتوسط {avg} — احتمال تشدد (المتوسط أقل من 50)',

  // Closing a cycle.
  'Close cycle': 'إغلاق الدورة',
  'Closing…': 'جارٍ الإغلاق…',
  'Could not close the cycle.': 'تعذّر إغلاق الدورة.',
  'Excluded': 'مستبعد',

  // HR actions on one review.
  'Waive self-assessment': 'الإعفاء من التقييم الذاتي',
  'Exclude from cycle': 'استبعاد من الدورة',
  'Correct score': 'تصحيح الدرجة',
  'Waive self-assessment — {name}': 'الإعفاء من التقييم الذاتي — {name}',
  'Exclude from cycle — {name}': 'استبعاد من الدورة — {name}',
  'Correct score — {name}': 'تصحيح الدرجة — {name}',
  'The manager review opens without the employee’s self-assessment. Use this when the employee cannot or will not submit it (long absence, no system access).':
    'تُفتح مراجعة المدير دون التقييم الذاتي للموظف. استخدم ذلك عندما لا يستطيع الموظف تقديمه أو لا يقدّمه (غياب طويل، أو لا وصول له إلى النظام).',
  'The employee is not appraised in this cycle and no longer holds it back. Use this for a leaver or someone who should not have been enrolled.':
    'لن يُقيَّم الموظف في هذه الدورة ولن يؤخّر إغلاقها. استخدم ذلك لموظف غادر أو لموظف ما كان يجب تسجيله.',
  'This result has been published. Correcting it withdraws it: publish it again and the employee acknowledges the corrected result.':
    'نُشرت هذه النتيجة. تصحيحها يسحبها: انشرها مرة أخرى ليُقرّ الموظف بالنتيجة المصحّحة.',
  'Leave a score blank to keep it. Scores are 0 to 100.': 'اترك الدرجة فارغة للإبقاء عليها. الدرجات من 0 إلى 100.',
  'Reason (recorded in the audit trail) *': 'السبب (يُسجَّل في سجل التدقيق) *',
  'Write the reason.': 'اكتب السبب.',
  'Every score must be between 0 and 100.': 'يجب أن تكون كل درجة بين 0 و100.',
  'Record': 'تسجيل',
  'Recording…': 'جارٍ التسجيل…',
  'The action could not be recorded.': 'تعذّر تسجيل الإجراء.',
  'Self-assessment waived. The manager review is open.': 'تم الإعفاء من التقييم الذاتي. مراجعة المدير مفتوحة.',
  '{name} is excluded from this cycle.': 'تم استبعاد {name} من هذه الدورة.',
  'Score corrected.': 'تم تصحيح الدرجة.',
  'Score corrected and the published result withdrawn. Publish it again to re-issue it.': 'تم تصحيح الدرجة وسحب النتيجة المنشورة. انشرها مرة أخرى لإعادة إصدارها.',

  // KPI from goals (manager review).
  'KPI from {count} agreed goals: {score} (weights total {weight}%)': 'مؤشر الأداء من {count} أهداف معتمدة: {score} (مجموع الأوزان {weight}%)',
  'The agreed goals’ weights total {weight}%. They must total 100% before the review can be submitted.':
    'مجموع أوزان الأهداف المعتمدة {weight}%. يجب أن يبلغ 100% قبل إرسال المراجعة.',

  // Goals belong to an appraisal cycle.
  'Appraisal cycle': 'دورة التقييم',
  'Not part of an appraisal cycle': 'ليس ضمن دورة تقييم',
  'The appraisal cycle this goal is scored in. Its agreed goals make up the KPI score of the review, and their weights must total 100%.':
    'دورة التقييم التي يُحتسب فيها هذا الهدف. تشكّل أهدافها المعتمدة درجة مؤشرات الأداء في التقييم، ويجب أن يبلغ مجموع أوزانها 100%.',

  // Editing a draft cycle: template and who it appraises.
  'Edit draft cycle — {name}': 'تعديل مسودة الدورة — {name}',
  'Scorecard template for this cycle': 'قالب بطاقة التقييم لهذه الدورة',
  'Every review in the cycle is scored on this template. A cycle cannot be launched without one.':
    'يُقيَّم كل تقييم في الدورة وفق هذا القالب. لا يمكن إطلاق الدورة بدونه.',
  'Choose a template': 'اختر قالبًا',
  'Company appraised': 'الشركة المشمولة بالتقييم',
  'The cycle enrols the active employees of this company who joined by the end of the review period. Where your organisation has several companies, a cycle cannot be launched without one.':
    'تسجّل الدورة الموظفين النشطين في هذه الشركة ممن التحقوا قبل نهاية فترة التقييم. إذا كانت منشأتك تضم عدة شركات، فلا يمكن إطلاق الدورة دون اختيار شركة.',
  'Choose a company': 'اختر شركة',
  'Department (optional)': 'الإدارة (اختياري)',
  'Leave empty to appraise the whole company, or choose one department of it.': 'اتركه فارغًا لتقييم الشركة كلها، أو اختر إحدى إداراتها.',
  'Whole company': 'الشركة كلها',
  'The cycle could not be saved.': 'تعذّر حفظ الدورة.',
};

export const appraisalCycle = {
  en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) as Record<string, string>,
  ar,
};
