// Payroll → manpower + value reconciliation against the prior month, and the sign-off that gates Approve.
// English keys are the English text (see i18n/translations.ts). Arabic follows the payroll usage HR teams read every day:
// مسير الرواتب (payroll run), مطابقة (reconciliation), اعتماد (approve), مصادقة / توقيع المراجعة (sign-off).
const en: Record<string, string> = {
  'Signed off by {name}': 'Signed off by {name}',
  'Reconciliation signed off': 'Reconciliation signed off',
  'No reconciliation sign-off is needed for this run': 'No reconciliation sign-off is needed for this run',
};

const ar: Record<string, string> = {
  'Signed off by {name}': 'صادق عليها {name}',
  'Reconciliation signed off': 'تمت المصادقة على المطابقة',
  'No reconciliation sign-off is needed for this run': 'لا يلزم مصادقة على المطابقة لهذا المسير',
};

export const payrollRecon = { en, ar };
