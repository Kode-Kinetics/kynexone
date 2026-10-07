import type { ReleaseADict } from './types';

/**
 * User Management → link a login to its employee record, or invite an employee to Self-Service
 * (components/access/LinkEmployeeLoginDialog.tsx). Whole sentences with {placeholders}.
 */
export const employeeLogin: ReleaseADict = {
  en: {
    'Link to employee record': 'Link to employee record',
    'Invite employee': 'Invite employee',
    'Employee: {name} ({code})': 'Employee: {name} ({code})',
    'Login: {email}': 'Login: {email}',
    "Search employees by name, code or work email": "Search employees by name, code or work email",
    "Looking for the employee record that uses {email}…": "Looking for the employee record that uses {email}…",
    "Found automatically: {name} has the work email {email}.": "Found automatically: {name} has the work email {email}.",
    "No employee record has exactly the work email {email}, but these records mention it. Choose the right one below.": "No employee record has exactly the work email {email}, but these records mention it. Choose the right one below.",
    "No employee record has the work email {email}. The employees below have a similar name. Choose one, then check its work email.": "No employee record has the work email {email}. The employees below have a similar name. Choose one, then check its work email.",
    "No employee record has the work email {email}. Search by name above, then set that work email on the record.": "No employee record has the work email {email}. Search by name above, then set that work email on the record.",
    "Choose this employee": "Choose this employee",
    "For example: the login was created before the employee record": "For example: the login was created before the employee record",
    'Checking the employee record…': 'Checking the employee record…',
    'The employee record could not be checked. Try again.': 'The employee record could not be checked. Try again.',
    'Work email': 'Work email',
    'Linked login': 'Linked login',
    'No linked login': 'No linked login',
    'Reason (kept in the audit trail)': 'Reason (kept in the audit trail)',
    'Give a reason. It is kept in the audit trail.': 'Give a reason. It is kept in the audit trail.',
    'Link this login': 'Link this login',
    'Send self-service invitation': 'Send self-service invitation',
    'Working…': 'Working…',
    'The login could not be linked.': 'The login could not be linked.',
    'The invitation could not be sent.': 'The invitation could not be sent.',
    'This employee cannot be linked right now.': 'This employee cannot be linked right now.',
    '{name} has been invited but has not set a password yet. You can send the invitation again.':
      '{name} has been invited but has not set a password yet. You can send the invitation again.',
    'This login is already linked to {name}. Nothing to do.': 'This login is already linked to {name}. Nothing to do.',
    '{name} is already linked to the login {email}.': '{name} is already linked to the login {email}.',
    "{name}'s work email belongs to a different login ({email}). Link that login instead, or correct the work email on the employee record.":
      "{name}'s work email belongs to a different login ({email}). Link that login instead, or correct the work email on the employee record.",
    "The login {email} uses {name}'s work email. Linking it lets {name} use Self-Service with the access the login already has.":
      "The login {email} uses {name}'s work email. Linking it lets {name} use Self-Service with the access the login already has.",
    "This login's email ({email}) does not match {name}'s work email ({workEmail}). Correct the work email on the employee record so they match, or send {name} a self-service invitation instead.":
      "This login's email ({email}) does not match {name}'s work email ({workEmail}). Correct the work email on the employee record so they match, or send {name} a self-service invitation instead.",
    '{name} has no login yet. An invitation to {workEmail} lets them set a password and use Self-Service.':
      '{name} has no login yet. An invitation to {workEmail} lets them set a password and use Self-Service.',
    'Linked to {name}.': 'Linked to {name}.',
    '{name} must sign out and sign in again to see Self-Service.': '{name} must sign out and sign in again to see Self-Service.',
    'Invitation link — copy it now and send it to them yourself': 'Invitation link — copy it now and send it to them yourself',
    'Invitation link': 'Invitation link',
    'Copy link': 'Copy link',
    'Link copied': 'Link copied',
    'This login is still recorded on another employee record. Contact support to resolve it.':
      'This login is still recorded on another employee record. Contact support to resolve it.',
    "Employee: {name}":
      "Employee: {name}",
    "This login is still recorded on {employee}'s employee record. Contact support to resolve it.":
      "This login is still recorded on {employee}'s employee record. Contact support to resolve it.",
    "Only a group-level administrator can link a login that has no company access yet.":
      "Only a group-level administrator can link a login that has no company access yet.",
    "A login already uses this work email, but it is outside your access. An administrator who manages it must link it.":
      "A login already uses this work email, but it is outside your access. An administrator who manages it must link it.",
    'This login works in a different company. Give it access to {company} first, or link it from that company.':
      'This login works in a different company. Give it access to {company} first, or link it from that company.',
    "You have handled this login's credentials (you created it, set its password, or were shown a reset or invitation link for it), so you cannot link it to an employee record. Another administrator must link it.":
      "You have handled this login's credentials (you created it, set its password, or were shown a reset or invitation link for it), so you cannot link it to an employee record. Another administrator must link it.",
    "The work email on this employee record was set by this login itself, so it cannot be linked on it. Have an administrator confirm and set the work email first.":
      "The work email on this employee record was set by this login itself, so it cannot be linked on it. Have an administrator confirm and set the work email first.",
    "You set this employee's work email, so you cannot also issue or link a credential for their login. Another administrator must do it.":
      "You set this employee's work email, so you cannot also issue or link a credential for their login. Another administrator must do it.",
    "The work email on this employee record was set by someone who has handled this login's credentials, so the login cannot be linked to it. Have a different administrator confirm and set the work email first.":
      "The work email on this employee record was set by someone who has handled this login's credentials, so the login cannot be linked to it. Have a different administrator confirm and set the work email first.",
    "Work email set by {name} on {date}.":
      "Work email set by {name} on {date}.",
    "Linked. {name} must set a new password from the invitation.":
      "Linked. {name} must set a new password from the invitation.",
    "Someone other than {name} had handled this login's password, so the old password no longer works.":
      "Someone other than {name} had handled this login's password, so the old password no longer works.",
    "Linking will reset this login's password. {name} will set a new one from an invitation.":
      "Linking will reset this login's password. {name} will set a new one from an invitation.",
  },
  ar: {
    'Link to employee record': 'ربط بسجل موظف',
    'Invite employee': 'دعوة موظف',
    'Employee: {name} ({code})': 'الموظف: {name} ({code})',
    'Login: {email}': 'حساب الدخول: {email}',
    "Search employees by name, code or work email": "ابحث عن الموظفين بالاسم أو الرمز أو البريد الإلكتروني للعمل",
    "Looking for the employee record that uses {email}…": "جارٍ البحث عن سجل الموظف الذي يستخدم {email}…",
    "Found automatically: {name} has the work email {email}.": "تم العثور عليه تلقائياً: البريد الإلكتروني للعمل لدى {name} هو {email}.",
    "No employee record has exactly the work email {email}, but these records mention it. Choose the right one below.": "لا يوجد سجل موظف بالبريد الإلكتروني للعمل {email} تماماً، لكن هذه السجلات تذكره. اختر السجل الصحيح أدناه.",
    "No employee record has the work email {email}. The employees below have a similar name. Choose one, then check its work email.": "لا يوجد سجل موظف بالبريد الإلكتروني للعمل {email}. للموظفين أدناه اسم مشابه. اختر أحدهم، ثم تحقّق من بريده الإلكتروني للعمل.",
    "No employee record has the work email {email}. Search by name above, then set that work email on the record.": "لا يوجد سجل موظف بالبريد الإلكتروني للعمل {email}. ابحث بالاسم أعلاه، ثم أضف هذا البريد الإلكتروني للعمل إلى السجل.",
    "Choose this employee": "اختر هذا الموظف",
    "For example: the login was created before the employee record": "مثال: أُنشئ حساب الدخول قبل سجل الموظف",
    'Checking the employee record…': 'جارٍ التحقق من سجل الموظف…',
    'The employee record could not be checked. Try again.': 'تعذّر التحقق من سجل الموظف. حاول مرة أخرى.',
    'Work email': 'البريد الإلكتروني الوظيفي',
    'Linked login': 'حساب الدخول المرتبط',
    'No linked login': 'لا يوجد حساب دخول مرتبط',
    'Reason (kept in the audit trail)': 'السبب (يُحفظ في سجل التدقيق)',
    'Give a reason. It is kept in the audit trail.': 'اذكر السبب. سيُحفظ في سجل التدقيق.',
    'Link this login': 'ربط حساب الدخول هذا',
    'Send self-service invitation': 'إرسال دعوة الخدمة الذاتية',
    'Working…': 'جارٍ التنفيذ…',
    'The login could not be linked.': 'تعذّر ربط حساب الدخول.',
    'The invitation could not be sent.': 'تعذّر إرسال الدعوة.',
    'This employee cannot be linked right now.': 'لا يمكن ربط هذا الموظف حالياً.',
    '{name} has been invited but has not set a password yet. You can send the invitation again.':
      'تمت دعوة {name} لكنه لم يعيّن كلمة مرور بعد. يمكنك إرسال الدعوة مرة أخرى.',
    'This login is already linked to {name}. Nothing to do.': 'حساب الدخول هذا مرتبط بالفعل بـ {name}. لا حاجة لأي إجراء.',
    '{name} is already linked to the login {email}.': '{name} مرتبط بالفعل بحساب الدخول {email}.',
    "{name}'s work email belongs to a different login ({email}). Link that login instead, or correct the work email on the employee record.":
      'البريد الإلكتروني للعمل الخاص بـ {name} يخص حساب دخول آخر ({email}). اربط ذلك الحساب بدلاً من هذا، أو صحّح البريد الإلكتروني للعمل في سجل الموظف.',
    "The login {email} uses {name}'s work email. Linking it lets {name} use Self-Service with the access the login already has.":
      'حساب الدخول {email} يستخدم البريد الإلكتروني للعمل الخاص بـ {name}. ربطه يتيح لـ {name} استخدام الخدمة الذاتية بالصلاحيات التي يملكها الحساب حالياً.',
    "This login's email ({email}) does not match {name}'s work email ({workEmail}). Correct the work email on the employee record so they match, or send {name} a self-service invitation instead.":
      'البريد الإلكتروني لحساب الدخول هذا ({email}) لا يطابق البريد الإلكتروني للعمل الخاص بـ {name} ({workEmail}). صحّح البريد الإلكتروني للعمل في سجل الموظف ليتطابقا، أو أرسل إلى {name} دعوة الخدمة الذاتية بدلاً من ذلك.',
    '{name} has no login yet. An invitation to {workEmail} lets them set a password and use Self-Service.':
      'لا يملك {name} حساب دخول بعد. الدعوة المرسلة إلى {workEmail} تتيح له تعيين كلمة مرور واستخدام الخدمة الذاتية.',
    'Linked to {name}.': 'تم الربط بـ {name}.',
    '{name} must sign out and sign in again to see Self-Service.': 'يجب على {name} تسجيل الخروج ثم الدخول مرة أخرى ليرى الخدمة الذاتية.',
    'Invitation link — copy it now and send it to them yourself': 'رابط الدعوة — انسخه الآن وأرسله إليه بنفسك',
    'Invitation link': 'رابط الدعوة',
    'Copy link': 'نسخ الرابط',
    'Link copied': 'تم نسخ الرابط',
    'This login is still recorded on another employee record. Contact support to resolve it.':
      'حساب الدخول هذا لا يزال مسجلاً في سجل موظف آخر. تواصل مع الدعم لحل ذلك.',
    "Employee: {name}":
      "الموظف: {name}",
    "This login is still recorded on {employee}'s employee record. Contact support to resolve it.":
      "حساب الدخول هذا لا يزال مسجلاً في سجل الموظف {employee}. تواصل مع الدعم لحل ذلك.",
    "Only a group-level administrator can link a login that has no company access yet.":
      "لا يمكن ربط حساب دخول ليس لديه صلاحية وصول إلى أي شركة بعد إلا من قِبل مسؤول على مستوى المجموعة.",
    "A login already uses this work email, but it is outside your access. An administrator who manages it must link it.":
      "يستخدم حساب دخول هذا البريد الإلكتروني للعمل بالفعل، لكنه خارج نطاق صلاحياتك. يجب أن يربطه مسؤول يدير ذلك الحساب.",
    'This login works in a different company. Give it access to {company} first, or link it from that company.':
      'يعمل حساب الدخول هذا في شركة أخرى. امنحه صلاحية الوصول إلى {company} أولاً، أو اربطه من تلك الشركة.',
    "You have handled this login's credentials (you created it, set its password, or were shown a reset or invitation link for it), so you cannot link it to an employee record. Another administrator must link it.":
      'لقد تعاملت مع بيانات اعتماد حساب الدخول هذا (أنشأته، أو عيّنت كلمة مروره، أو عُرض عليك رابط إعادة تعيين أو دعوة له)، لذا لا يمكنك ربطه بسجل موظف. يجب أن يربطه مسؤول آخر.',
    "The work email on this employee record was set by this login itself, so it cannot be linked on it. Have an administrator confirm and set the work email first.":
      "البريد الإلكتروني للعمل في سجل هذا الموظف عيّنه صاحب حساب الدخول نفسه، لذا لا يمكن الربط بناءً عليه. اطلب من مسؤول تأكيد البريد الإلكتروني للعمل وتعيينه أولاً.",
    "You set this employee's work email, so you cannot also issue or link a credential for their login. Another administrator must do it.":
      "أنت من عيّن البريد الإلكتروني للعمل لهذا الموظف، لذا لا يمكنك أيضاً إصدار بيانات اعتماد لحساب دخوله أو ربطه. يجب أن يقوم بذلك مسؤول آخر.",
    "The work email on this employee record was set by someone who has handled this login's credentials, so the login cannot be linked to it. Have a different administrator confirm and set the work email first.":
      "البريد الإلكتروني للعمل في سجل هذا الموظف عيّنه شخص تعامل مع بيانات اعتماد حساب الدخول هذا، لذا لا يمكن ربط الحساب به. اطلب من مسؤول آخر تأكيد البريد الإلكتروني للعمل وتعيينه أولاً.",
    "Work email set by {name} on {date}.":
      "عيّن {name} البريد الإلكتروني للعمل بتاريخ {date}.",
    "Linked. {name} must set a new password from the invitation.":
      "تم الربط. يجب على {name} تعيين كلمة مرور جديدة من خلال الدعوة.",
    "Someone other than {name} had handled this login's password, so the old password no longer works.":
      "تعامل شخص غير {name} مع كلمة مرور حساب الدخول هذا، لذا لم تعد كلمة المرور القديمة صالحة.",
    "Linking will reset this login's password. {name} will set a new one from an invitation.":
      "سيؤدي الربط إلى إعادة تعيين كلمة مرور حساب الدخول هذا. سيعيّن {name} كلمة مرور جديدة من خلال دعوة.",
  },
};
