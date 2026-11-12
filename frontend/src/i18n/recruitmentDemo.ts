/**
 * Recruitment screen wording found in the 2026-11 demo rehearsal: the application drawer's tabs, the
 * pipeline stage and timeline-event names, interview and offer list labels, Saudi-appropriate example
 * text, and the onboarding pickers. Stage and event names are enum.* keys (see ./enumLabel).
 * Terms follow ./offerLifecycle: Offer = العرض, Interview = المقابلة, Status = الحالة.
 */
const ar: Record<string, string> = {
  // Application drawer tabs.
  'Timeline': 'الجدول الزمني',
  'Interviews': 'المقابلات',
  'Offer': 'العرض',
  // Pipeline stages.
  'enum.RecruitmentStage.Applied': 'تم التقديم',
  'enum.RecruitmentStage.Screening': 'الفرز',
  'enum.RecruitmentStage.Assessment': 'التقييم',
  'enum.RecruitmentStage.Interview': 'المقابلة',
  'enum.RecruitmentStage.Offer': 'العرض',
  'enum.RecruitmentStage.Hired': 'تم التعيين',
  // Timeline events.
  'enum.RecruitmentEvent.ApplicationCreated': 'تم إنشاء الطلب',
  'enum.RecruitmentEvent.StageAdvanced': 'انتقال إلى مرحلة جديدة',
  'enum.RecruitmentEvent.Rejected': 'تم رفض الطلب',
  'enum.RecruitmentEvent.NoteAdded': 'تمت إضافة ملاحظة',
  'enum.RecruitmentEvent.InterviewScheduled': 'تمت جدولة مقابلة',
  'enum.RecruitmentEvent.FeedbackRecorded': 'تم تسجيل التقييم',
  'enum.RecruitmentEvent.OfferGenerated': 'تم إنشاء العرض',
  'enum.RecruitmentEvent.OfferSent': 'تم إرسال العرض',
  'enum.RecruitmentEvent.OfferAccepted': 'تم قبول العرض',
  'enum.RecruitmentEvent.OfferDeclined': 'تم رفض العرض',
  'enum.RecruitmentEvent.OnboardingStarted': 'بدأت إجراءات التعيين',
  'Original': 'الأصلي',
  // Interviews list.
  'Feedback notes': 'ملاحظات التقييم',
  // Example text (Saudi-appropriate).
  'For example: customer service, Excel, Arabic and English': 'مثال: خدمة العملاء، إكسل، العربية والإنجليزية',
  'For example: Riyadh, Saudi Arabia': 'مثال: الرياض، المملكة العربية السعودية',
  '5+ years of experience\nBachelor’s degree in a relevant field…': 'خبرة لا تقل عن 5 سنوات\nشهادة جامعية في تخصص ذي صلة…',
  'For example: Saudi': 'مثال: سعودي',
  // Onboarding: choose people instead of pasting ids.
  'Employee (optional)': 'الموظف (اختياري)',
  'Hired application (optional)': 'طلب موظف تم تعيينه (اختياري)',
  'Choose a hired candidate…': 'اختر مرشحًا تم تعيينه…',
  'Nobody has been hired yet': 'لم يتم تعيين أي مرشح بعد',
  'The hired candidates could not be loaded.': 'تعذر تحميل المرشحين الذين تم تعيينهم.',
  'Select a checklist and choose an employee or a hired candidate.': 'اختر قائمة مهام ثم اختر موظفًا أو مرشحًا تم تعيينه.',
};

export const recruitmentDemo = { ar, en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) as Record<string, string> };
