// "Below the legal minimum" warnings (components/BelowMinimumPanel.tsx, views/BelowMinimumRules.tsx): statutory values are
// defaults; going under one needs an acknowledgement and a reason, and is listed so nothing is hidden. The server's own
// sentences (warnings, labels, citations) arrive already in English and Arabic.
const ar: Record<string, string> = {
  'Saudi default: 15 days = ½ month': 'الافتراضي السعودي: 15 يومًا = نصف شهر',
  'Saudi default: 30 days = 1 month': 'الافتراضي السعودي: 30 يومًا = شهر',
  'Legal minimums': 'الحدود الدنيا النظامية',
  'Saudi default: {value}': 'الافتراضي السعودي: {value}',
  'More generous than the law': 'أكرم من النظام',
  'Below the legal minimum of {min} ({citation})': 'أقل من الحد الأدنى النظامي البالغ {min} ({citation})',
  'Below the legal minimum ({citation})': 'أقل من الحد الأدنى النظامي ({citation})',
  'I understand employees may claim the difference': 'أدرك أن للموظفين المطالبة بالفرق',
  'Why is this below the legal minimum? (10 to 500 characters)': 'لماذا هي أقل من الحد الأدنى النظامي؟ (من 10 إلى 500 حرف)',
  'Tick the box and give a reason to save below the legal minimum.': 'ضع علامة في المربع واذكر السبب لحفظ قيمة أقل من الحد الأدنى النظامي.',
  'Acknowledged by {name} on {date}: {reason}': 'أقرّ بذلك {name} في {date}: {reason}',
  'Rules below the legal minimum': 'القواعد الأقل من الحد الأدنى النظامي',
  'Every rule set below what the law requires, and who accepted it. Nothing is hidden.': 'كل قاعدة تقل عما يقضي به النظام، ومن قبل بها. لا شيء مخفي.',
  'No rules are below the legal minimum.': 'لا توجد قواعد أقل من الحد الأدنى النظامي.',
  'Could not load the rules below the legal minimum.': 'تعذّر تحميل القواعد الأقل من الحد الأدنى النظامي.',
  'Rule': 'القاعدة',
  'Configured': 'المحدد',
  'Legal minimum': 'الحد الأدنى النظامي',
  'Citation': 'السند النظامي',
  'Acknowledged': 'الإقرار',
  'Not acknowledged': 'لم يُقرّ به',
  'Leave policy': 'سياسة الإجازة',
  'End of service': 'نهاية الخدمة',
  'Payroll rule': 'قاعدة الرواتب',
};

export const belowMinimum = { ar, en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) };
