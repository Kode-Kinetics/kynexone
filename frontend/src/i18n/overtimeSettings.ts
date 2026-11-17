/** Overtime company settings (Setup → Payroll rules) and the overtime approval queue. English is the key; Arabic is below. */
const ar: Record<string, string> = {
  // Setup → Payroll rules: the five overtime company settings.
  'Approve overtime automatically': 'اعتماد العمل الإضافي تلقائيا',
  'When on, overtime is approved the moment it is filed, with no manager or HR step. Leave empty for the default: off.':
    'عند التفعيل يُعتمد العمل الإضافي فور تقديمه دون خطوة المدير أو الموارد البشرية. اتركه فارغا للإعداد الافتراضي: معطل.',
  'Approve automatically': 'اعتماد تلقائي',
  'Require an approval step': 'يتطلب خطوة اعتماد',
  'Overtime approved after its month closed': 'عمل إضافي يُعتمد بعد إقفال شهره',
  'Refuse the approval, or carry the pay into the next open payroll run as back pay. Leave empty to use the account-wide setting (default: refuse).':
    'رفض الاعتماد، أو ترحيل المبلغ إلى أول مسير رواتب مفتوح كأجر بأثر رجعي. اتركه فارغا لاستخدام الإعداد العام للحساب (الافتراضي: الرفض).',
  'Refuse the approval': 'رفض الاعتماد',
  'Carry into the next payroll run': 'ترحيله إلى مسير الرواتب التالي',
  'Overtime budgets': 'ميزانيات العمل الإضافي',
  'What happens when approving overtime takes a department over its monthly overtime budget. Approved overtime is always counted against the budget. Leave empty for the default: off.':
    'ما يحدث عندما يتجاوز اعتماد العمل الإضافي ميزانية القسم الشهرية للعمل الإضافي. يُحتسب العمل الإضافي المعتمد دائما من الميزانية. اتركه فارغا للإعداد الافتراضي: معطل.',
  'Do not check budgets': 'عدم التحقق من الميزانيات',
  'Warn when over budget': 'التنبيه عند تجاوز الميزانية',
  'Block approvals over budget': 'منع الاعتماد عند تجاوز الميزانية',
  'Line managers file overtime for their team': 'المديرون المباشرون يقدمون العمل الإضافي لفرقهم',
  'Whether a line manager may file overtime on behalf of their own team members. Leave empty for the default: allowed.':
    'هل يجوز للمدير المباشر تقديم طلب عمل إضافي نيابة عن أعضاء فريقه. اتركه فارغا للإعداد الافتراضي: مسموح.',
  'Managers may file for their team': 'يجوز للمديرين التقديم عن فرقهم',
  'Only HR files for others': 'الموارد البشرية فقط تقدم عن الآخرين',
  'HR may approve the manager step': 'يجوز للموارد البشرية اعتماد خطوة المدير',
  'Whether an HR Manager may take the first (line manager) approval step. The final step still needs a different person. Leave empty for the default: allowed.':
    'هل يجوز لمدير الموارد البشرية تولي خطوة الاعتماد الأولى (المدير المباشر). تبقى الخطوة النهائية لشخص آخر. اتركه فارغا للإعداد الافتراضي: مسموح.',
  'Only the line manager approves step 1': 'المدير المباشر فقط يعتمد الخطوة الأولى',
  "Overtime will be approved, and become payable, the moment it is filed, with no manager or HR check. It needs a second person's approval and is recorded against the person who confirms it.":
    'سيُعتمد العمل الإضافي ويصبح مستحق الصرف فور تقديمه، دون أي تحقق من المدير أو الموارد البشرية. يتطلب ذلك موافقة شخص آخر ويُسجل باسم من يؤكده.',
  // Overtime → Approvals for an HR Manager, who now sees both steps.
  'Overtime awaiting HR': 'عمل إضافي بانتظار الموارد البشرية',
  'Step 2 requests were forwarded by a manager: adjust hours if needed, then approve or reject. Step 1 requests have no manager decision yet: forwarding one sends it to a different HR approver for the final step.':
    'طلبات الخطوة 2 أحالها مدير: عدّل الساعات عند الحاجة ثم اعتمد أو ارفض. طلبات الخطوة 1 لم يقرر فيها مدير بعد: إحالة أحدها ترسله إلى معتمد آخر من الموارد البشرية للخطوة النهائية.',
  'Approved, but this takes the department over its overtime budget for the month.': 'تم الاعتماد، لكنه يتجاوز ميزانية القسم للعمل الإضافي لهذا الشهر.',
};

export const overtimeSettings = { ar, en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) };
