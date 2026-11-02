/**
 * Add / remove for organisation rows (owner decision 2026-10-25): Setup tabs and the Guided Setup wizard share one rule.
 * Never used = deleted; in use = turned off ("متوقف"), kept in history and restorable ("استعادة").
 * Terms follow the rest of Setup: الفروع، الأقسام، المسميات الوظيفية، الدرجات، مراكز التكلفة، المواقع.
 */
const ar: Record<string, string> = {
  // Kinds (named in the confirmation)
  'branch': 'الفرع',
  'department': 'القسم',
  'designation': 'المسمى الوظيفي',
  'grade': 'الدرجة',
  'cost centre': 'مركز التكلفة',
  'location': 'الموقع',
  'drop-down value': 'قيمة القائمة المنسدلة',

  // Setup tabs: confirmation, result, permission
  'Remove {what}': 'إزالة {what}',
  'Remove {name}?': 'هل تريد إزالة {name}؟',
  'If nothing uses it, it is deleted. If employees or other records use it, it is turned off instead: it stays in history, disappears from new choices, and you can restore it anytime.':
    'إذا لم يستخدمه أي سجل فسيُحذف. أما إذا كان الموظفون أو سجلات أخرى تستخدمه فسيُوقَف بدلًا من ذلك: يبقى في السجل التاريخي ويختفي من الخيارات الجديدة ويمكنك استعادته في أي وقت.',
  'Removed {name}.': 'تمت إزالة {name}.',
  'Turned off {name}. {reason}': 'تم إيقاف {name}. {reason}',
  'Restore anytime.': 'يمكنك استعادته في أي وقت.',
  'Turned {name} back on.': 'تمت إعادة تشغيل {name}.',
  'Dismiss': 'إخفاء',
  'Restore': 'استعادة',
  'Show turned-off ({count})': 'عرض الموقوف ({count})',
  'You do not have permission to change this.': 'ليست لديك صلاحية تعديل هذا العنصر.',
  'This belongs to another company or to the whole group. Ask a group administrator.': 'هذا العنصر يتبع شركة أخرى أو المجموعة بأكملها. اطلب ذلك من مدير المجموعة.',
  'Built-in value: it supports system screens, so it cannot be removed.': 'قيمة مدمجة: تعتمد عليها شاشات النظام، لذا لا يمكن إزالتها.',

  // Wizard rows
  'Will be removed': 'ستتم إزالته',
  'Off': 'متوقف',
  'Undo': 'تراجع',
  'removing': 'إزالة',
  'Will be removed when you save. If anything uses it, it is turned off instead, stays in history, and can be restored.':
    'ستتم إزالته عند الحفظ. وإذا كان أي سجل يستخدمه فسيُوقَف بدلًا من ذلك، ويبقى في السجل التاريخي ويمكن استعادته.',
  'Turned off: it stays in history but is not offered for new records.': 'متوقف: يبقى في السجل التاريخي ولا يُعرض للسجلات الجديدة.',
  'It could not be turned back on. Try again.': 'تعذّرت إعادة تشغيله. حاول مرة أخرى.',
  'Code locked: it is in use.': 'الرمز مقفل لأنه قيد الاستخدام.',
  'Already set up for this company. Edit it here. To change its code or remove it, use Setup: it checks what uses it first.':
    'تم إعداده مسبقًا لهذه الشركة. عدّله هنا. ولتغيير رمزه أو إزالته استخدم الإعداد، فهو يتحقق أولًا مما يستخدمه.',

  '{count, plural, one {# employee} other {# employees}}': '{count, plural, zero {# موظف} one {موظف واحد} two {موظفان} few {# موظفين} many {# موظفًا} other {# موظف}}',
  '{count, plural, one {# position} other {# positions}}': '{count, plural, zero {# وظيفة} one {وظيفة واحدة} two {وظيفتان} few {# وظائف} many {# وظيفة} other {# وظيفة}}',
  '{count, plural, one {# department} other {# departments}}': '{count, plural, zero {# قسم} one {قسم واحد} two {قسمان} few {# أقسام} many {# قسمًا} other {# قسم}}',
  '{count, plural, one {# designation} other {# designations}}': '{count, plural, zero {# مسمى وظيفي} one {مسمى وظيفي واحد} two {مسميان وظيفيان} few {# مسميات وظيفية} many {# مسمى وظيفيًا} other {# مسمى وظيفي}}',
  '{count, plural, one {# location} other {# locations}}': '{count, plural, zero {# موقع} one {موقع واحد} two {موقعان} few {# مواقع} many {# موقعًا} other {# موقع}}',
  '{count, plural, one {# branch} other {# branches}}': '{count, plural, zero {# فرع} one {فرع واحد} two {فرعان} few {# فروع} many {# فرعًا} other {# فرع}}',
  '{count, plural, one {# benefit or loan limit} other {# benefit and loan limits}}': '{count, plural, zero {# حد استحقاق} one {حد استحقاق واحد} two {حدا استحقاق} few {# حدود استحقاق} many {# حد استحقاق} other {# حد استحقاق}}',
  '{count, plural, one {# approval workflow} other {# approval workflows}}': '{count, plural, zero {# مسار موافقة} one {مسار موافقة واحد} two {مساران للموافقة} few {# مسارات موافقة} many {# مسار موافقة} other {# مسار موافقة}}',
  '{count, plural, one {# staffing budget} other {# staffing budgets}}': '{count, plural, zero {# ميزانية توظيف} one {ميزانية توظيف واحدة} two {ميزانيتا توظيف} few {# ميزانيات توظيف} many {# ميزانية توظيف} other {# ميزانية توظيف}}',
  'Used by {list}': 'مستخدم في {list}',
  ', ': '، ',

  'Use Remove / Restore to turn it off or on.': 'استخدم «إزالة / استعادة» لإيقافه أو تشغيله.',

  // Review
  'Removed': 'تمت الإزالة',
  '{count} to remove': '{count} للإزالة',
  'Kept as it is. Remove it in Step 4 if you no longer need it.': 'يبقى كما هو. أزله في الخطوة 4 إن لم تعد بحاجة إليه.',
};

export const orgRowRemoval = {
  en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) as Record<string, string>,
  ar,
};
