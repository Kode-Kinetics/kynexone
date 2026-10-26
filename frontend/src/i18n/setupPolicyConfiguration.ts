/** Policy setup copy. English keys remain the fallback; Arabic is translated explicitly. */
const ar: Record<string, string> = {
  'Close guide': 'إغلاق الدليل',
  'How this works': 'كيف تعمل هذه الميزة؟',
  'Policy setup guide': 'دليل إعداد السياسة',
  '{count} of {limit} characters': '{count} من أصل {limit} حرف',
  'Previous guide step': 'الخطوة السابقة في الدليل',
  'Continue company setup': 'متابعة إعداد الشركة',
  'Add your HR policy': 'أضف سياسة الموارد البشرية',
  'Review policy text': 'مراجعة نص السياسة',
  'Existing HR policy': 'سياسة الموارد البشرية الحالية',
  'Start from your existing HR policy': 'ابدأ بسياسة الموارد البشرية الحالية',
  'Policy excerpts': 'مقتطفات السياسة',

  'Biometric device': 'جهاز البصمة',
  'Mobile app with location': 'تطبيق الهاتف مع تحديد الموقع',
  'Web check-in': 'تسجيل الحضور عبر الويب',
  'Entered by hand': 'الإدخال اليدوي',
  'Working day (minutes)': 'مدة يوم العمل (بالدقائق)',
  'Break (minutes)': 'مدة الاستراحة (بالدقائق)',
  'Overtime': 'العمل الإضافي',
  'Paid overtime': 'عمل إضافي مدفوع الأجر',
  'Time off in lieu': 'إجازة تعويضية',
  'No overtime policy': 'لا توجد سياسة للعمل الإضافي',

  'Policy name': 'اسم السياسة',
  'Accrual': 'تراكم الرصيد',
  'Prorate partial months by calendar days employed': 'احتساب الأشهر الجزئية بالتناسب مع أيام الخدمة التقويمية',


  'Salary grades': 'درجات الرواتب',
  'Grade {number}': 'الدرجة {number}',
  'Band': 'الفئة',
  'Maximum salary': 'الحد الأعلى للراتب',
  'Level': 'المستوى',

  'Benefits': 'المزايا',
  'Effective from': 'سارية من',
  'Add benefit plan': 'إضافة خطة مزايا',
  'All grades': 'جميع الدرجات',

  'Company details': 'تفاصيل الشركة',
  'Working week': 'أسبوع العمل',
  'Review & create': 'المراجعة والإنشاء',
};

export const setupPolicyConfiguration = {
  ar,
  en: Object.fromEntries(Object.keys(ar).map(key => [key, key])),
};
