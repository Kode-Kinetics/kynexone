/**
 * Strings added by the recruitment walkthrough fixes, region B of views/RecruitmentPage.tsx (one file per agent, so no two edits collide).
 * Spread into the en and ar dictionaries in translations.ts; add keys here, never there. Keys are the English text.
 * Terms follow ./launchRecruitment: Talent Pool = مجمع المواهب, pipeline = المسار, candidate = المرشح, Job Openings = الوظائف الشاغرة.
 */
export const recruitmentFixesB: { en: Record<string, string>; ar: Record<string, string> } = {
  en: {
    'Blacklist': 'Blacklist',
    'Blacklisted': 'Blacklisted',
    'Blacklist {name}': 'Blacklist {name}',
    'Blacklist {name}?': 'Blacklist {name}?',
    'This candidate will be shut out of every hiring step: they cannot be added to a pipeline, moved, interviewed or given an offer. This cannot be undone from this screen.': 'This candidate will be shut out of every hiring step: they cannot be added to a pipeline, moved, interviewed or given an offer. This cannot be undone from this screen.',
    'Why is this candidate being blacklisted?': 'Why is this candidate being blacklisted?',
    'Blacklisted candidates cannot be moved to another stage.': 'Blacklisted candidates cannot be moved to another stage.',
    'Hiring stages, scrolls sideways': 'Hiring stages, scrolls sideways',
    'This candidate has already applied for this opening.': 'This candidate has already applied for this opening.',
    'A candidate with this email already exists.': 'A candidate with this email already exists.',
    'Could not add this candidate to the pipeline. Try again.': 'Could not add this candidate to the pipeline. Try again.',
    'The application is no longer active.': 'The application is no longer active.',
    'Application created.': 'Application created.',
    'Show earlier stages': 'Show earlier stages',
    'Show later stages': 'Show later stages',
  },
  ar: {
    'Blacklist': 'إدراج في القائمة السوداء',
    'Blacklisted': 'مدرج في القائمة السوداء',
    'Blacklist {name}': 'إدراج {name} في القائمة السوداء',
    'Blacklist {name}?': 'هل تريد إدراج {name} في القائمة السوداء؟',
    'This candidate will be shut out of every hiring step: they cannot be added to a pipeline, moved, interviewed or given an offer. This cannot be undone from this screen.': 'سيُمنع هذا المرشح من جميع خطوات التوظيف: لا يمكن إضافته إلى مسار التوظيف أو نقله أو مقابلته أو منحه عرضًا. لا يمكن التراجع عن ذلك من هذه الشاشة.',
    'Why is this candidate being blacklisted?': 'لماذا يُدرج هذا المرشح في القائمة السوداء؟',
    'Blacklisted candidates cannot be moved to another stage.': 'لا يمكن نقل المرشحين المدرجين في القائمة السوداء إلى مرحلة أخرى.',
    'Hiring stages, scrolls sideways': 'مراحل التوظيف، تُمرَّر أفقيًا',
    'This candidate has already applied for this opening.': 'سبق لهذا المرشح التقدم لهذه الوظيفة.',
    'A candidate with this email already exists.': 'يوجد مرشح بهذا البريد الإلكتروني بالفعل.',
    'Could not add this candidate to the pipeline. Try again.': 'تعذرت إضافة هذا المرشح إلى المسار. حاول مرة أخرى.',
    'The application is no longer active.': 'لم يعد طلب التوظيف نشطًا.',
    'Application created.': 'أُنشئ الطلب.',
    'Show earlier stages': 'عرض المراحل السابقة',
    'Show later stages': 'عرض المراحل التالية',
  },
};
