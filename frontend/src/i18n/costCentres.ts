// Cost accounting v1 (WP-F): the cost centre rule on GL accounts, the mapping functional class and fixed cost
// centre, and the Cost accounting readiness and switch. Spelled "cost centre" throughout. Finance Arabic follows
// glSetup.ts (مركز التكلفة، قيد، مدين/دائن). English keys are the English text (see i18n/translations.ts).
// «Required» and «Optional» are reused from the shared dictionary, not redefined here: they would change every
// other screen that says Required.
const ar: Record<string, string> = {
  // ── Screen 2: the account's cost centre rule ──
  'Account type says what kind of money an account holds (asset, liability, equity, income or expense); a cost centre says which team or department the cost belongs to, and only income and expense accounts can carry one.': 'نوع الحساب يحدد طبيعة المبلغ (أصول أو خصوم أو حقوق ملكية أو إيرادات أو مصروفات)، أما مركز التكلفة فيحدد الفريق أو القسم الذي تُحمَّل عليه التكلفة، ولا تحمل مركز تكلفة إلا حسابات الإيرادات والمصروفات.',
  'What kind of account this is. Cost centres are set separately, in Cost centre rule.': 'نوع الحساب فقط. تُحدَّد مراكز التكلفة بشكل منفصل في «قاعدة مركز التكلفة».',
  'Cost centre rule': 'قاعدة مركز التكلفة',
  'Cost centre rule: {rule}': 'قاعدة مركز التكلفة: {rule}',
  'Rule: {rule} (inherited)': 'القاعدة: {rule} (موروثة)',
  'Not allowed': 'غير مسموح',
  'Every posting to this account must carry a cost centre.': 'يجب أن يحمل كل ترحيل إلى هذا الحساب مركز تكلفة.',
  'A cost centre is added when one is known.': 'يُضاف مركز التكلفة عند توفره.',
  'Postings to this account never carry a cost centre.': 'لا يحمل أي ترحيل إلى هذا الحساب مركز تكلفة.',
  'Only expense and income accounts can require a cost centre.': 'لا يمكن إلزام مركز التكلفة إلا في حسابات المصروفات والإيرادات.',
  'Filter by cost centre rule': 'تصفية حسب قاعدة مركز التكلفة',
  'All rules': 'جميع القواعد',
  'Cost accounting is off for this company. These rules apply once you turn it on.': 'محاسبة التكلفة متوقفة لهذه الشركة. تسري هذه القواعد عند تفعيلها.',
  'Open Cost accounting': 'فتح محاسبة التكلفة',

  // ── Screen 2: apply the recommended rules ──
  'Apply recommended rules': 'تطبيق القواعد الموصى بها',
  'Apply recommended cost centre rules?': 'تطبيق قواعد مراكز التكلفة الموصى بها؟',
  'Expense and income accounts become Required. Asset, liability and equity accounts become Not allowed.': 'تصبح حسابات المصروفات والإيرادات إلزامية، وتصبح حسابات الأصول والخصوم وحقوق الملكية غير مسموحة.',
  '{count, plural, one {# account will change.} other {# accounts will change.}}': '{count, plural, zero {لن يتغيّر أي حساب.} one {سيتغيّر حساب واحد.} two {سيتغيّر حسابان.} few {ستتغيّر # حسابات.} many {سيتغيّر # حسابًا.} other {سيتغيّر # حساب.}}',
  'Nothing changes in payroll until Cost accounting is turned on.': 'لا يتغيّر شيء في الرواتب إلى أن تُفعَّل محاسبة التكلفة.',
  'All accounts already follow the recommended rules.': 'جميع الحسابات تتبع القواعد الموصى بها بالفعل.',
  'Apply rules': 'تطبيق القواعد',
  '{count, plural, one {Rules applied to # account.} other {Rules applied to # accounts.}}': '{count, plural, zero {لم تُطبَّق القواعد على أي حساب.} one {طُبّقت القواعد على حساب واحد.} two {طُبّقت القواعد على حسابين.} few {طُبّقت القواعد على # حسابات.} many {طُبّقت القواعد على # حسابًا.} other {طُبّقت القواعد على # حساب.}}',
  'Account type': 'نوع الحساب',
  'Becomes': 'يصبح',
  'Reason for this change': 'سبب هذا التغيير',
  'Recorded in the audit log.': 'يُسجَّل في سجل التدقيق.',
  'Enter a reason for this change.': 'أدخل سببًا لهذا التغيير.',
  'The preview could not be loaded.': 'تعذّر تحميل المعاينة.',

  // ── Journal export: what a read-only reader sees ──
  'Line detail is shown to people who can download the file. You can see totals and balances.': 'يظهر تفصيل الأسطر لمن يستطيع تنزيل الملف. يمكنك الاطلاع على الإجماليات والأرصدة.',
  'Your access lets you see totals and balances, but not download the file.': 'تتيح لك صلاحيتك الاطلاع على الإجماليات والأرصدة دون تنزيل الملف.',
};

export const costCentres = { ar, en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) };
