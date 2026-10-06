import type { ReleaseADict } from './types';

/**
 * Deductions statement strings, HR and employee (R3). Owned by slice R3; R0 seeded it. Add keys here, never in translations.ts.
 * Whole sentences with {placeholders}; amounts and percentages arrive already formatted for the reader's locale.
 * The "legal basis" sentences are keys the API sends (DeductionLegalBasis in DeductionStatementService.cs) — keep them in step.
 */
const pairs: [string, string][] = [
  // R0 seeds
  ['Deductions from each payslip, with their legal basis and the remaining balance, appear here.', 'تظهر هنا الاستقطاعات من كل مسير رواتب مع سندها النظامي والرصيد المتبقي.'],
  ['Every deduction from your pay, why it is made, and what is left to repay.', 'كل خصم من راتبك وسببه والمبلغ المتبقي للسداد.'],

  // Legal basis of a line (keys sent by the API)
  ['Required by law: social insurance (GOSI) and other contributions the law makes payable (Article 92).', 'مستحق بموجب النظام: اشتراكات التأمينات الاجتماعية وغيرها من الاشتراكات التي يوجبها النظام (المادة 92).'],
  ['Repayment of a loan from the employer (Article 92). Each instalment may be at most 10% of the wage unless the employee agreed in writing.', 'سداد قرض من صاحب العمل (المادة 92). لا يجوز أن يزيد كل قسط على 10% من الأجر إلا بموافقة الموظف الكتابية.'],
  ['Repayment of a salary advance from the employer (Article 92).', 'سداد سلفة على الراتب من صاحب العمل (المادة 92).'],
  ['Pay not earned for time not worked. This is not a deduction under Article 92.', 'أجر غير مستحق عن وقت لم يُعمل فيه، وهو ليس استقطاعاً بموجب المادة 92.'],
  ['A penalty or damages under the approved work regulations (Article 92).', 'جزاء أو تعويض عن أضرار وفق لائحة تنظيم العمل المعتمدة (المادة 92).'],
  ['A debt ordered by a court (Article 92).', 'دين صادر بحكم قضائي (المادة 92).'],
  ["A correction of this month's own pay. It is not a debt owed to the employer.", 'تصحيح لراتب هذا الشهر نفسه، وليس ديناً مستحقاً لصاحب العمل.'],
  ['A payroll adjustment of a type the system does not recognise.', 'تسوية رواتب من نوع لا يعرفه النظام.'],
  ['Recovery of pay overpaid in an earlier payroll run.', 'استرداد أجر صُرف بالزيادة في مسير رواتب سابق.'],
  ['Another deduction recorded on the payslip.', 'استقطاع آخر مسجّل في مسير الرواتب.'],

  // Categories and limit status
  ['Social insurance and other deductions required by law', 'التأمينات الاجتماعية وغيرها من الاستقطاعات النظامية'],
  ['Employer loan', 'قرض من صاحب العمل'],
  ['Salary advance', 'سلفة على الراتب'],
  ['Pay not earned (absence)', 'أجر غير مستحق (غياب)'],
  ['Penalty or adjustment', 'جزاء أو تسوية'],
  ['Court order', 'حكم قضائي'],
  ['Other deduction', 'استقطاع آخر'],
  ['Within the limit', 'ضمن الحد'],
  ['Near the limit', 'قريب من الحد'],
  ['Over the limit', 'يتجاوز الحد'],

  // Statement
  ['Not final yet', 'غير نهائي بعد'],
  ['The 50% limit', 'حد الـ 50%'],
  ['Wage due', 'الأجر المستحق'],
  ['Counted deductions', 'الاستقطاعات المحتسبة'],
  ['50% limit', 'حد 50%'],
  ['Over the limit by', 'التجاوز عن الحد'],
  ['Room left', 'المتبقي ضمن الحد'],
  ['Article 93: loan, advance, penalty and court-ordered deductions together may not exceed half of the wage due. Social insurance (GOSI) and pay not earned for absence are not counted.', 'المادة 93: لا يجوز أن تتجاوز استقطاعات القروض والسلف والجزاءات والأحكام القضائية مجتمعةً نصف الأجر المستحق. ولا تُحتسب التأمينات الاجتماعية ولا الأجر غير المستحق عن الغياب.'],
  ['Counted toward the 50% limit', 'محتسبة ضمن حد الـ 50%'],
  ['Nothing on this payslip counts toward the limit.', 'لا شيء في مسير الرواتب هذا يُحتسب ضمن الحد.'],
  ['Not counted in the limit', 'غير محتسبة ضمن الحد'],
  ['No other deductions on this payslip.', 'لا توجد استقطاعات أخرى في مسير الرواتب هذا.'],
  ["These lines add up to the payslip's deductions total of {amount}.", 'مجموع هذه البنود يطابق إجمالي استقطاعات مسير الرواتب البالغ {amount}.'],
  ["These lines do not add up to the payslip's deductions total of {amount}. Ask payroll to check this payslip before relying on it.", 'مجموع هذه البنود لا يطابق إجمالي استقطاعات مسير الرواتب البالغ {amount}. اطلب من قسم الرواتب مراجعته قبل الاعتماد عليه.'],
  ['{type} instalment of {amount}. The balance after this payslip could not be confirmed.', 'قسط {type} بمبلغ {amount}. تعذّر تأكيد الرصيد المتبقي بعد مسير الرواتب هذا.'],
  ['{type} instalment of {amount}: this was the last instalment.', 'قسط {type} بمبلغ {amount}: هذا هو القسط الأخير.'],
  ['{type} instalment of {amount}: {remaining} of {total} instalments left, {balance} still owed.', 'قسط {type} بمبلغ {amount}: تبقّى {remaining} من {total} أقساط، والمبلغ المتبقي {balance}.'],
  ['{type} instalment of {amount}: {remaining, plural, one {# instalment} other {# instalments}} left, {balance} still owed.', 'قسط {type} بمبلغ {amount}: {remaining, plural, one {تبقّى قسط واحد} two {تبقّى قسطان} few {تبقّت # أقساط} many {تبقّى # قسطاً} other {تبقّى # قسط}}، والمبلغ المتبقي {balance}.'],
  ['Limit without your written consent: 10% of your wage (this instalment is {pct}).', 'الحد دون موافقتك الكتابية: 10% من أجرك (هذا القسط {pct}).'],
  ["Limit without the employee's written consent: 10% of the wage (this instalment is {pct}).", 'الحد دون موافقة الموظف الكتابية: 10% من الأجر (هذا القسط {pct}).'],
  ['Your written consent is on file.', 'موافقتك الكتابية محفوظة في ملفك.'],
  ["The employee's written consent is on file.", 'موافقة الموظف الكتابية محفوظة في ملفه.'],
  ['Above 10% and no written consent is on file.', 'يتجاوز 10% ولا توجد موافقة كتابية محفوظة.'],
  ['Counts toward the 50% limit', 'يُحتسب ضمن حد الـ 50%'],
  ['Not counted in the 50% limit', 'لا يُحتسب ضمن حد الـ 50%'],
  ['Counted deductions of {debt} are over the limit of {limit} by {over}.', 'الاستقطاعات المحتسبة البالغة {debt} تتجاوز الحد البالغ {limit} بمقدار {over}.'],
  ['Counted deductions of {debt} use {pct} of the wage. The limit is {limit}, so {room} is left.', 'الاستقطاعات المحتسبة البالغة {debt} تمثل {pct} من الأجر. الحد {limit}، فيتبقى {room}.'],

  // Balances and history
  ['Open loans and advances', 'القروض والسلف القائمة'],
  ['You have nothing left to repay.', 'لا يوجد عليك مبلغ متبقٍ للسداد.'],
  ['Nothing is left to repay.', 'لا يوجد مبلغ متبقٍ للسداد.'],
  ['You have no finalised payslips in the last {months} months.', 'لا توجد لديك مسيرات رواتب نهائية خلال آخر {months} أشهر.'],
  ['No payslips for this employee in the last {months} months.', 'لا توجد مسيرات رواتب لهذا الموظف خلال آخر {months} أشهر.'],
  ['Counted: {amount}', 'المحتسب: {amount}'],
  ['{balance} still owed: {instalment} a month, {remaining, plural, one {# instalment} other {# instalments}} left.', 'المتبقي {balance}: {instalment} شهرياً، {remaining, plural, one {وتبقّى قسط واحد} two {وتبقّى قسطان} few {وتبقّت # أقساط} many {وتبقّى # قسطاً} other {وتبقّى # قسط}}.'],
  ['{balance} still owed: {instalment} a month.', 'المتبقي {balance}: {instalment} شهرياً.'],
  ['Next instalment due on {date}.', 'القسط التالي مستحق في {date}.'],
  ['Repaid outside payroll, so it is not deducted from pay.', 'يُسدَّد خارج مسير الرواتب، لذلك لا يُخصم من الراتب.'],
  ['You do not have access to payroll deductions.', 'ليست لديك صلاحية الاطلاع على استقطاعات الرواتب.'],
  ['Deductions could not be loaded.', 'تعذّر تحميل الاستقطاعات.'],
  ['Your account is not linked to an employee record. Ask HR to link it.', 'حسابك غير مرتبط بسجل موظف. اطلب من الموارد البشرية ربطه.'],
  ['Could not load your deductions.', 'تعذّر تحميل خصوماتك.'],
  ['Loading your deductions', 'جارٍ تحميل خصوماتك'],
  ['Why each deduction is made', 'سبب كل خصم'],

  // Payroll run check and drawer
  ['The deductions check could not be loaded.', 'تعذّر تحميل فحص الاستقطاعات.'],
  ['These deductions could not be loaded.', 'تعذّر تحميل هذه الاستقطاعات.'],
  ['Deductions check (Article 93)', 'فحص الاستقطاعات (المادة 93)'],
  ['Loan, advance, penalty and court-ordered deductions may not exceed half of the wage due. Near the limit means more than 80% of it is used.', 'لا يجوز أن تتجاوز استقطاعات القروض والسلف والجزاءات والأحكام القضائية نصف الأجر المستحق. ويعني «قريب من الحد» أن أكثر من 80% منه مستخدم.'],
  ['Show', 'عرض'],
  ['Near or over the limit', 'قريب من الحد أو يتجاوزه'],
  ['Everyone', 'الجميع'],
  ['Over the limit: {over}. Near the limit: {near}. Employees in this run: {total}.', 'يتجاوز الحد: {over}. قريب من الحد: {near}. الموظفون في هذا المسير: {total}.'],
  ['No employee in this run is near or over the limit.', 'لا يوجد موظف في هذا المسير قريب من الحد أو يتجاوزه.'],
  ['No payslips in this run yet.', 'لا توجد مسيرات رواتب في هذا التشغيل بعد.'],
  ['Share of wage', 'النسبة من الأجر'],
  ['Needs a look', 'يحتاج إلى مراجعة'],
  ['Open the deductions for {name}', 'فتح استقطاعات {name}'],
  ['Deductions for {name}', 'استقطاعات {name}'],

  // Loan request: Art. 92 consent step
  ['Written consent needed (Article 92)', 'يلزم موافقة كتابية (المادة 92)'],
  ['The wage could not be confirmed, so the 10% limit cannot be checked. A signed consent is needed before this instalment can be deducted from pay.', 'تعذّر تأكيد الأجر، لذلك لا يمكن التحقق من حد 10%. يلزم وجود موافقة موقّعة قبل خصم هذا القسط من الراتب.'],
  ['Your instalment is {pct} of your wage. Above 10%, it can be deducted from your pay only with your signed consent.', 'قسطك يمثل {pct} من أجرك. إذا تجاوز 10% فلا يُخصم من راتبك إلا بموافقتك الموقّعة.'],
  ["This instalment is {pct} of the employee's wage. Above 10%, it can be deducted from pay only with the employee's signed consent.", 'هذا القسط يمثل {pct} من أجر الموظف. إذا تجاوز 10% فلا يُخصم من الراتب إلا بموافقة الموظف الموقّعة.'],
  ['Choose more instalments to bring it to 10% or less, or sign the consent form with HR, who will submit the request with it.', 'اختر عدداً أكبر من الأقساط ليصبح القسط 10% أو أقل، أو وقّع نموذج الموافقة لدى الموارد البشرية لتقدّم الطلب مرفقاً به.'],
  ['Signed consent attached: {file}', 'تم إرفاق الموافقة الموقّعة: {file}'],
  ['Signed consent attached.', 'تم إرفاق الموافقة الموقّعة.'],
  ['Uploading…', 'جارٍ الرفع…'],
  ["Upload the employee's signed consent", 'ارفع موافقة الموظف الموقّعة'],
  ['It is kept on the employee’s file as HR evidence and is not shown in self-service.', 'تُحفظ في ملف الموظف كمستند إثبات لدى الموارد البشرية ولا تظهر في الخدمة الذاتية.'],
  ['The consent could not be uploaded. Please try again.', 'تعذّر رفع الموافقة. يرجى المحاولة مرة أخرى.'],
];

export const deductions: ReleaseADict = {
  en: Object.fromEntries(pairs.map(([en]) => [en, en])),
  ar: Object.fromEntries(pairs),
};
