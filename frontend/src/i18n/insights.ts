// Rules-engine findings on the home dashboard (AiInsightEngine): the engine writes English sentences with the numbers baked
// in, so lib/insightText.ts rebuilds each sentence from its type and numbers. English keys are the English text.
// Arabic uses the payroll and HR terms of the rest of the product: صافي الرواتب (net payroll), الرصيد (balance), الإقامة والتأشيرة.

const plural = (one: string, two: string, few: string, many: string, other: string) =>
  `{count, plural, one {${one}} two {${two}} few {${few}} many {${many}} other {${other}}}`;

const ar: Record<string, string> = {
  // Titles
  'Net payroll increased by {pct}%': 'ارتفع صافي الرواتب بنسبة {pct}%',
  'Net payroll decreased by {pct}%': 'انخفض صافي الرواتب بنسبة {pct}%',
  '{count, plural, one {# employee without salary assignment} other {# employees without salary assignment}}': plural('موظف واحد بلا راتب مُسند', 'موظفان بلا راتب مُسند', '# موظفين بلا راتب مُسند', '# موظفًا بلا راتب مُسند', '# موظف بلا راتب مُسند'),
  '{count, plural, one {# employee with overtime exceeding 30% of average basic} other {# employees with overtime exceeding 30% of average basic}}': plural('موظف واحد تجاوز عمله الإضافي 30% من متوسط الراتب الأساسي', 'موظفان تجاوز عملهما الإضافي 30% من متوسط الراتب الأساسي', '# موظفين تجاوز عملهم الإضافي 30% من متوسط الراتب الأساسي', '# موظفًا تجاوز عملهم الإضافي 30% من متوسط الراتب الأساسي', '# موظف تجاوز عملهم الإضافي 30% من متوسط الراتب الأساسي'),
  '{count, plural, one {# employee with 60+ days leave balance} other {# employees with 60+ days leave balance}}': plural('موظف واحد رصيد إجازاته 60 يومًا فأكثر', 'موظفان رصيد إجازاتهما 60 يومًا فأكثر', '# موظفين رصيد إجازاتهم 60 يومًا فأكثر', '# موظفًا رصيد إجازاتهم 60 يومًا فأكثر', '# موظف رصيد إجازاتهم 60 يومًا فأكثر'),
  '{count, plural, one {# termination in the last 30 days ({pct}%)} other {# terminations in the last 30 days ({pct}%)}}': plural('إنهاء خدمة واحد خلال آخر 30 يومًا ({pct}%)', 'إنهاءا خدمة خلال آخر 30 يومًا ({pct}%)', '# حالات إنهاء خدمة خلال آخر 30 يومًا ({pct}%)', '# حالة إنهاء خدمة خلال آخر 30 يومًا ({pct}%)', '# حالة إنهاء خدمة خلال آخر 30 يومًا ({pct}%)'),
  '{count, plural, one {# employee with visa expiring within 60 days} other {# employees with visa expiring within 60 days}}': plural('موظف واحد تنتهي تأشيرته خلال 60 يومًا', 'موظفان تنتهي تأشيرتاهما خلال 60 يومًا', '# موظفين تنتهي تأشيراتهم خلال 60 يومًا', '# موظفًا تنتهي تأشيراتهم خلال 60 يومًا', '# موظف تنتهي تأشيراتهم خلال 60 يومًا'),
  '{count, plural, one {# employee assigned to inactive salary structures} other {# employees assigned to inactive salary structures}}': plural('موظف واحد مُسند إلى هياكل رواتب غير فعالة', 'موظفان مُسندان إلى هياكل رواتب غير فعالة', '# موظفين مُسندون إلى هياكل رواتب غير فعالة', '# موظفًا مُسندًا إلى هياكل رواتب غير فعالة', '# موظف مُسند إلى هياكل رواتب غير فعالة'),

  // Details
  'Net payroll for {period} is up {pct}% ({net}) against the 3-month average ({avg}). Review for new hires, terminations, or one-time adjustments.': 'ارتفع صافي رواتب {period} بنسبة {pct}% ({net}) مقارنة بمتوسط الأشهر الثلاثة السابقة ({avg}). راجع التعيينات الجديدة وإنهاءات الخدمة والتسويات لمرة واحدة.',
  'Net payroll for {period} is down {pct}% ({net}) against the 3-month average ({avg}). Review for new hires, terminations, or one-time adjustments.': 'انخفض صافي رواتب {period} بنسبة {pct}% ({net}) مقارنة بمتوسط الأشهر الثلاثة السابقة ({avg}). راجع التعيينات الجديدة وإنهاءات الخدمة والتسويات لمرة واحدة.',
  '{count} of {total} active employees ({pct}%) have no active salary structure assigned. They will be excluded from payroll processing until salary is configured.': 'لا يوجد هيكل راتب فعال مُسند لـ{count} من أصل {total} من الموظفين النشطين ({pct}%). سيُستثنون من معالجة الرواتب إلى أن يُضبط الراتب.',
  'Employees who received overtime pay above 30% of the company average basic salary in {period}: {count}. Review for unauthorized overtime, shift planning issues, or data entry errors.': 'عدد الموظفين الذين تجاوزت أجور عملهم الإضافي 30% من متوسط الراتب الأساسي في الشركة خلال {period}: {count}. راجع العمل الإضافي غير المصرّح به ومشكلات جدولة الورديات وأخطاء إدخال البيانات.',
  'Employees with more than 60 days of accumulated leave balance: {count}. Large balances are a financial liability and may point to low team morale or vacation planning issues. Consider enforcing a leave utilization policy.': 'عدد الموظفين الذين تجاوز رصيد إجازاتهم المتراكم 60 يومًا: {count}. الأرصدة الكبيرة التزام مالي وقد تدل على ضعف معنويات الفريق أو خلل في تخطيط الإجازات. فكّر في تطبيق سياسة لاستهلاك الإجازات.',
  'Employees who left in the last 30 days: {terminations}, or {pct}% monthly turnover. This is above normal thresholds. Review exit interviews and retention strategies. New hires: {joins}.': 'عدد الموظفين الذين غادروا خلال آخر 30 يومًا: {terminations}، أي معدل دوران شهري {pct}%. وهذا أعلى من الحدود المعتادة. راجع مقابلات الخروج واستراتيجيات الاستبقاء. التعيينات الجديدة: {joins}.',
  'Active employees whose visas expire within the next 60 days: {count}. Expired visas can result in regulatory fines and operational disruption. Initiate renewal processes immediately.': 'عدد الموظفين النشطين الذين تنتهي تأشيراتهم خلال الـ60 يومًا القادمة: {count}. قد يؤدي انتهاء التأشيرات إلى غرامات نظامية وتعطّل في العمل. ابدأ إجراءات التجديد فورًا.',
  'Employees assigned to salary structures that have been deactivated: {count}. They will fail payroll validation. Reassign them to active salary structures.': 'عدد الموظفين المُسندين إلى هياكل رواتب أُلغي تفعيلها: {count}. سيفشل التحقق من رواتبهم. أعد إسنادهم إلى هياكل رواتب فعالة.',
};

const en: Record<string, string> = Object.fromEntries(Object.keys(ar).map(key => [key, key]));

export const insights = { en, ar };
