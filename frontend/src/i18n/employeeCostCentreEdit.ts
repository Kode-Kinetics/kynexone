/**
 * Cost centre on transfer (Wave A): the cost centre in Edit employee, the create wizard and the hire wizard — where it comes
 * from, the live answer when the department changes, the keep-or-follow question for an override, and the refusals by code.
 * Terms as in ./costCentres: مركز التكلفة (cost centre), القسم (department), القسم الأعلى (parent department), المنصب
 * (position), استثناء (override), الإعداد ← هيكل الشركة (Setup → Company structure).
 * The arrow between a department and a code points in reading order: → in English, ← in Arabic. Codes and names are values,
 * so BdiMessage isolates each one and the arrow stays between them.
 * Keys reused from other catalogs (not redefined here): 'Cost centre', 'Cost center', 'Reason', 'Company default',
 * 'Only HR managers and admins can change this.', 'Enter a reason for this change.', 'Loading…', 'Retry'.
 */
const ar: Record<string, string> = {
  // ── Where it comes from (the source line under the dropdown) ──
  'Inherited from {department}': 'موروث من القسم {department}',
  'Inherited from {department} (parent department)': 'موروث من {department} (القسم الأعلى)',
  'Inherited from position {title}': 'موروث من المنصب {title}',
  'Override: set for this employee': 'استثناء: محدد لهذا الموظف',
  'No cost centre: add one in Setup → Company structure': 'لا يوجد مركز تكلفة: أضِف مركزًا من الإعداد ← هيكل الشركة',

  // ── The dropdown ──
  'Follow department · {code}': 'اتباع القسم · {code}',
  'Follow department · no cost centre': 'اتباع القسم · بلا مركز تكلفة',
  'Follow department': 'اتباع القسم',
  'Why this person has a different cost centre. Recorded in the audit log.': 'سبب اختلاف مركز تكلفة هذا الموظف. يُسجَّل في سجل التدقيق.',
  'Why this person goes back to the department’s cost centre. Recorded in the audit log.': 'سبب عودة هذا الموظف إلى مركز تكلفة القسم. يُسجَّل في سجل التدقيق.',
  'Why you chose this for the override. Recorded in the audit log.': 'سبب اختيارك هذا للاستثناء. يُسجَّل في سجل التدقيق.',

  // ── The department changes in the form ──
  'Will follow {department} → {code}': 'سيتبع {department} ← {code}',
  'Will follow {department}: it has no cost centre yet.': 'سيتبع {department}: لا يوجد فيه مركز تكلفة بعد.',
  'Checking the cost centre for {department}…': 'جارٍ التحقق من مركز التكلفة للقسم {department}…',
  'The cost centre for the new department could not be checked. It is worked out again when you save.': 'تعذّر التحقق من مركز التكلفة للقسم الجديد، وسيُحدَّد من جديد عند الحفظ.',
  'This department is not in the employee’s company.': 'هذا القسم لا يتبع شركة الموظف.',
  'Changing the department moves the cost centre, so only HR managers and admins can change it.': 'تغيير القسم ينقل مركز التكلفة، لذا يقتصر تغييره على مديري الموارد البشرية ومسؤولي النظام.',

  // ── Keep or follow (an override meets a department change) ──
  'This employee has a cost centre override. What happens to it after the move?': 'لهذا الموظف مركز تكلفة استثنائي. ماذا يحدث له بعد النقل؟',
  'Keep {code} (override)': 'الإبقاء على {code} (استثناء)',
  'Follow {department} → {code}': 'اتباع {department} ← {code}',
  'Follow {department} · no cost centre': 'اتباع {department} · بلا مركز تكلفة',
  'Choose whether to keep the override or follow the new department.': 'اختر الإبقاء على الاستثناء أو اتباع القسم الجديد.',

  // ── Review ──
  'From {from} to {to}. Reason: {reason}': 'من {from} إلى {to}. السبب: {reason}',
  'Keeps {code} (override). Reason: {reason}': 'يبقى {code} (استثناء). السبب: {reason}',
  'Follows {department}: {code}. Reason: {reason}': 'يتبع {department}: {code}. السبب: {reason}',
  'Follows {department}: {code}': 'يتبع {department}: {code}',
  '{choice}. Reason: {reason}': '{choice}. السبب: {reason}',

  // ── Refusals, in words, by code ──
  'This cost centre is inactive. Choose an active one.': 'مركز التكلفة هذا غير نشط. اختر مركزًا نشطًا.',
  'This cost centre belongs to another company. Choose one of this company’s cost centres.': 'مركز التكلفة هذا يتبع شركة أخرى. اختر أحد مراكز تكلفة هذه الشركة.',
  'This cost centre no longer exists. Choose another.': 'مركز التكلفة هذا لم يعد موجودًا. اختر مركزًا آخر.',
  'Someone changed this employee’s cost centre since you opened the form. Reload the employee and try again.': 'غيّر شخص آخر مركز تكلفة هذا الموظف منذ فتحت النموذج. أعد تحميل بيانات الموظف وحاول مرة أخرى.',
  'The reason contains characters that are not allowed. Remove them and try again.': 'يحتوي السبب على أحرف غير مسموح بها. احذفها وحاول مرة أخرى.',
  'The reason is too long. Use 500 characters or fewer.': 'السبب طويل جدًا. استخدم 500 حرف أو أقل.',
  'Checking too often. Trying again in a moment…': 'عدد كبير من عمليات التحقق. ستُعاد المحاولة بعد قليل…',
  'The cost centre could not be saved. Review it and try again.': 'تعذّر حفظ مركز التكلفة. راجعه وحاول مرة أخرى.',
  'Changing the department of someone with a cost centre override needs a decision. Open Edit employee to choose.': 'تغيير قسم موظف له مركز تكلفة استثنائي يحتاج إلى قرار. افتح تعديل الموظف للاختيار.',
  'Open Edit employee': 'فتح تعديل الموظف',
  'The cost centres could not be loaded.': 'تعذّر تحميل مراكز التكلفة.',
};

export const employeeCostCentreEdit = { ar, en: Object.fromEntries(Object.keys(ar).map((key) => [key, key])) };
