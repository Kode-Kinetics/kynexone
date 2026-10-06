import type { ReleaseADict } from './types';

/**
 * Employee package strings, HR panel and employee app (R2). Owned by slice R2; R0 seeded it. Add keys here, never in
 * translations.ts. {placeholders} are filled after translation (packageFormat.fill), so each language orders its own words.
 * Component names (Housing allowance, Medical insurance…) are NOT here: they come from the server catalogue in EN and AR.
 */
const pairs: Array<[string, string]> = [
  ['The employee’s package for the current contract year appears here.', 'تظهر هنا باقة الموظف للسنة التعاقدية الحالية.'],
  ['Your pay, your contract benefits for this contract year, and the facilities your grade offers.', 'راتبك ومزايا عقدك لهذه السنة التعاقدية والتسهيلات التي توفرها درجتك.'],

  // Groups and headers
  ['Pay (in the Qiwa contract)', 'الأجر (كما في عقد قوى)'],
  ['Pay (in your Qiwa contract)', 'الأجر (كما في عقدك على قوى)'],
  ['Contract benefits — fixed until {date}', 'مزايا العقد — ثابتة حتى {date}'],
  ['Contract benefits — fixed for this contract', 'مزايا العقد — ثابتة لهذا العقد'],
  ['Contract benefits — not yet fixed for this contract year', 'مزايا العقد — لم تُثبَّت بعد لهذه السنة التعاقدية'],
  ['Facilities — current policy', 'التسهيلات — حسب السياسة الحالية'],
  ['Package for this contract year', 'الباقة لهذه السنة التعاقدية'],
  ['Grade {grade}', 'الدرجة {grade}'],
  ['Contract {number}: {from} to {to}', 'العقد {number}: من {from} إلى {to}'],
  ['As of {date}', 'بتاريخ {date}'],
  ['no end date', 'دون تاريخ انتهاء'],
  ['Dependants on file: {n}', 'المعالون المسجلون: {n}'],
  ['Your contract renewal review opens on {date}.', 'تبدأ مراجعة تجديد عقدك في {date}.'],

  // Values
  ['Provided in kind', 'يُقدَّم عيناً'],
  ['{rate}% of basic = {amount} a month', '{rate}% من الراتب الأساسي = {amount} شهرياً'],
  ['{rate}% of basic salary', '{rate}% من الراتب الأساسي'],
  ['{amount} a month', '{amount} شهرياً'],
  ['{amount} a day', '{amount} يومياً'],
  ['{amount} a year', '{amount} سنوياً'],
  ['{amount} a child a year', '{amount} لكل طفل سنوياً'],
  ['up to {n} children', 'حتى {n} أطفال'],
  ['Up to {amount}', 'حتى {amount}'],
  ['Up to {amount} ({formula})', 'حتى {amount} ({formula})'],
  ['housing allowance', 'بدل السكن'],
  ['basic salary', 'الراتب الأساسي'],
  ['gross salary', 'إجمالي الراتب'],
  ['{n} × {class} ticket {per}', '{n} × تذكرة {class} {per}'],
  ['a year', 'سنوياً'],
  ['per contract year', 'لكل سنة تعاقدية'],
  ['Economy class', 'الدرجة السياحية'],
  ['Business class', 'درجة رجال الأعمال'],
  ['CCHI basic class', 'الفئة الأساسية لمجلس الضمان الصحي'],
  ['VIP class', 'فئة كبار الشخصيات (VIP)'],
  ['Class {tier}', 'الفئة {tier}'],
  ['Included', 'مشمولة'],
  ['Not included', 'غير مشمولة'],
  ['Employee only', 'للموظف فقط'],
  ['Employee + 1 dependant', 'الموظف + معال واحد'],
  ['Employee + {n} dependants', 'الموظف + {n} من المعالين'],
  ['Employee (no dependants on file in scope)', 'الموظف (لا يوجد معالون مسجلون مشمولون)'],
  ['Reviewed at renewal', 'يُراجَع عند التجديد'],

  // Reasons
  ['Your company does not offer this benefit.', 'لا تقدّم شركتك هذه الميزة.'],
  ['Not included for this grade.', 'غير مشمولة في هذه الدرجة.'],
  ['Applies once the required months of service are completed.', 'تُطبَّق بعد إكمال مدة الخدمة المطلوبة.'],
  ['Applies once probation ends.', 'تُطبَّق بعد انتهاء فترة التجربة.'],
  ['Limited to another nationality group, under a recorded legal basis.', 'مقصورة على فئة جنسية أخرى وفق أساس نظامي مسجّل.'],
  ['Housing is provided in kind, so there is no housing allowance to advance against.', 'السكن مقدَّم عيناً، فلا يوجد بدل سكن تُصرف السلفة على أساسه.'],
  ['No salary is on file yet.', 'لا يوجد راتب مسجّل بعد.'],
  ['Not set for this grade yet. HR has a gap to fill in the grade table.', 'لم تُحدَّد لهذه الدرجة بعد، وعلى الموارد البشرية استكمالها في جدول الدرجات.'],
  ['No grade is recorded, so the grade standard cannot be shown.', 'لا توجد درجة مسجّلة، لذلك لا يمكن عرض معيار الدرجة.'],
  ['The salary gives neither a housing allowance nor housing in kind (Article 61).', 'لا يتضمن الراتب بدل سكن ولا سكناً عينياً (المادة 61).'],
  ['The salary gives neither a transport allowance nor transport in kind (Article 61).', 'لا يتضمن الراتب بدل نقل ولا نقلاً عينياً (المادة 61).'],
  ['This item is not available right now.', 'هذا البند غير متاح حالياً.'],

  // Why this value?
  ['Why this value?', 'لماذا هذه القيمة؟'],
  ['Paid with your salary from {date}, as in your Qiwa contract.', 'يُصرف مع راتبك منذ {date} كما في عقدك على قوى.'],
  ['Fixed in your contract from {from} to {to}. Changes to the grade table do not alter it.', 'مثبتة في عقدك من {from} إلى {to}، ولا تغيّرها التعديلات على جدول الدرجات.'],
  ['the end of the contract', 'نهاية العقد'],
  ['The standard for your grade ({grade}), in force since {date}. Not yet fixed in your contract.', 'معيار درجتك ({grade}) الساري منذ {date}، ولم يُثبَّت في عقدك بعد.'],
  ['Current company policy for your grade ({grade}), in force since {date}. It can change.', 'سياسة الشركة الحالية لدرجتك ({grade}) السارية منذ {date}، وقد تتغير.'],
  ['Your company sets its own value for this grade.', 'تحدد شركتك قيمة خاصة بها لهذه الدرجة.'],
  ['The same for every company in the group.', 'القيمة نفسها لجميع شركات المجموعة.'],
  ['From the salary row in force since {date} (the Qiwa contract wage).', 'من سجل الراتب الساري منذ {date} (أجر عقد قوى).'],
  ['Grade standard for {grade}: {value}.', 'معيار الدرجة {grade}: {value}.'],
  ['Fixed for contract {number} from {from} to {to}.', 'مثبتة للعقد {number} من {from} إلى {to}.'],
  ['Copied from the grade cell for {grade}, in force since {date}.', 'منسوخة من خانة الدرجة {grade} السارية منذ {date}.'],
  ['Loaded from the grade table and not yet confirmed against the signed contract.', 'حُمِّلت من جدول الدرجات ولم تُطابَق بعد مع العقد الموقّع.'],
  ['Grade cell for {grade}, in force since {date}. Not yet fixed for this contract year.', 'خانة الدرجة {grade} السارية منذ {date}، ولم تُثبَّت بعد لهذه السنة التعاقدية.'],
  ['Current policy: the grade cell for {grade}, in force since {date}.', 'السياسة الحالية: خانة الدرجة {grade} السارية منذ {date}.'],
  ['{company} sets its own value for this grade.', 'تحدد {company} قيمة خاصة بها لهذه الدرجة.'],
  ['Applies after {n} months of service.', 'تُطبَّق بعد {n} شهراً من الخدمة.'],
  ['Limited to Saudi employees. Legal basis: {basis}', 'مقصورة على الموظفين السعوديين. الأساس النظامي: {basis}'],
  ['Limited to non-Saudi employees. Legal basis: {basis}', 'مقصورة على الموظفين غير السعوديين. الأساس النظامي: {basis}'],
  ['The grade standard is different now. This is reviewed at renewal; nothing changes mid-year.', 'تغيّر معيار الدرجة، ويُراجَع ذلك عند التجديد دون أي تغيير خلال السنة.'],

  // HR actions and states
  ['Fix the package for this contract year', 'تثبيت الباقة لهذه السنة التعاقدية'],
  ['Fixing…', 'جارٍ التثبيت…'],
  ['Later changes to the grade table will not alter this year’s benefits.', 'لن تغيّر التعديلات اللاحقة على جدول الدرجات مزايا هذه السنة.'],
  ['Confirm it matches the signed contract', 'تأكيد مطابقتها للعقد الموقّع'],
  ['These benefits were loaded from the grade table. Check them against the signed contract, then confirm.', 'حُمِّلت هذه المزايا من جدول الدرجات. راجعها مقابل العقد الموقّع ثم أكّدها.'],
  ['No active contract term, so nothing is fixed yet. The grade standard is shown.', 'لا يوجد عقد ساري، لذلك لم يُثبَّت شيء بعد ويظهر معيار الدرجة.'],
  ['This contract year was already fixed.', 'هذه السنة التعاقدية مثبتة مسبقاً.'],
  ['The package is fixed for this contract year.', 'تم تثبيت الباقة لهذه السنة التعاقدية.'],
  ['Nothing to fix: the grade has no contract benefits yet.', 'لا شيء لتثبيته: لا توجد مزايا عقد لهذه الدرجة بعد.'],
  ['The package is confirmed against the signed contract.', 'تم تأكيد مطابقة الباقة للعقد الموقّع.'],
  ['The package could not be changed.', 'تعذّر تعديل الباقة.'],
  ['The package could not be loaded.', 'تعذّر تحميل الباقة.'],
  ['Load the package again', 'إعادة تحميل الباقة'],
  ['Loading the package', 'جارٍ تحميل الباقة'],
  ['You do not have access to this employee’s package.', 'لا تملك صلاحية الاطلاع على باقة هذا الموظف.'],

  // Employee app
  ['Your package could not be loaded.', 'تعذّر تحميل باقتك.'],
  ['Load my package again', 'إعادة تحميل باقتي'],
  ['Your login is not linked to an employee record yet. Ask HR to link it.', 'حسابك غير مرتبط بسجل موظف بعد. اطلب من الموارد البشرية ربطه.'],
];

export const packageStrings: ReleaseADict = {
  en: Object.fromEntries(pairs.map(([en]) => [en, en])),
  ar: Object.fromEntries(pairs),
};
