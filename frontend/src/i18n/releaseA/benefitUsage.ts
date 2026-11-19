import type { ReleaseADict } from './types';

/**
 * Benefit utilisation strings: the HR/Finance "Benefit usage" screen, the request dialog and the employee's "My benefits — used and
 * left" card. It works like a loan request (Pending → Finance approves or rejects; the requester may withdraw). Add keys here, never in
 * translations.ts. Keys already translated elsewhere (Cancel, Approve, Waiting for Finance…) are reused, not repeated.
 */
const pairs: Array<[string, string]> = [
  // Screen and menu
  ['Benefit usage', 'استخدام المزايا'],
  ['See what each employee’s fixed benefits allow this period, what is used and what is left. HR requests a use; Finance approves it.',
    'اطّلع على ما تتيحه مزايا كل موظف الثابتة في هذه الفترة، وما استُخدم منها وما تبقّى. تطلب الموارد البشرية الاستخدام وتعتمده المالية.'],
  ['Education and tickets used and left, per employee: a use is requested and Finance approves it.',
    'التعليم والتذاكر المستخدمة والمتبقية لكل موظف: يُطلب الاستخدام وتعتمده المالية.'],
  ['Benefit usage could not be loaded.', 'تعذّر تحميل استخدام المزايا.'],
  ['Waiting for Finance ({n})', 'بانتظار المالية ({n})'],
  ['Entitlement, used and left', 'المستحق والمستخدم والمتبقي'],
  ['Find an employee or benefit', 'ابحث عن موظف أو ميزة'],
  ['No fixed benefits to show', 'لا توجد مزايا ثابتة لعرضها'],
  ['Tickets, education and other counted benefits appear here once an employee’s package is fixed for the contract year.',
    'تظهر هنا التذاكر والتعليم والمزايا المعدودة الأخرى بعد تثبيت باقة الموظف للسنة التعاقدية.'],
  ['Nothing matches the search', 'لا توجد نتائج مطابقة للبحث'],
  ['Search: {text}', 'البحث: {text}'],
  ['Left to use', 'المتبقي'],
  ['{amount} waiting for Finance', '{amount} بانتظار المالية'],
  ['{name}: {left} left of {cap}', '{name}: متبقٍّ {left} من {cap}'],
  ['{name} (employee)', '{name} (الموظف)'],
  ['Not confirmed against the contract', 'لم يُطابَق مع العقد بعد'],
  ['Request a benefit for {name}', 'طلب ميزة لـ {name}'],
  ['Request use', 'طلب استخدام'],
  ['Decided requests', 'الطلبات المبتوت فيها'],
  ['Requested for {name}. It now waits for Finance.', 'تم الطلب لـ {name}، وهو الآن بانتظار اعتماد المالية.'],

  // A request
  ['For {name}', 'لـ {name}'],
  ['Academic year {year}', 'العام الدراسي {year}'],
  ['Evidence: {ref}', 'المستند: {ref}'],
  ['Requested by a former user', 'طلبه مستخدم سابق'],
  ['Reason: {text}', 'السبب: {text}'],
  ['Approved.', 'تم الاعتماد.'],
  ['Rejected.', 'تم الرفض.'],
  ['Withdrawn. The entitlement is available again.', 'تم سحب الطلب، وعاد الاستحقاق متاحاً.'],
  ['Say why.', 'اذكر السبب.'],
  ['Finance decides this. Whoever requested it, and the employee it is for, cannot.',
    'تقرّر المالية هذا الطلب، ولا يقرّره من قدّمه ولا الموظف المعني به.'],

  // Request dialog
  ['Request a benefit', 'طلب ميزة'],
  ['Request a benefit for an employee', 'طلب ميزة لموظف'],
  ['Send request', 'إرسال الطلب'],
  ['Give the evidence for what was used. Finance approves the request next. Nothing is paid from here.',
    'أرفق مرجع ما استُخدم، ثم تعتمد المالية الطلب. لا يُصرف أي مبلغ من هنا.'],
  ['Nothing can be requested now: every benefit is used, not yet confirmed, or its period has ended.',
    'لا يمكن طلب شيء الآن: كل ميزة إما مستخدمة بالكامل أو لم تُطابَق بعد أو انتهت فترتها.'],
  ['Choose a benefit', 'اختر ميزة'],
  ['Left this period ({from} to {to}): {left} of {total}', 'المتبقي لهذه الفترة (من {from} إلى {to}): {left} من {total}'],
  ['Choose a child', 'اختر الابن أو الابنة'],
  ['{name} — {left} left', '{name} — متبقٍّ {left}'],
  ['Each child has the per-child amount a year. Children beyond the grade’s child limit cannot be named until a named child’s request is withdrawn.',
    'لكل ابن أو ابنة المبلغ المحدد سنوياً. لا يمكن تسمية أبناء يتجاوزون حد الدرجة إلا بعد سحب طلب أحد الأبناء المسمَّين.'],
  ['Each traveller has their own tickets a period. Family members beyond the grade’s limit cannot be named this period.',
    'لكل مسافر تذاكره الخاصة في الفترة. لا يمكن تسمية أفراد من العائلة يتجاوزون حد الدرجة في هذه الفترة.'],
  ['The invoice, receipt or booking number you checked. Finance sees it when deciding.',
    'رقم الفاتورة أو الإيصال أو الحجز، وتطّلع عليه المالية عند اتخاذ القرار.'],
  ['Travel date', 'تاريخ السفر'],
  ['Claim date', 'تاريخ المطالبة'],
  ['Number of tickets or items', 'عدد التذاكر أو الوحدات'],
  ['Academic year', 'العام الدراسي'],
  ['For example 2026/2027', 'مثال: 2026/2027'],
  ['Evidence reference', 'مرجع المستند'],
  ['Invoice, receipt or booking number', 'رقم الفاتورة أو الإيصال أو الحجز'],

  // Field checks
  ['Choose a benefit.', 'اختر ميزة.'],
  ['Choose a date inside this benefit period.', 'اختر تاريخاً ضمن فترة هذه الميزة.'],
  ['Enter how many, more than zero.', 'أدخل العدد، على أن يكون أكبر من صفر.'],
  ['Enter an amount more than zero.', 'أدخل مبلغاً أكبر من صفر.'],
  ['Use at most two decimals.', 'استخدم منزلتين عشريتين كحد أقصى.'],
  ['That is more than is left for this period.', 'هذا أكثر من المتبقي لهذه الفترة.'],
  ['Give the invoice, receipt or booking reference you checked.', 'أدخل مرجع الفاتورة أو الإيصال أو الحجز.'],
  ['Give the academic year the fees are for.', 'أدخل العام الدراسي الذي تخصه الرسوم.'],

  // Refusals
  ['You do not have the authority for this step.', 'ليست لديك صلاحية هذه الخطوة.'],
  ['This record no longer exists. Refresh the page.', 'لم يعد هذا السجل موجوداً. حدّث الصفحة.'],
  ['This employee or record is outside your companies.', 'هذا الموظف أو السجل خارج الشركات المسموح لك بها.'],
  ['The employee has no fixed entitlement to this benefit on that date.', 'لا يوجد للموظف استحقاق ثابت لهذه الميزة في ذلك التاريخ.'],
  ['This benefit is not used up by a count or an amount.', 'هذه الميزة لا تُستهلك بعدد أو مبلغ.'],
  ['HR must confirm this benefit against the signed contract before it can be requested.',
    'يجب أن تطابق الموارد البشرية هذه الميزة مع العقد الموقّع قبل طلبها.'],
  ['That benefit period has ended. Choose a date in an open period.', 'انتهت فترة هذه الميزة. اختر تاريخاً ضمن فترة مفتوحة.'],
  ['Request a number for a counted benefit and an amount for money, more than zero.',
    'اطلب عدداً للميزة المعدودة ومبلغاً للميزة النقدية، على أن يكون أكبر من صفر.'],
  ['Request the amount in the company currency.', 'اطلب المبلغ بعملة الشركة.'],
  ['You requested this, so another person must decide it.', 'أنت من قدّم هذا الطلب، لذا يجب أن يقرّره شخص آخر.'],
  ['This request was already decided. Refresh the page.', 'سبق البتّ في هذا الطلب. حدّث الصفحة.'],
  ['This form was already sent with different details. Close it and start again.', 'سبق إرسال هذا النموذج ببيانات مختلفة. أغلقه وابدأ من جديد.'],
  ['Choose a family member this benefit covers.', 'اختر أحد أفراد العائلة المشمولين بهذه الميزة.'],
  ['The grade’s limit of dependants for this period is reached. Use a child or traveller already named.',
    'بلغ عدد المعالين حد الدرجة لهذه الفترة. اختر ابناً أو مسافراً سبقت تسميته.'],
  ['One of the texts is too long.', 'أحد النصوص أطول من المسموح.'],
  ['This is your own benefit, so another person must act on it.', 'هذه ميزتك أنت، لذا يجب أن يتولاها شخص آخر.'],

  // Employee card
  ['My benefits — used and left', 'مزاياي — المستخدم والمتبقي'],
  ['Your benefit usage could not be loaded.', 'تعذّر تحميل استخدام مزاياك.'],
  ['Resets on {date}', 'تتجدد في {date}'],
  ['You', 'أنت'],
  ['Your request was sent. Finance decides it next.', 'تم إرسال طلبك، وستبتّ فيه المالية.'],
];

export const benefitUsage: ReleaseADict = {
  en: Object.fromEntries(pairs.map(([en]) => [en, en])),
  ar: Object.fromEntries(pairs),
};
