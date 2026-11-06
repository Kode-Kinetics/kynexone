import type { ReleaseADict } from './types';

/**
 * Monthly cash allowances on a salary: the "Other monthly allowances" section of the salary, hire and assign-salary forms, the
 * Finance approval comparison and the employee profile list. Whole sentences with {placeholders} so Arabic orders its own words.
 */
const pairs: Array<[string, string]> = [
  // The section
  ['Other monthly allowances', 'بدلات شهرية أخرى'],
  ['You changed these. Saving sends exactly the lines shown here.', 'غيّرتَ هذه البدلات. سيُرسَل عند الحفظ البنود الظاهرة هنا بالضبط.'],
  ['These stay as they are on the new salary. A percentage is worked out again on the new basic salary.', 'تبقى هذه البدلات كما هي في الراتب الجديد. وتُحسب النسبة المئوية من جديد على الراتب الأساسي الجديد.'],
  ['There are no monthly allowances on this salary. Add one if this employee should get it.', 'لا توجد بدلات شهرية على هذا الراتب. أضف بدلاً إن كان الموظف يستحقه.'],
  ['Choose a grade to fill these in from Benefits by grade, or add them yourself.', 'اختر درجة لتعبئة هذه البدلات من «المزايا حسب الدرجة»، أو أضفها بنفسك.'],
  ['Filled in from Benefits by grade for this grade when you save. Change a line to set your own.', 'تُعبَّأ من «المزايا حسب الدرجة» لهذه الدرجة عند الحفظ. غيّر أي بند لتحدد بنفسك.'],
  ['This grade gives no monthly allowances. Add one if this employee should get it.', 'هذه الدرجة لا تمنح بدلات شهرية. أضف بدلاً إن كان الموظف يستحقه.'],
  ['The allowances already on this employee’s salary stay as they are. Add a line to set the allowances yourself instead.', 'تبقى البدلات الموجودة على راتب هذا الموظف كما هي. أضف بنداً لتحدد البدلات بنفسك بدلاً منها.'],
  ['Loading the monthly allowances…', 'جارٍ تحميل البدلات الشهرية…'],
  ['The monthly allowances could not be loaded.', 'تعذّر تحميل البدلات الشهرية.'],
  ['You can’t add or change monthly allowances here. The ones already on the salary stay as they are.', 'لا يمكنك إضافة البدلات الشهرية أو تغييرها هنا. تبقى البدلات الموجودة على الراتب كما هي.'],
  ['Allowance, line {n}', 'البدل، البند {n}'],
  ['Fixed amount or percentage of basic, line {n}', 'مبلغ ثابت أو نسبة من الأساسي، البند {n}'],
  ['Percentage of basic, line {n}', 'النسبة من الأساسي، البند {n}'],
  ['Monthly amount, line {n}', 'المبلغ الشهري، البند {n}'],
  ['Remove allowance line {n}', 'إزالة بند البدل {n}'],
  ['Choose an allowance', 'اختر بدلاً'],
  ['Worked out once the basic salary is entered.', 'يُحسب بعد إدخال الراتب الأساسي.'],
  ['Total of these allowances: {amount} a month', 'إجمالي هذه البدلات: {amount} شهرياً'],
  ['No monthly allowance has been added in Benefits by grade yet.', 'لم يُضف أي بدل شهري في «المزايا حسب الدرجة» بعد.'],
  ['Add an allowance', 'إضافة بدل'],
  ['Go back to the allowances on the salary now', 'العودة إلى البدلات الموجودة على الراتب حالياً'],
  ['Go back to the grade’s allowances', 'العودة إلى بدلات الدرجة'],
  ['Includes {amount} a month in other monthly allowances.', 'يشمل {amount} شهرياً من البدلات الشهرية الأخرى.'],
  ['Other monthly allowances · Approval', 'بدلات شهرية أخرى · تتطلب موافقة'],
  ['From {before} to {after}', 'من {before} إلى {after}'],
  ['Fix the allowance lines that show a message before saving.', 'صحّح بنود البدلات التي تظهر عليها رسالة قبل الحفظ.'],

  // What stops a line
  ['Choose the allowance for this line.', 'اختر البدل لهذا البند.'],
  ['This allowance is on the salary twice. Keep one line for it.', 'هذا البدل مكرر في الراتب. أبقِ بنداً واحداً له.'],
  ['Enter a monthly amount above zero, with at most two decimal places.', 'أدخل مبلغاً شهرياً أكبر من صفر بخانتين عشريتين على الأكثر.'],
  ['Enter a percentage above 0 and up to 100, with at most two decimal places.', 'أدخل نسبة أكبر من 0 وحتى 100 بخانتين عشريتين على الأكثر.'],
  ['Enter the basic salary before adding monthly allowances.', 'أدخل الراتب الأساسي قبل إضافة البدلات الشهرية.'],
  ['This percentage of the basic salary comes to nothing. Use a fixed amount instead.', 'هذه النسبة من الراتب الأساسي لا تساوي شيئاً. استخدم مبلغاً ثابتاً بدلاً منها.'],

  // Finance approval and profile
  ['Monthly allowances', 'البدلات الشهرية'],
  ['Monthly allowances in this change', 'البدلات الشهرية في هذا التغيير'],
  ['This change does not touch the monthly allowances; the current ones carry on. A percentage is worked out again on the new basic salary.', 'لا يمس هذا التغيير البدلات الشهرية؛ تستمر الحالية. وتُحسب النسبة المئوية من جديد على الراتب الأساسي الجديد.'],
  ['This change does not touch the monthly allowances; the current ones carry on unchanged.', 'لا يمس هذا التغيير البدلات الشهرية؛ تستمر الحالية دون تغيير.'],
  ['This change sets the monthly allowances shown below.', 'يحدد هذا التغيير البدلات الشهرية الموضحة أدناه.'],
  ['The monthly allowances in this change are the same as the current ones.', 'البدلات الشهرية في هذا التغيير هي نفسها الحالية.'],
  ['On the salary now', 'على الراتب حالياً'],
  ['Difference', 'الفرق'],
  ['Changed', 'تغيّر'],
  ['Part of the first package. Nothing is paid until Finance approves it.', 'جزء من الحزمة الأولى. لا يُصرف شيء حتى تعتمدها المالية.'],
  ['Paid each month on top of the salary above.', 'تُصرف كل شهر إضافة إلى الراتب أعلاه.'],
  ['This change is out of date, so it was not approved. Nothing was paid or changed.', 'هذا التغيير لم يعد صالحاً، لذلك لم يُعتمد. لم يُصرف أو يُغيَّر أي شيء.'],
  ['Posts to {account}', 'يُرحَّل إلى {account}'],
  ['Counts toward end of service pay', 'يدخل في مكافأة نهاية الخدمة'],
  ['Left out of end of service pay: {reason}', 'مستثنى من مكافأة نهاية الخدمة: {reason}'],
  ['Left out of end of service pay', 'مستثنى من مكافأة نهاية الخدمة'],
  ['The salary was not saved.', 'لم يُحفظ الراتب.'],
];

export const salaryAllowances: ReleaseADict = {
  en: Object.fromEntries(pairs.map(([en]) => [en, en])),
  ar: Object.fromEntries(pairs),
};
