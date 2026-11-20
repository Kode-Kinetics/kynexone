/**
 * Strings added by the recruitment walkthrough fixes, region C of views/RecruitmentPage.tsx (one file per agent, so no two edits collide).
 * Spread into the en and ar dictionaries in translations.ts; add keys here, never there. Keys are the English text.
 */
const pairs: Record<string, [string, string]> = {
  // Offer workspace: status line, next action, approver picker
  'Draft, original frozen': ['Draft, original frozen', 'مسودة، الأصل مثبت'],
  'Rejected by an approver': ['Rejected by an approver', 'رفضه أحد المعتمدين'],
  'Waiting for the approver to decide': ['Waiting for the approver to decide', 'بانتظار قرار المعتمِد'],
  'Someone else has to send this offer': ['Someone else has to send this offer', 'يجب أن يرسل هذا العرض شخص آخر'],
  'An approval is already waiting for a decision, so nobody else can be asked now. Wait for the decision, or create a revision to start again.': [
    'An approval is already waiting for a decision, so nobody else can be asked now. Wait for the decision, or create a revision to start again.',
    'هناك موافقة بانتظار القرار، لذلك لا يمكن طلب الموافقة من أحد آخر الآن. انتظر القرار أو أنشئ مراجعة للبدء من جديد.'],
  // Send dialog
  'How the offer reaches the candidate': ['How the offer reaches the candidate', 'كيف يصل العرض إلى المرشح'],
  'Email it from here': ['Email it from here', 'إرساله بالبريد من هنا'],
  'I already sent it myself': ['I already sent it myself', 'أرسلته بنفسي بالفعل'],
  'The offer is not issued until you press Record external sending. Use this after you have sent the original letter yourself, by email, in person or another way, and note when and how it was sent.': [
    'The offer is not issued until you press Record external sending. Use this after you have sent the original letter yourself, by email, in person or another way, and note when and how it was sent.',
    'لا يُصدر العرض إلا عند الضغط على تسجيل إرسال خارجي. استخدم هذا بعد أن ترسل الخطاب الأصلي بنفسك، بالبريد أو حضوريًا أو بطريقة أخرى، ودوّن متى وكيف أُرسل.'],
  'Because emails are only captured for testing here, sending one does not issue the offer. Send the letter yourself, then choose "I already sent it myself" and press Record external sending.': [
    'Because emails are only captured for testing here, sending one does not issue the offer. Send the letter yourself, then choose "I already sent it myself" and press Record external sending.',
    'بما أن رسائل البريد هنا تُلتقط للاختبار فقط، فإن إرسال رسالة لا يُصدر العرض. أرسل الخطاب بنفسك، ثم اختر "أرسلته بنفسي بالفعل" واضغط تسجيل إرسال خارجي.'],
  // Interviews
  'Cancel this interview?': ['Cancel this interview?', 'إلغاء هذه المقابلة؟'],
  'The {interviewType} interview with {name} on {dateTime} will be marked as cancelled. You can schedule a new one afterwards.': [
    'The {interviewType} interview with {name} on {dateTime} will be marked as cancelled. You can schedule a new one afterwards.',
    'ستُسجَّل مقابلة {interviewType} مع {name} بتاريخ {dateTime} على أنها ملغاة. يمكنك جدولة مقابلة جديدة بعد ذلك.'],
  'Cancel interview': ['Cancel interview', 'إلغاء المقابلة'],
  'That start time has already passed. Choose a date and time that are still ahead.': ['That start time has already passed. Choose a date and time that are still ahead.', 'وقت البدء هذا قد مضى. اختر تاريخًا ووقتًا لم يحينا بعد.'],
  'Panel feedback': ['Panel feedback', 'ملاحظات اللجنة'],
  'Hide panel feedback': ['Hide panel feedback', 'إخفاء ملاحظات اللجنة'],
  'Could not load the panel feedback.': ['Could not load the panel feedback.', 'تعذر تحميل ملاحظات اللجنة.'],
  'The panel has not submitted any feedback.': ['The panel has not submitted any feedback.', 'لم يقدم أحد من اللجنة ملاحظات بعد.'],
  // Line manager's interview screen
  'Your feedback on {name} was saved. HR can now see it on the interview.': ['Your feedback on {name} was saved. HR can now see it on the interview.', 'تم حفظ ملاحظاتك عن {name}. يمكن للموارد البشرية الآن الاطلاع عليها في المقابلة.'],
  'Add more feedback': ['Add more feedback', 'إضافة ملاحظات أخرى'],
  'Your feedback': ['Your feedback', 'ملاحظاتك'],
  'enum.InterviewRecommendation.Proceed': ['Proceed', 'المتابعة'],
  // New-hire compensation handoff
  'Needs review': ['Needs review', 'تحتاج إلى مراجعة'],
  'Not applicable': ['Not applicable', 'غير منطبق'],
  'Old offer details are not available': ['Old offer details are not available', 'تفاصيل العرض القديم غير متاحة'],
  'Confirmed placement of an older offer': ['Confirmed placement of an older offer', 'تعيين مؤكد لعرض أقدم'],
  'Placement of an older offer must be set explicitly': ['Placement of an older offer must be set explicitly', 'يجب تحديد تعيين العرض الأقدم صراحةً'],
  'Original company is missing, not supported': ['Original company is missing, not supported', 'الشركة الأصلية مفقودة وغير مدعومة'],
  'Named process': ['Named process', 'إجراء مسمى'],
  'Offer-only benefit': ['Offer-only benefit', 'منفعة في العرض فقط'],
  'One time': ['One time', 'لمرة واحدة'],
  'A company outside your list': ['A company outside your list', 'شركة خارج قائمتك'],
  // Reasons the offer API gives when the terms cannot be edited (shown under the Terms tab)
  'Create new revision to change submitted terms.': ['Create new revision to change submitted terms.', 'أنشئ مراجعة جديدة لتغيير الشروط المرسلة.'],
  'Managed offer lifecycle disabled.': ['Managed offer lifecycle disabled.', 'دورة العرض المُدارة معطلة.'],
  'Cancel or reconcile unresolved delivery first.': ['Cancel or reconcile unresolved delivery first.', 'ألغِ أو سوِّ حالة الإرسال غير المحسومة أولًا.'],
  'The application is no longer active.': ['The application is no longer active.', 'لم يعد الطلب نشطًا.'],
  // Assessment result box
  'Marks (0–{max})': ['Marks (0–{max})', 'الدرجات (0–{max})'],
  'Score % (0–100)': ['Score % (0–100)', 'النسبة % (0–100)'],
  '{name} result: {scale}': ['{name} result: {scale}', 'نتيجة {name}: {scale}'],
  // Next action: who has to do what, when the viewer cannot
  'An HR Manager or Admin has to request approval': ['An HR Manager or Admin has to request approval', 'يجب أن يطلب مدير موارد بشرية أو مدير نظام الاعتماد'],
  'Someone who can create a revision has to take this forward': ['Someone who can create a revision has to take this forward', 'يجب أن يتابع الأمر شخص يملك صلاحية إنشاء مراجعة'],
  // Send dialog sender
  'Sent from your company email': ['Sent from your company email', 'يُرسل من بريد شركتك'],
  'Sent from the platform email': ['Sent from the platform email', 'يُرسل من بريد المنصة'],
  'Technical details': ['Technical details', 'تفاصيل تقنية'],
  'Sender check code': ['Sender check code', 'رمز التحقق من المرسل'],
  // Assessments
  'Not scored yet': ['Not scored yet', 'لم يُقيَّم بعد'],
};

export const recruitmentFixesC: { en: Record<string, string>; ar: Record<string, string> } = {
  en: Object.fromEntries(Object.entries(pairs).map(([k, v]) => [k, v[0]])),
  ar: Object.fromEntries(Object.entries(pairs).map(([k, v]) => [k, v[1]])),
};
