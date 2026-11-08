/**
 * UX foundation: the shared confirmation dialog (three tiers), the refusal pattern (what happened, why, what to do)
 * and the confirmations that replaced native window.confirm / prompt. One concept, one Arabic term (GLOSSARY.md):
 * مسير الرواتب (payroll run), الموظف (employee), إجازة (leave), مسودة (draft), قيد عكسي (contra journal).
 * English is the key; whole sentences with {placeholders}, never fragments.
 */
const ar: Record<string, string> = {
  // Saudi Compliance action centre
  'Open People: incomplete records are marked': 'فتح الموظفين: السجلات الناقصة معلَّمة',
  'Fix': 'معالجة',

  // Empty states
  'Clear filters': 'مسح عوامل التصفية',
  'Access is given by your HR administrator.': 'يمنح مسؤول الموارد البشرية الصلاحية.',

  // The `i` (InfoTip)
  'About {field}': 'حول {field}',
  'More information': 'مزيد من المعلومات',
  'Field key': 'مفتاح الحقل',

  // ConfirmDialog (tiers 2 and 3)
  'What will change': 'ما الذي سيتغير',
  '(required)': '(مطلوب)',
  '(optional)': '(اختياري)',
  'Type {text} to confirm': 'اكتب {text} للتأكيد',
  'Type {text} exactly to continue.': 'اكتب {text} كما هو تمامًا للمتابعة.',
  'Give a reason to continue.': 'اذكر السبب للمتابعة.',
  'Give a reason of at least {count} characters to continue.': 'اذكر سببًا لا يقل عن {count} أحرف للمتابعة.',

  // Refusal pattern
  'Ask your HR administrator to grant you access.': 'اطلب من مسؤول الموارد البشرية منحك الصلاحية.',
  'Check your connection and try again in a moment.': 'تحقق من اتصالك ثم أعد المحاولة بعد قليل.',

  // Employees
  'Merge this record into {target}?': 'هل تريد دمج هذا السجل في {target}؟',
  'This record is closed as a duplicate: its pending approvals are cancelled and its payroll footprint is deactivated. History is kept and linked for audit.':
    'يُغلق هذا السجل بوصفه مكررًا: تُلغى موافقاته المعلقة ويُعطَّل أثره في الرواتب. يبقى السجل التاريخي مرتبطًا للتدقيق.',
  'Merge records': 'دمج السجلين',
  'Your edits to this employee have not been saved and will be lost.': 'لم تُحفظ تعديلاتك على بيانات هذا الموظف وستضيع.',
  'Discard this employee draft?': 'هل تريد تجاهل مسودة هذا الموظف؟',
  'The details you entered have not been saved and will be lost.': 'لم تُحفظ البيانات التي أدخلتها وستضيع.',
  'Delete {name}?': 'هل تريد حذف {name}؟',
  'The record is hidden from all lists. History is kept for audit.': 'يختفي السجل من جميع القوائم، ويبقى سجله التاريخي للتدقيق.',
  'Delete employee': 'حذف الموظف',

  // Payroll
  'Delete this draft payroll run?': 'هل تريد حذف مسودة مسير الرواتب هذه؟',
  'It is permanently removed. Processed or locked runs cannot be deleted; void them instead.': 'يُحذف نهائيًا. لا يمكن حذف المسيرات المعالجة أو المقفلة؛ أبطلها بدلًا من ذلك.',
  'Delete run': 'حذف المسير',
  'Confirm payments outside the bank file': 'تأكيد الدفعات خارج ملف البنك',
  'Confirm that exactly {count, plural, one {# employee is} other {# employees are}} paid outside the bank file?':
    'هل تؤكد أن {count, plural, one {موظفًا واحدًا يُدفع له} two {موظفَين يُدفع لهما} few {# موظفين يُدفع لهم} many {# موظفًا يُدفع لهم} other {# موظف يُدفع لهم}} خارج ملف البنك تحديدًا؟',
  'Confirm paid outside the bank file': 'تأكيد الدفع خارج ملف البنك',
  'Post a contra journal?': 'هل تريد ترحيل قيد عكسي؟',
  'Reversing posts a contra journal that offsets the original entry. The reason you entered is sent with the reversal.':
    'يرحّل العكس قيدًا عكسيًا يقابل القيد الأصلي. يُرسل السبب الذي أدخلته مع العكس.',
  'Post contra journal': 'ترحيل القيد العكسي',
  'Reverse the payment recorded for {code}?': 'هل تريد عكس الدفعة المسجلة للموظف {code}؟',
  "Why is this employee's recorded payment being reversed (for example, a cheque bounced)?": 'لماذا تُعكس الدفعة المسجلة لهذا الموظف (مثلًا: ارتداد الشيك)؟',
  'Reverse payment': 'عكس الدفعة',
  'Cancel this settlement?': 'هل تريد إلغاء هذه التسوية؟',
  'Any required contra journal is posted. The reason you entered is sent with the cancellation.':
    'يُرحَّل أي قيد عكسي لازم. يُرسل السبب الذي أدخلته مع الإلغاء.',
  'Cancel settlement': 'إلغاء التسوية',

  // Leave
  'Withdraw this leave request?': 'هل تريد سحب طلب الإجازة هذا؟',
  'The request is retracted before anyone decides it. You can submit a new one later.': 'يُسحب الطلب قبل أن يبت فيه أحد. يمكنك تقديم طلب جديد لاحقًا.',
  'Withdraw request': 'سحب الطلب',
  'Withdrawing…': 'جارٍ السحب…',
  'Approving…': 'جارٍ الاعتماد…',
  'The leave request was not withdrawn': 'لم يُسحب طلب الإجازة',
  'Reload the list to see its current status.': 'أعد تحميل القائمة لمعرفة حالته الحالية.',
  'The cancellation was not requested': 'لم يُقدَّم طلب الإلغاء',
  'Check the reason and try again.': 'راجع السبب ثم أعد المحاولة.',
  'The leave request was not approved': 'لم يُعتمد طلب الإجازة',
  'The leave request was not rejected': 'لم يُرفض طلب الإجازة',
  'Reload the queue: it may have been decided already.': 'أعد تحميل القائمة: فقد يكون قد بُتَّ فيه بالفعل.',
  'Reload the list: it may have been decided already.': 'أعد تحميل القائمة: فقد يكون قد بُتَّ فيه بالفعل.',
  'The encashment request was not approved': 'لم يُعتمد طلب صرف بدل الإجازة',
  'The encashment request was not rejected': 'لم يُرفض طلب صرف بدل الإجازة',
  'The payroll approval was not recorded': 'لم يُسجَّل اعتماد الرواتب',
  'Check that the run is open and belongs to the same legal entity as the employee.': 'تأكد من أن المسير مفتوح ويتبع الكيان القانوني نفسه للموظف.',
  'Void this encashment?': 'هل تريد إبطال صرف بدل الإجازة هذا؟',
  'The payout is cancelled. If payroll has already processed it, void or reopen the payroll run first.':
    'يُلغى الصرف. إذا عالجه مسير الرواتب بالفعل، فأبطل المسير أو أعد فتحه أولًا.',
  'Void encashment': 'إبطال الصرف',
  'Void reason': 'سبب الإبطال',
  'The encashment was not voided': 'لم يُبطَل صرف بدل الإجازة',
  'If payroll was processed, void or reopen the payroll run first.': 'إذا عولج المسير، فأبطل مسير الرواتب أو أعد فتحه أولًا.',
  'Reject this encashment request?': 'هل تريد رفض طلب صرف بدل الإجازة هذا؟',
  'The request is marked as rejected. Add notes so the employee knows why.': 'يُعلَّم الطلب بأنه مرفوض. أضف ملاحظات ليعرف الموظف السبب.',
  'Reject request': 'رفض الطلب',
  'Rejection notes': 'ملاحظات الرفض',

  // Recruitment
  'Reject this requisition?': 'هل تريد رفض طلب التوظيف هذا؟',
  'The requisition is marked as rejected with the reason you give.': 'يُعلَّم طلب التوظيف بأنه مرفوض مع السبب الذي تذكره.',
  'Reject requisition': 'رفض طلب التوظيف',
  'Rejection reason': 'سبب الرفض',

  // Policy drafts
  'Replace your existing policy form draft?': 'هل تريد استبدال مسودة نموذج السياسة الحالية؟',
  'Replace draft': 'استبدال المسودة',
  'Your unsaved edits in the task form are lost.': 'ستضيع تعديلاتك غير المحفوظة في نموذج المهمة.',
  'Replace values': 'استبدال القيم',

  // Compensation benchmarks
  'Retire this benchmark set?': 'هل تريد إيقاف استخدام مجموعة المقارنة هذه؟',
  'The set is marked as retired.': 'تُعلَّم المجموعة بأنها متوقفة.',
  'Retire set': 'إيقاف المجموعة',
  'Retiring…': 'جارٍ الإيقاف…',
  'The benchmark set was not retired': 'لم تُوقَف مجموعة المقارنة',
  'Reload the page to see its current state.': 'أعد تحميل الصفحة لمعرفة حالتها الحالية.',
  // Failures that are said out loud (what happened, why, what to do)
  'The comment was not sent': 'لم يُرسل التعليق',
  'Your text is still in the box. Try again.': 'نصك ما زال في الخانة. أعد المحاولة.',
  'The status was not changed': 'لم تتغير الحالة',
  'Reload the request to see its current status.': 'أعد تحميل الطلب لمعرفة حالته الحالية.',
  'The request was not submitted': 'لم يُقدَّم الطلب',
  'Your details are still in the form. Try again.': 'بياناتك ما زالت في النموذج. أعد المحاولة.',
  'The insight was not acknowledged': 'لم يُسجَّل الاطلاع على الملاحظة',
  'Try again; it is still listed as new.': 'أعد المحاولة؛ فهي ما زالت مدرجة كجديدة.',
  'The risk scores were not recomputed': 'لم تُعَد حسابات درجات المخاطر',
  'The scores shown are the previous ones. Try again.': 'الدرجات المعروضة هي السابقة. أعد المحاولة.',
  'The shift assignment was not removed': 'لم يُزل تعيين الوردية',
  'It is still on the roster. Try again.': 'ما زال في جدول المناوبات. أعد المحاولة.',
  'The renewal status was not changed': 'لم تتغير حالة التجديد',
  'The employee record was not deleted': 'لم يُحذف سجل الموظف',

  // After a save: what changed, where you are, the next action
  'Request submitted: {subject}': 'تم تقديم الطلب: {subject}',
  'You are on the list of requests.': 'أنت الآن في قائمة الطلبات.',
  'Submit another request': 'تقديم طلب آخر',
  'You are on the draft record, at the step where you stopped.': 'أنت الآن في سجل المسودة، عند الخطوة التي توقفت عندها.',
  'Back to the People list': 'العودة إلى قائمة الموظفين',

  // Disabled with a reason
  'Changes are turned off until this has loaded. If it did not load, use Retry below.': 'التعديلات متوقفة إلى أن يكتمل التحميل. إذا لم يكتمل، فاستخدم إعادة المحاولة أدناه.',
  'Employee limit reached ({used}/{max}). Upgrade your plan to add more employees.': 'تم بلوغ حد الموظفين ({used}/{max}). رقِّ خطتك لإضافة المزيد من الموظفين.',
  'User limit reached ({used}/{max}). Upgrade your plan to add more users.': 'تم بلوغ حد المستخدمين ({used}/{max}). رقِّ خطتك لإضافة المزيد من المستخدمين.',

  // Benefits by grade: one home
  "You can't see Benefits by grade": 'لا يمكنك الاطلاع على المزايا حسب الدرجة',
  'What each grade is entitled to is set there, and access is given by your HR administrator.': 'تُحدَّد هناك استحقاقات كل درجة، ويمنح مسؤول الموارد البشرية صلاحية الاطلاع عليها.',
  'What each grade receives (housing, transport, tickets, medical, education) is set in Benefits by grade, not here. This page is for enrolments and contributions.':
    'ما تحصل عليه كل درجة (السكن والنقل والتذاكر والتأمين الطبي والتعليم) يُحدَّد في المزايا حسب الدرجة وليس هنا. هذه الصفحة للتسجيل في المزايا والاشتراكات.',

  // Page headings that were English-only
  'HR Request Center': 'مركز طلبات الموارد البشرية',
  'Employee service desk: submit, track and resolve HR requests': 'مكتب خدمة الموظفين: قدّم طلبات الموارد البشرية وتابعها وأنجزها',
  'Benefits Administration': 'إدارة المزايا',
  'Document & Compliance Management': 'إدارة المستندات والامتثال',
  'Employee Management': 'إدارة الموظفين',
  'New hires': 'الموظفون الجدد',
  'Recruitment & Requisitions': 'التوظيف وطلبات التوظيف',
  'Saudi Compliance Command Center': 'مركز قيادة الامتثال السعودي',
};

export const uxFoundation = {
  en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) as Record<string, string>,
  ar,
};
