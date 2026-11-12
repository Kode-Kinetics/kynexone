/** Leave Balance: creating the year's balances from the leave policies. Terms follow GLOSSARY.md. */
const created = '{count, plural, one {Created # balance} other {Created # balances}} for {year} from the leave policies.';
const nothing = 'No new balances were created for {year}. Balances come from active leave policies, so check that each leave type has an active policy and that employees have a joining date.';
const docInfo = "This leave type needs a supporting document, for example a medical certificate. It is saved on the employee's file and sent with the request.";
const explain = 'Leave balances are created from the leave policies. Create {year} balances for every employee now: monthly policies are credited month by month up to today, and yearly policies grant their full entitlement. Balances that already exist are not changed.';

export const leaveOpenYear = {
  en: {
    [created]: created,
    [nothing]: nothing,
    [explain]: explain,
    [docInfo]: docInfo,
    'Creating balances…': 'Creating balances…',
    "Create this year's balances from the leave policies": "Create this year's balances from the leave policies",
  },
  ar: {
    [created]: '{count, plural, zero {لم يُنشأ أي رصيد} one {أُنشئ رصيد واحد} two {أُنشئ رصيدان} few {أُنشئت # أرصدة} many {أُنشئ # رصيداً} other {أُنشئ # رصيد}} لعام {year} من سياسات الإجازات.',
    [nothing]: 'لم يُنشأ أي رصيد جديد لعام {year}. تأتي الأرصدة من سياسات الإجازات الفعّالة، فتأكد من وجود سياسة فعّالة لكل نوع إجازة ومن إدخال تاريخ الانضمام للموظفين.',
    [explain]: 'تُنشأ أرصدة الإجازات من سياسات الإجازات. أنشئ الآن أرصدة عام {year} لجميع الموظفين: تُضاف السياسات الشهرية شهراً بشهر حتى اليوم، وتمنح السياسات السنوية استحقاقها كاملاً. لا تتغير الأرصدة الموجودة.',
    [docInfo]: 'هذا النوع من الإجازات يتطلب مستنداً داعماً، مثل شهادة طبية. يُحفظ المستند في ملف الموظف ويُرسل مع الطلب.',
    'Creating balances…': 'جارٍ إنشاء الأرصدة…',
    "Create this year's balances from the leave policies": 'إنشاء أرصدة هذا العام من سياسات الإجازات',
  },
};
