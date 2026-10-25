/**
 * Setup → Attendance rules (components/attendance/AttendanceRulesPanel.tsx, lib/attendanceRules.ts). Terms follow the
 * Attendance module: الحضور، التأخير (late), الانصراف المبكر (early exit), نصف يوم (half day), فترة السماح (grace),
 * ساعات الدوام (working day), الاستراحة (break).
 */
const ar: Record<string, string> = {
  // Section
  'Attendance rules': 'قواعد الحضور',
  'What counts as late, an early exit or a half day, and the length of the working day.': 'ما يُعدّ تأخيرًا أو انصرافًا مبكرًا أو نصف يوم، ومدة يوم العمل.',
  // Page
  'These rules decide who counts as late, who left early, what makes a half day, and how long a working day is. Each employee follows the most specific policy that covers them, or the one assigned on their record.': 'تحدّد هذه القواعد من يُعدّ متأخرًا ومن انصرف مبكرًا وما يُعدّ نصف يوم وكم تبلغ مدة يوم العمل. يتبع كل موظف أدق سياسة تشمله، أو السياسة المعيّنة في سجلّه.',
  'New rules': 'قواعد جديدة',
  'Everyone': 'الجميع',
  'Applies to': 'تنطبق على',
  'Grace before late': 'السماح قبل التأخير',
  'Early leaving allowed': 'الانصراف المبكر المسموح',
  'Half day below': 'نصف يوم عند أقل من',
  'Working day': 'يوم العمل',
  'Unpaid break': 'استراحة غير مدفوعة',
  '{n} min': '{n} دقيقة',
  'Off': 'متوقفة',
  'Turn off': 'إيقاف',
  'Turn on': 'تشغيل',
  'Turn off these rules?': 'هل تريد إيقاف هذه القواعد؟',
  'Employees covered by {name} will follow the next most specific policy from now on. Days already processed do not change.': 'سيتبع الموظفون الذين تشملهم {name} السياسة التالية الأدق من الآن فصاعدًا. لا تتغيّر الأيام التي عولجت بالفعل.',
  '{name} is on again.': 'تم تشغيل {name} من جديد.',
  '{name} is off. Its employees follow the other rules.': 'تم إيقاف {name}. يتبع موظفوها القواعد الأخرى.',
  '{name} saved.': 'تم حفظ {name}.',
  'The change could not be saved. Try again.': 'تعذّر حفظ التغيير. حاول مرة أخرى.',
  // Editor
  'New attendance rules': 'قواعد حضور جديدة',
  'Edit attendance rules': 'تعديل قواعد الحضور',
  'Changes apply to attendance processed from now on.': 'تسري التغييرات على الحضور الذي يُعالج من الآن فصاعدًا.',
  'Shown in this list, for example “Head office hours”.': 'يظهر في هذه القائمة، مثل «دوام المكتب الرئيسي».',
  'The code cannot be changed: employees can be assigned to these rules by code.': 'لا يمكن تغيير الرمز: يمكن تعيين الموظفين لهذه القواعد بالرمز.',
  'Short and unique, for example HQ-DAY. Letters, digits, hyphens and underscores.': 'قصير وفريد، مثل HQ-DAY. حروف وأرقام وشرطات.',
  'Who it applies to': 'على من تنطبق',
  'Leave all three on “All” to cover everyone. Choose a branch, department or grade to cover only them. The most specific policy wins, and an employee assigned a policy on their record always follows that one.': 'اترك الخيارات الثلاثة على «الكل» لتشمل الجميع. اختر فرعًا أو إدارة أو درجة لتشمل أصحابها فقط. تسود السياسة الأدق، ويتبع الموظف المعيّنة له سياسة في سجلّه تلك السياسة دائمًا.',
  'Some branches, departments or grades could not be loaded.': 'تعذّر تحميل بعض الفروع أو الإدارات أو الدرجات.',
  'All branches': 'كل الفروع',
  'All departments': 'كل الإدارات',
  'All grades': 'كل الدرجات',
  'Grace period before late (minutes)': 'فترة السماح قبل احتساب التأخير (بالدقائق)',
  'Someone arriving within this time of shift start is not counted as late.': 'من يصل خلال هذه المدة من بداية الوردية لا يُحتسب متأخرًا.',
  'Early leaving allowed (minutes)': 'الانصراف المبكر المسموح (بالدقائق)',
  'Leaving this long before shift end is not counted as an early exit.': 'الانصراف قبل نهاية الوردية بهذه المدة لا يُحتسب انصرافًا مبكرًا.',
  'Working day (minutes)': 'يوم العمل (بالدقائق)',
  'The length of a full day, for example 480 for 8 hours. Also used for leave days and Ramadan hours.': 'مدة اليوم الكامل، مثل 480 لثماني ساعات. تُستخدم أيضًا لأيام الإجازة وساعات رمضان.',
  'Unpaid break (minutes)': 'الاستراحة غير المدفوعة (بالدقائق)',
  'Taken off the time between first in and last out.': 'تُخصم من المدة بين أول دخول وآخر خروج.',
  'Half day below (minutes)': 'نصف يوم عند أقل من (بالدقائق)',
  'A day worked for less than this is marked as a half day.': 'اليوم الذي يُعمل أقل من هذه المدة يُسجَّل نصف يوم.',
  'Saving does not change days already processed. The new rules apply to days processed from now on and to days you recompute. Before a payroll run, unpaid days that now differ are listed under “Recompute these days”; paid and payroll-locked days never change.': 'لا يغيّر الحفظ الأيام التي عولجت بالفعل. تسري القواعد الجديدة على الأيام التي تُعالج من الآن وعلى الأيام التي تعيد احتسابها. قبل تشغيل مسير الرواتب تُعرض الأيام غير المدفوعة التي اختلفت نتيجتها تحت «أعد احتساب هذه الأيام»؛ أما الأيام المدفوعة والمقفلة في الرواتب فلا تتغيّر أبدًا.',
  'The rules could not be saved. Try again.': 'تعذّر حفظ القواعد. حاول مرة أخرى.',
  'Create rules': 'إنشاء القواعد',
  // Validation
  'Use 1 to 40 letters, digits, hyphens or underscores, with no spaces.': 'استخدم من 1 إلى 40 حرفًا أو رقمًا أو شرطة أو شرطة سفلية، دون مسافات.',
  'Give the policy a name of up to 120 characters.': 'أعطِ السياسة اسمًا لا يتجاوز 120 حرفًا.',
  'Enter a whole number of minutes from 0 to 120.': 'أدخل عددًا صحيحًا من الدقائق بين 0 و120.',
  'Enter a whole number of minutes from 0 to 240.': 'أدخل عددًا صحيحًا من الدقائق بين 0 و240.',
  'Enter a whole number of minutes from 60 to 960.': 'أدخل عددًا صحيحًا من الدقائق بين 60 و960.',
  'Enter a whole number of minutes, shorter than the working day.': 'أدخل عددًا صحيحًا من الدقائق أقصر من يوم العمل.',
  'Enter a whole number of minutes above 0 and no more than the working day.': 'أدخل عددًا صحيحًا من الدقائق أكبر من 0 ولا يزيد على يوم العمل.',
  // Company setup, More settings
  'Grace period, early leaving, half day and working day': 'فترة السماح والانصراف المبكر ونصف اليوم ويوم العمل',
  'HR approval rules have no screen of their own yet. Ask your group super admin to set these up.': 'ليس لقواعد موافقات الموارد البشرية شاشة خاصة بعد. اطلب من المسؤول الأعلى للمجموعة إعدادها.',
};

export const attendanceRules = {
  en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) as Record<string, string>,
  ar,
};
