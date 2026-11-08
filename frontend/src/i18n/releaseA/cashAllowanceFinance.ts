import type { ReleaseADict } from './types';

/**
 * Wave D of the monthly cash allowances: Finance confirms and corrects an allowance in Benefits by grade, the end of service category,
 * a pay reduction needing the employee's written agreement (salary forms and Finance approval), and the payroll readiness list of
 * allowance lines that cannot be used. Whole sentences with {placeholders} so Arabic orders its own words.
 */
const pairs: Array<[string, string]> = [
  // Benefits by grade: waiting for Finance, confirm, correct
  ['Waiting for Finance', 'بانتظار المالية'],
  ['Finance must confirm it before it can be used.', 'يجب أن تؤكده المالية قبل استخدامه.'],
  ['Confirm {benefit}', 'تأكيد {benefit}'],
  ['Correct {benefit}', 'تصحيح {benefit}'],
  ['Correct GL account or end of service for {benefit}', 'تصحيح حساب الأستاذ العام أو نهاية الخدمة لـ {benefit}'],
  ['Correct GL / end of service', 'تصحيح الحساب / نهاية الخدمة'],
  ['{count, plural, one {Nothing can be set for {benefits} until Finance confirms it.} other {Nothing can be set for {benefits} until Finance confirms them.}}',
    '{count, plural, zero {لا يمكن تحديد شيء لـ {benefits} حتى تؤكده المالية.} one {لا يمكن تحديد شيء لـ {benefits} حتى تؤكده المالية.} two {لا يمكن تحديد شيء لـ {benefits} حتى تؤكدهما المالية.} few {لا يمكن تحديد شيء لـ {benefits} حتى تؤكدها المالية.} many {لا يمكن تحديد شيء لـ {benefits} حتى تؤكدها المالية.} other {لا يمكن تحديد شيء لـ {benefits} حتى تؤكدها المالية.}}'],
  ['Added. Finance must confirm the GL account and end-of-service setting before it can be used.', 'تمت الإضافة. يجب أن تؤكد المالية حساب الأستاذ العام وإعداد نهاية الخدمة قبل استخدامه.'],
  ['{benefit} was confirmed. Set its value for each grade, then publish.', 'تم تأكيد {benefit}. حدّد قيمته لكل درجة ثم انشر.'],
  ['{benefit} was changed from {month}.', 'تم تغيير {benefit} اعتباراً من {month}.'],
  ['{benefit} was changed.', 'تم تغيير {benefit}.'],
  ['These draft runs were calculated with the old setting; process them again: {months}.', 'حُسبت مسودات التشغيل هذه بالإعداد القديم؛ أعد معالجتها: {months}.'],
  ['Worth a second look', 'يستحق مراجعة ثانية'],
  ['This looks like a reimbursement (travel, phone, fuel, expenses). A reimbursement usually does not count as wage for end of service. Check that it should be included.',
    'يبدو أن هذا تعويض عن مصروفات (سفر، هاتف، وقود، مصاريف). التعويض لا يُحتسب عادةً من الأجر في مكافأة نهاية الخدمة. تحقّق من أنه يجب احتسابه.'],
  ['A fixed monthly cash payment usually counts as wage for end of service; keep the documented basis.',
    'المبلغ النقدي الشهري الثابت يُحتسب عادةً من الأجر في مكافأة نهاية الخدمة؛ احتفظ بالأساس الموثّق لاستبعاده.'],
  ['Commissions are usually part of the GOSI wage for Saudis; a cash allowance here is never in the GOSI base.',
    'العمولات تكون عادةً جزءاً من الأجر الخاضع للتأمينات الاجتماعية للسعوديين؛ أما البدل النقدي هنا فلا يدخل أبداً في وعاء التأمينات.'],
  ['Unable to confirm the allowance.', 'تعذّر تأكيد البدل.'],
  ['Unable to change the allowance.', 'تعذّر تغيير البدل.'],
  ['Check the GL account and the end of service setting. No grade can be given a value for this allowance, and no salary can carry it, until you confirm.', 'راجع حساب الأستاذ العام وإعداد نهاية الخدمة. لا يمكن إعطاء أي درجة قيمة لهذا البدل، ولا يمكن إضافته إلى أي راتب، حتى تؤكده.'],
  ['The change starts on the first day of the month you choose. A month a company has already approved or locked cannot be changed.', 'يبدأ التغيير في اليوم الأول من الشهر الذي تختاره. لا يمكن تغيير شهر اعتمدته شركة أو أقفلته.'],
  ['Change starts from', 'يبدأ التغيير من'],
  ['Reason for the change (required)', 'سبب التغيير (مطلوب)'],
  ['The earliest month you can change is {month}.', 'أبكر شهر يمكنك تغييره هو {month}.'],
  ['Reload the grid', 'إعادة تحميل الجدول'],
  ['Save the correction', 'حفظ التصحيح'],
  ['You don’t have access to the list of GL accounts, so the account stays as it is.', 'لا تملك صلاحية الاطلاع على قائمة حسابات الأستاذ العام، لذلك يبقى الحساب كما هو.'],

  // End of service category
  ['Non-recurring', 'غير متكرر'],
  ['Choose a reason', 'اختر سبباً'],
  ['Note (required)', 'ملاحظة (مطلوبة)'],
  ['Choose why this allowance is left out of end of service pay.', 'اختر سبب استثناء هذا البدل من مكافأة نهاية الخدمة.'],

  // What Finance's actions can be refused for
  ['Choose the month the change starts from.', 'اختر الشهر الذي يبدأ منه التغيير.'],
  ['A change starts on the first day of a month. Choose a month.', 'يبدأ التغيير في اليوم الأول من الشهر. اختر شهراً.'],
  ['Say why you are changing it.', 'اذكر سبب التغيير.'],
  ['Shorten the reason.', 'اختصر السبب.'],
  ['Change the GL account or the end of service setting, or cancel.', 'غيّر حساب الأستاذ العام أو إعداد نهاية الخدمة، أو ألغِ.'],
  ['Only someone who can reach every company can do this.', 'لا يستطيع القيام بذلك إلا من يملك صلاحية الوصول إلى كل الشركات.'],
  ['Only Finance can do this.', 'لا تستطيع القيام بذلك إلا المالية.'],
  ['You added this allowance, so someone else in Finance must confirm it.', 'أنت من أضاف هذا البدل، لذلك يجب أن يؤكده شخص آخر في المالية.'],
  ['This benefit is not a monthly cash allowance.', 'هذه الميزة ليست بدلاً نقدياً شهرياً.'],
  ['Finance has already confirmed this allowance.', 'أكدت المالية هذا البدل بالفعل.'],
  ['This allowance has not been confirmed yet. Confirm it first.', 'لم يُؤكَّد هذا البدل بعد. أكّده أولاً.'],
  ['A later change to this allowance already exists. Choose a month after it.', 'يوجد تغيير لاحق على هذا البدل. اختر شهراً بعده.'],
  ['A payroll month from that date has already been approved or locked.', 'هناك شهر رواتب من ذلك التاريخ اعتُمد أو أُقفل بالفعل.'],

  // Salary lines on an allowance that is still waiting for Finance
  ['{name} — waiting for Finance', '{name} — بانتظار المالية'],

  // A pay reduction needs the employee's written agreement
  ['This lowers the employee’s pay', 'هذا يخفض راتب الموظف'],
  ['The employee’s written agreement is needed before their pay goes down.', 'يلزم الحصول على موافقة الموظف الخطية قبل أن ينخفض راتبه.'],
  ['{item} goes from {from} to {to} a month.', '{item} ينخفض من {from} إلى {to} شهرياً.'],
  ['{item} is removed. It was {from} a month.', 'تمت إزالة {item}. كان {from} شهرياً.'],
  ['The monthly pay goes from {from} to {to}.', 'ينخفض الراتب الشهري من {from} إلى {to}.'],
  ['Employee’s written agreement reference', 'مرجع موافقة الموظف الخطية'],
  ['For example the signed amendment’s number or the Qiwa request number.', 'مثلاً رقم الملحق الموقّع أو رقم الطلب في منصة قوى.'],
  ['Employee’s written agreement reference: {reference}', 'مرجع موافقة الموظف الخطية: {reference}'],
  ['No written agreement reference was given.', 'لم يُذكر مرجع لموافقة الموظف الخطية.'],
  ['Keep the agreement reference under 200 characters.', 'اجعل مرجع الموافقة أقل من 200 حرف.'],
  ['Enter a real reference: at least 3 characters, including a letter or a number.', 'أدخل مرجعاً حقيقياً: 3 أحرف على الأقل، ويتضمن حرفاً أو رقماً.'],
  ['At least 3 characters, with a letter or a number.', 'لا يقل عن 3 أحرف، ويتضمن حرفاً أو رقماً.'],
  ['{item} goes up from {from} to {to} a month.', '{item} يرتفع من {from} إلى {to} شهرياً.'],
  ['Enter the employee’s written agreement reference before saving.', 'أدخل مرجع موافقة الموظف الخطية قبل الحفظ.'],
  ['Lowering an employee’s pay needs their written agreement; enter its reference.', 'خفض راتب الموظف يحتاج إلى موافقته الخطية؛ أدخل مرجعها.'],
  ['This changes the contract wage. Amend the contract on Qiwa and keep the employee’s acceptance.', 'هذا يغيّر أجر العقد. عدّل العقد في منصة قوى واحتفظ بقبول الموظف.'],
  ['This change lowers the employee’s pay but has no written agreement reference, so it was not approved. Nothing was paid or changed.', 'يخفض هذا التغيير راتب الموظف دون مرجع لموافقته الخطية، لذلك لم يُعتمد. لم يُصرف أو يُغيَّر أي شيء.'],
  ['Reject this change, or ask for it to be proposed again with the reference.', 'ارفض هذا التغيير، أو اطلب اقتراحه من جديد مع المرجع.'],
  ['Monthly pay: {current} now, {proposed} proposed.', 'الراتب الشهري: {current} حالياً، و{proposed} مقترحاً.'],

  // Benefits by grade: the list of allowances waiting for Finance (G-011)
  ['{count, plural, one {# cash allowance is waiting for you to confirm it} other {# cash allowances are waiting for you to confirm them}}',
    '{count, plural, zero {لا توجد بدلات نقدية بانتظار تأكيدك} one {بدل نقدي واحد بانتظار تأكيدك} two {بدلان نقديان بانتظار تأكيدكما} few {# بدلات نقدية بانتظار تأكيدك} many {# بدلًا نقديًا بانتظار تأكيدك} other {# بدل نقدي بانتظار تأكيدك}}'],
  ['Review and confirm', 'مراجعة وتأكيد'],
  ['Review {benefit}, then confirm it', 'راجع {benefit} ثم أكّده'],
  ['Payroll cannot be processed for anyone who has one of these on their salary until it is confirmed.', 'لا يمكن معالجة الرواتب لأي موظف لديه أحد هذه البدلات في راتبه قبل تأكيده.'],

  // Payroll readiness: allowance lines the run cannot use
  ['{count, plural, one {# monthly allowance line can’t be used yet} other {# monthly allowance lines can’t be used yet}}',
    '{count, plural, zero {لا توجد بنود بدلات شهرية لا يمكن استخدامها بعد} one {بند بدل شهري واحد لا يمكن استخدامه بعد} two {بندا بدل شهري لا يمكن استخدامهما بعد} few {# بنود بدلات شهرية لا يمكن استخدامها بعد} many {# بند بدل شهري لا يمكن استخدامها بعد} other {# بند بدل شهري لا يمكن استخدامها بعد}}'],
  ['Payroll is not ready to process until each one is fixed.', 'الرواتب ليست جاهزة للمعالجة حتى يُصلَح كل بند منها.'],
  ['The allowance {name} on this salary is no longer set up in Benefits by grade. Remove it from the salary, or set it up again.', 'البدل {name} على هذا الراتب لم يعد معرّفاً في «المزايا حسب الدرجة». أزله من الراتب، أو عرّفه من جديد.'],
  ['The allowance {name} is waiting for Finance to confirm it in Benefits by grade.', 'البدل {name} بانتظار أن تؤكده المالية في «المزايا حسب الدرجة».'],
  ['The allowance {name} has no GL account yet. Finance sets it in Benefits by grade.', 'البدل {name} ليس له حساب أستاذ عام بعد. تحدده المالية في «المزايا حسب الدرجة».'],
  ['{count} more are not listed. Fix these and refresh to see them.', 'هناك {count} أخرى غير مدرجة. أصلح هذه ثم حدّث الصفحة لرؤيتها.'],
];

export const cashAllowanceFinance: ReleaseADict = {
  en: Object.fromEntries(pairs.map(([en]) => [en, en])),
  ar: Object.fromEntries(pairs),
};
