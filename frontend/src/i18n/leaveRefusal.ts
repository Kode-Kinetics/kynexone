/**
 * Leave refusals and requesting the cancellation of approved leave. Terms follow GLOSSARY.md.
 *
 * The server refuses a leave request with `{ code, message, args }` (backend LeaveRefusalCodes). lib/leaveRefusal.ts maps each
 * code to one of the sentences below, so Arabic users read Arabic. The sentences are neutral about who is asking, because the
 * same refusal reaches an employee in self-service and HR applying for an employee; only the missing-balance sentence has a
 * "your" and a "this employee" form.
 */
const overlap = 'There is already approved or pending leave on some of these dates.';
const notice = 'This leave type needs at least {days, plural, one {# day} other {# days}} of notice. Choose a later start date.';
const insufficient = 'There are not enough days left in this balance for these dates.';
const balanceSelf = 'Your {leaveType} balance for {year} has not been set up yet. Ask HR to set up your {year} leave balance.';
const balanceHr = 'The {leaveType} balance for {year} has not been set up for this employee yet. Set up their {year} leave balance first.';
const probation = 'This leave type is not available during probation.';
const minService = 'This leave type needs at least {months, plural, one {# month} other {# months}} of service, which has not been reached yet.';
const maxConsecutive = 'This leave type cannot be taken for more than {days, plural, one {# day} other {# days}} in a row.';
const minDays = 'A request for this leave type must be for at least {days, plural, one {# day} other {# days}}.';
const maxDays = 'A single request for this leave type can be for at most {days, plural, one {# day} other {# days}}.';
const reason = 'This leave type needs a reason.';
const attachment = 'This leave type needs a supporting document.';
const attachmentMissing = 'The supporting document could not be found. Attach it again.';
const halfDay = 'This leave type cannot be taken as a half day.';
const hourly = 'This leave type cannot be requested in hours.';
const blackout = 'Leave is not allowed on some of these dates. Choose other dates or talk to the approver.';
const unavailable = 'This leave type is not available right now. Choose another or ask HR.';
const statutory = 'This is more than the legal limit for this leave type, which is {days, plural, one {# day} other {# days}}.';
const statutoryAny = 'This is more than the legal limit for this leave type.';
const notEligible = 'The eligibility rules for {leaveType} are not met. Ask HR if this looks wrong.';
const invalidDates = 'These dates are not valid. Check them and try again.';
const refused = 'The request was refused. Check the details or ask HR.';

export const LEAVE_REFUSAL_KEYS = {
  overlap, notice, insufficient, balanceSelf, balanceHr, probation, minService, maxConsecutive, minDays, maxDays, reason,
  attachment, attachmentMissing, halfDay, hourly, blackout, unavailable, statutory, statutoryAny, notEligible, invalidDates, refused,
} as const;

export const leaveRefusal = {
  en: {
    ...Object.fromEntries(Object.values(LEAVE_REFUSAL_KEYS).map((k) => [k, k])),
    'Request cancellation': 'Request cancellation',
    'Request cancellation of this approved leave? Your approver will decide whether it is cancelled, and your balance stays as it is until they do.': 'Request cancellation of this approved leave? Your approver will decide whether it is cancelled, and your balance stays as it is until they do.',
    'Cancellation requested. Your approver will review it.': 'Cancellation requested. Your approver will review it.',
  } as Record<string, string>,
  ar: {
    [overlap]: 'توجد إجازة معتمدة أو بانتظار الاعتماد في بعض هذه التواريخ.',
    [notice]: 'يتطلب هذا النوع من الإجازات إشعارًا مسبقًا لا يقل عن {days, plural, zero {# يوم} one {يوم واحد} two {يومين} few {# أيام} many {# يومًا} other {# يوم}}. اختر تاريخ بداية لاحقًا.',
    [insufficient]: 'رصيد الإجازة من هذا النوع لا يكفي لهذه التواريخ.',
    [balanceSelf]: 'لم يُعدّ رصيد {leaveType} لسنة {year} بعد. اطلب من الموارد البشرية إعداد رصيد إجازتك لسنة {year}.',
    [balanceHr]: 'لم يُعدّ رصيد {leaveType} لسنة {year} لهذا الموظف بعد. أعدّ رصيد إجازته لسنة {year} أولًا.',
    [probation]: 'هذا النوع من الإجازات غير متاح خلال فترة التجربة.',
    [minService]: 'يتطلب هذا النوع من الإجازات مدة خدمة لا تقل عن {months, plural, zero {# شهر} one {شهر واحد} two {شهرين} few {# أشهر} many {# شهرًا} other {# شهر}}، ولم تكتمل هذه المدة بعد.',
    [maxConsecutive]: 'الحد الأقصى للإجازة المتتالية من هذا النوع هو {days, plural, zero {# يوم} one {يوم واحد} two {يومان} few {# أيام} many {# يومًا} other {# يوم}}.',
    [minDays]: 'يجب ألا تقل مدة طلب هذا النوع من الإجازات عن {days, plural, zero {# يوم} one {يوم واحد} two {يومين} few {# أيام} many {# يومًا} other {# يوم}}.',
    [maxDays]: 'يجب ألا تزيد مدة الطلب الواحد من هذا النوع من الإجازات عن {days, plural, zero {# يوم} one {يوم واحد} two {يومين} few {# أيام} many {# يومًا} other {# يوم}}.',
    [reason]: 'يتطلب هذا النوع من الإجازات ذكر السبب.',
    [attachment]: 'يتطلب هذا النوع من الإجازات مستندًا داعمًا.',
    [attachmentMissing]: 'تعذّر العثور على المستند الداعم. أرفقه مرة أخرى.',
    [halfDay]: 'لا يمكن أخذ هذا النوع من الإجازات نصف يوم.',
    [hourly]: 'لا يمكن طلب هذا النوع من الإجازات بالساعات.',
    [blackout]: 'الإجازات غير مسموح بها في بعض هذه التواريخ. اختر تواريخ أخرى أو تحدث مع المعتمِد.',
    [unavailable]: 'هذا النوع من الإجازات غير متاح حاليًا. اختر نوعًا آخر أو راجع الموارد البشرية.',
    [statutory]: 'الطلب يتجاوز الحد النظامي لهذا النوع من الإجازات، وهو {days, plural, zero {# يوم} one {يوم واحد} two {يومان} few {# أيام} many {# يومًا} other {# يوم}}.',
    [statutoryAny]: 'الطلب يتجاوز الحد النظامي لهذا النوع من الإجازات.',
    [notEligible]: 'لا تنطبق شروط الأهلية لإجازة {leaveType}. راجع الموارد البشرية إن بدا لك ذلك غير صحيح.',
    [invalidDates]: 'التواريخ غير صالحة. راجعها وحاول مرة أخرى.',
    [refused]: 'رُفض الطلب. راجع التفاصيل أو اسأل الموارد البشرية.',
    'Request cancellation': 'طلب الإلغاء',
    'Request cancellation of this approved leave? Your approver will decide whether it is cancelled, and your balance stays as it is until they do.': 'هل تريد طلب إلغاء هذه الإجازة المعتمدة؟ سيقرر المعتمِد ما إذا كانت ستُلغى، ويبقى رصيدك كما هو حتى يقرر.',
    'Cancellation requested. Your approver will review it.': 'تم إرسال طلب الإلغاء. سيراجعه المعتمِد.',
  } as Record<string, string>,
};
