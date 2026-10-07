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
    'Search employees by name or code': 'Search employees by name or code',
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
    'This login works in a different company. Give it access to {company} first, or link it from that company.':
      'This login works in a different company. Give it access to {company} first, or link it from that company.',
  },
  ar: {
    'Link to employee record': 'ربط بسجل موظف',
    'Invite employee': 'دعوة موظف',
    'Employee: {name} ({code})': 'الموظف: {name} ({code})',
    'Login: {email}': 'حساب الدخول: {email}',
    'Search employees by name or code': 'ابحث عن الموظفين بالاسم أو الرمز',
    'Checking the employee record…': 'جارٍ التحقق من سجل الموظف…',
    'The employee record could not be checked. Try again.': 'تعذّر التحقق من سجل الموظف. حاول مرة أخرى.',
    'Work email': 'البريد الإلكتروني للعمل',
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
    'This login works in a different company. Give it access to {company} first, or link it from that company.':
      'يعمل حساب الدخول هذا في شركة أخرى. امنحه صلاحية الوصول إلى {company} أولاً، أو اربطه من تلك الشركة.',
  },
};
