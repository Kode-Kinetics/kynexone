// The chart of accounts every company starts with (Models/FinanceGl.cs). A company may rename any account; a renamed
// account is shown as typed, because the lookup only matches the seeded English text. English keys are the English text.
const ar: Record<string, string> = {
  'Cash/Bank': 'النقد والبنك',
  'Employee Loans Receivable': 'قروض الموظفين المستحقة القبض',
  'Employee Salary Advances': 'سلف رواتب الموظفين',
  'Employee Overpayment Receivable': 'مبالغ مدفوعة زيادة للموظفين مستحقة القبض',
  'Prepaid Statutory Remittance': 'مدفوعات نظامية مقدَّمة',
  'Salaries Payable': 'الرواتب المستحقة الدفع',
  'Social Insurance Payable (Employee)': 'التأمينات الاجتماعية المستحقة (حصة الموظف)',
  'Income Tax Payable': 'ضريبة الدخل المستحقة',
  'Fixed Deductions Payable': 'الاستقطاعات الثابتة المستحقة',
  'Social Insurance Employer Payable': 'التأمينات الاجتماعية المستحقة (حصة صاحب العمل)',
  'Loan & Advance Deductions Payable': 'استقطاعات القروض والسلف المستحقة',
  'Other Deductions': 'استقطاعات أخرى',
  'Bonus Payable': 'المكافآت المستحقة الدفع',
  'End of Service Benefit Provision': 'مخصص مكافأة نهاية الخدمة',
  'Final Settlement Payable': 'التسوية النهائية المستحقة الدفع',
  'Accrued Annual Leave Provision': 'مخصص الإجازة السنوية المستحقة',
  'Opening Balance Equity': 'حقوق الملكية للأرصدة الافتتاحية',
  'Basic Salary Expense': 'مصروف الراتب الأساسي',
  'Housing Allowance Expense': 'مصروف بدل السكن',
  'Transport Allowance Expense': 'مصروف بدل النقل',
  'Other Allowances Expense': 'مصروف البدلات الأخرى',
  'Overtime Expense': 'مصروف العمل الإضافي',
  'Absence, Late & Early Deductions (contra)': 'استقطاعات الغياب والتأخير والانصراف المبكر (حساب مقابل)',
  'Attendance & Late Deductions (contra)': 'استقطاعات الحضور والتأخير (حساب مقابل)',
  'Unpaid Leave Deductions (contra)': 'استقطاعات الإجازة غير المدفوعة (حساب مقابل)',
  'Other Earnings': 'مستحقات أخرى',
  'Employer Social Insurance Expense': 'مصروف التأمينات الاجتماعية (حصة صاحب العمل)',
  'End of Service Benefit Expense': 'مصروف مكافأة نهاية الخدمة',
  'Leave Encashment Expense': 'مصروف صرف بدل الإجازة',
  'Payment in Lieu of Notice': 'بدل مهلة الإشعار',
  'Final Settlement Deductions (contra)': 'استقطاعات التسوية النهائية (حساب مقابل)',
  'Employee Receivable Write-off Expense': 'مصروف إعدام ذمم الموظفين',
  'Annual Leave Provision Expense': 'مصروف مخصص الإجازة السنوية',
  'Payroll Rounding Differences': 'فروقات تقريب الرواتب',
  'Employee Bonus Expense': 'مصروف مكافآت الموظفين',
};

const en: Record<string, string> = Object.fromEntries(Object.keys(ar).map(key => [key, key]));

export const glAccountNames = { en, ar };
