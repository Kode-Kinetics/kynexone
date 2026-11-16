/**
 * Launch wave: Saudi operations wording. Honest labels (L4.3): the GOSI screen is a reconciliation report, the Saudi bank file is a
 * payroll payment instruction file in a bank's format, and nothing says anything was filed or submitted. Qiwa data-check field names
 * and the work-location explanation (L4.2). Terms follow GLOSSARY.md: التأمينات الاجتماعية, مسير الرواتب, حماية الأجور.
 */
const ar: Record<string, string> = {
  // GOSI: reconciliation report, not a filing
  "GOSI Reconciliation": "مطابقة التأمينات الاجتماعية",
  "GOSI reconciliation": "مطابقة التأمينات الاجتماعية",
  "GOSI reconciliation report": "تقرير مطابقة التأمينات الاجتماعية",
  "Compare what payroll deducted for GOSI with what the rules expect, per employee. A reconciliation report, not a filing.": "قارن ما خصمته الرواتب للتأمينات الاجتماعية بما تتوقعه القواعد لكل موظف. هذا تقرير مطابقة وليس إقرارًا.",
  "This is a reconciliation report. Nothing is submitted or filed with GOSI from here; submit and pay through the GOSI portal.": "هذا تقرير مطابقة. لا يُرسل أو يُقدَّم أي شيء إلى المؤسسة العامة للتأمينات الاجتماعية من هنا؛ قدّم وسدّد عبر بوابة المؤسسة.",
  "Your role cannot read GOSI period summary. The GOSI reconciliation report needs Admin, HR Manager, Payroll Manager or Payroll Officer.": "لا يملك دورك صلاحية قراءة ملخص فترة التأمينات الاجتماعية. يتطلب تقرير المطابقة دور مدير النظام أو مدير الموارد البشرية أو مدير الرواتب أو مسؤول الرواتب.",
  "Your role cannot read payroll run reconciliation. The GOSI reconciliation report needs Admin, HR Manager, Payroll Manager or Payroll Officer.": "لا يملك دورك صلاحية قراءة مطابقة مسير الرواتب. يتطلب تقرير مطابقة التأمينات الاجتماعية دور مدير النظام أو مدير الموارد البشرية أو مدير الرواتب أو مسؤول الرواتب.",
  "Employee GOSI readiness (missing IDs, classification, blocked employees) lives on {link}. This screen reconciles what payroll actually deducted.": "جاهزية الموظفين للتأمينات الاجتماعية (الهويات الناقصة والتصنيف والموظفون المتعذّر احتسابهم) في {link}. تطابق هذه الشاشة ما خصمته الرواتب فعليًا.",
  "There is no processed payroll run for this period and company, so there is nothing to reconcile. Pick a period from the quick-picks above, or process payroll for this month first.": "لا يوجد مسير رواتب معالَج لهذه الفترة والشركة، فلا شيء للمطابقة. اختر فترة من الاختصارات أعلاه، أو عالج رواتب هذا الشهر أولًا.",
  "Whole period · reconciliation view ({count, plural, one {# run} other {# runs}})": "الفترة كاملة · عرض المطابقة ({count, plural, zero {لا مسيرات} one {مسير واحد} two {مسيران} few {# مسيرات} many {# مسيرًا} other {# مسير}})",
  "Other runs share this period. Use the whole-period view to reconcile.": "تشترك مسيرات أخرى في هذه الفترة. استخدم عرض الفترة كاملة للمطابقة.",
  "Expected is recomputed once on the period-aggregated covered wage, so the statutory ceiling is applied exactly once. This is the figure to compare with your GOSI invoice.": "يُعاد احتساب المتوقع مرة واحدة على الأجر الخاضع المجمّع للفترة، فيُطبَّق الحد النظامي الأقصى مرة واحدة فقط. هذا هو الرقم الذي تقارنه بفاتورة التأمينات الاجتماعية.",
  "Read from the effective-dated statutory rules — the same values the payslip and the GOSI reconciliation use. Change them under {path}; this screen never holds its own copy.": "مقروءة من القواعد النظامية ذات تاريخ السريان — وهي القيم نفسها التي تستخدمها قسيمة الراتب ومطابقة التأمينات الاجتماعية. غيّرها ضمن {path}؛ لا تحتفظ هذه الشاشة بنسخة خاصة بها.",
  "Export CSV (for reconciliation, not a GOSI upload)": "تصدير CSV (للمطابقة وليس للرفع إلى التأمينات الاجتماعية)",
  "Reconciliation report from KynexOne. This is not a GOSI file and cannot be uploaded to GOSI. Decimals use a point.": "تقرير مطابقة من KynexOne. هذا ليس ملف التأمينات الاجتماعية ولا يمكن رفعه إليها. الفاصلة العشرية نقطة.",
  "Total": "الإجمالي",
  "Employee code": "رمز الموظف",
  "Employee": "الموظف",
  "Classification": "التصنيف",
  "Covered wage": "الأجر الخاضع",
  "Expected employee": "المتوقع على الموظف",
  "Actual employee": "الفعلي على الموظف",
  "Employee variance": "فرق الموظف",
  "Expected employer": "المتوقع على صاحب العمل",
  "Actual employer": "الفعلي على صاحب العمل",
  "Employer variance": "فرق صاحب العمل",
  // Bank file
  "Saudi bank payroll file: the Payroll payment instruction file (bank format) — the ANB Connect instruction with WPS fields — is the file to send to the bank. It appears below. If it does not, you need the Payroll export permission, or the platform team has switched bank export off for your company. Mudad accepts only the WPS file your bank signs — keep the bank’s output file or a Mudad screenshot as evidence before marking the batch Accepted.": "ملف رواتب البنك السعودي: ملف تعليمات دفع الرواتب (بصيغة البنك) — تعليمات ANB Connect بحقول حماية الأجور — هو الملف الذي يُرسل إلى البنك. يظهر أدناه. وإن لم يظهر فأنت بحاجة إلى صلاحية تصدير الرواتب، أو أن فريق المنصة أوقف التصدير البنكي لشركتك. لا يقبل «مدد» إلا ملف حماية الأجور الذي يوقّعه بنكك — احتفظ بملف المخرجات من البنك أو بلقطة شاشة من «مدد» كدليل قبل وضع علامة «مقبول» على الدفعة.",
  // Attendance rules: when a day starts with no shift assigned (M7)
  "Day starts at (no shift assigned)": "بداية اليوم (دون وردية معيّنة)",
  "09:00 (standard)": "09:00 (الافتراضي)",
  "Day starts at, when no shift is assigned": "بداية اليوم عند عدم تعيين وردية",
  "Late and early leaving are measured from this time, for the working day and break above. Leave empty for 09:00. A shift assigned to an employee always wins.": "يُحتسب التأخر والمغادرة المبكرة من هذا الوقت، بحسب يوم العمل والاستراحة أعلاه. اتركه فارغًا لاعتماد 09:00. الوردية المعيّنة للموظف لها الأسبقية دائمًا.",
  "Enter a time such as 08:00.": "أدخل وقتًا مثل 08:00.",
  // Qiwa data check
  "Saudi / non-Saudi": "سعودي / غير سعودي",
  "ID type": "نوع الهوية",
  "ID number": "رقم الهوية",
  "Nationality": "الجنسية",
  "Occupation code": "رمز المهنة",
  "Establishment ID": "رقم المنشأة",
  "Work location ID": "رقم موقع العمل",
  "Contract reference": "مرجع العقد",
  ", ": "، ",
  "Work location ID is the number Qiwa gives each work location of your establishment. Branches in KynexOne do not hold it yet, so it is not filled from the employee’s branch: enter it on each employee’s record, in the Qiwa section.": "رقم موقع العمل هو الرقم الذي تمنحه «قوى» لكل موقع عمل في منشأتك. لا تحتفظ الفروع في KynexOne به بعد، فلا يُملأ من فرع الموظف: أدخله في سجل كل موظف ضمن قسم «قوى».",
};

export const launchSaudiOps = { ar, en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) as Record<string, string> };
