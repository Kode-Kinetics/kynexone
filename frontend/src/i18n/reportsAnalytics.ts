// Reports → Analytics Dashboard KPI tiles (views/ReportsPage.tsx): each tile's one-line definition, and the link to the
// list or report that holds exactly the records it counts. The definitions restate AnalyticsController.GetKPIs. Keys
// already translated elsewhere (Active Employees, New This Month, On Leave, Pending Leave, Period, Status) are reused.
const ar: Record<string, string> = {
  'Exits This Month': 'المغادرون هذا الشهر',
  'Open Positions': 'الوظائف الشاغرة',
  'Present Today': 'الحاضرون اليوم',
  'Late Today': 'المتأخرون اليوم',
  'Pending OT': 'العمل الإضافي المعلق',
  'Active Loans': 'القروض النشطة',
  'Visas Expiring (30d)': 'تأشيرات تنتهي (30 يومًا)',
  'Passports Expiring (30d)': 'جوازات تنتهي (30 يومًا)',
  'Total Net Salary': 'إجمالي صافي الرواتب',
  'Needs access to this data': 'يتطلب صلاحية الوصول إلى هذه البيانات',
  'Outstanding: {amount}': 'المتبقي: {amount}',
  'Employees whose status is Active.': 'الموظفون الذين حالتهم نشط.',
  'Employees whose joining date falls in this calendar month.': 'الموظفون الذين يقع تاريخ التحاقهم في هذا الشهر الميلادي.',
  'Resigned or terminated employees whose contract ends in this calendar month.':
    'الموظفون المستقيلون أو المنتهية خدمتهم الذين ينتهي عقدهم في هذا الشهر الميلادي.',
  'Job openings whose status is Open.': 'الوظائف المطروحة التي حالتها مفتوحة.',
  "Today's attendance records marked present, late or half day.": 'سجلات حضور اليوم المسجلة حضورًا أو تأخيرًا أو نصف يوم.',
  "Today's attendance records with late minutes.": 'سجلات حضور اليوم التي فيها دقائق تأخير.',
  'Approved leave requests that cover today.': 'طلبات الإجازة المعتمدة التي تشمل اليوم.',
  'Leave requests submitted and waiting for a decision.': 'طلبات الإجازة المقدّمة التي تنتظر قرارًا.',
  'Overtime requests waiting for a decision.': 'طلبات العمل الإضافي التي تنتظر قرارًا.',
  'Active employee loans. Outstanding is what is still to be repaid on them.': 'قروض الموظفين النشطة. المتبقي هو ما لم يُسدَّد منها بعد.',
  'Active visas that expire in the next 30 days.': 'التأشيرات السارية التي تنتهي خلال الثلاثين يومًا القادمة.',
  'Active passports that expire in the next 30 days.': 'الجوازات السارية التي تنتهي خلال الثلاثين يومًا القادمة.',
  'The latest pay period with a payroll run. Draft and voided runs are not counted.':
    'أحدث فترة رواتب فيها مسير رواتب. لا تُحتسب المسيرات المسودة أو الملغاة.',
  "The status of that period's payroll runs.": 'حالة مسيرات الرواتب لتلك الفترة.',
  "Net pay of that period's payroll runs. Shown only when they are all in one currency.":
    'صافي رواتب مسيرات تلك الفترة. يظهر فقط إذا كانت كلها بعملة واحدة.',
  'Show these employees': 'عرض هؤلاء الموظفين',
  'Open the Pending Leave Approvals report': 'فتح تقرير الإجازات بانتظار الموافقة',
  'Open the Loan Balance report': 'فتح تقرير أرصدة القروض',
  'Open the payroll runs': 'فتح مسيرات الرواتب',
};

export const reportsAnalytics = { ar, en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) };
