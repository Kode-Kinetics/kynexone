// Resetting another user's password (components/access/ResetPasswordDialog.tsx), the Users list's readable
// status, access-mode and role labels (views/UserManagementPage.tsx), and the "choose your own password" step
// after a temporary password (views/LoginPage.tsx). "Temporary password" is «كلمة المرور المؤقتة»; "reset link"
// is «رابط إعادة التعيين». Keys already translated elsewhere (Cancel, Close, Copy, Copied, Done, Reason, Send reset
// link, Show/Hide password, Security check, Reset password) are reused, not redefined.
const ar: Record<string, string> = {
  // The dialog
  "Reset password for {name}": "إعادة تعيين كلمة المرور لـ {name}",
  "How should {name} get a new password?": "كيف سيحصل {name} على كلمة مرور جديدة؟",
  "Email a reset link": "إرسال رابط إعادة التعيين بالبريد الإلكتروني",
  "Recommended. {name} chooses their own password. The link works once and expires in 1 hour.":
    "الخيار المُوصى به. يختار {name} كلمة المرور بنفسه. يعمل الرابط مرة واحدة وتنتهي صلاحيته خلال ساعة.",
  "Set a temporary password": "تعيين كلمة مرور مؤقتة",
  "For someone who cannot get the email. Give it to them in person. They must choose their own password when they next sign in, and they are signed out everywhere now.":
    "لمن لا يستطيع استلام البريد الإلكتروني. سلّمها له شخصيًا. سيُطلب منه اختيار كلمة مرور خاصة به عند تسجيل الدخول التالي، ويُسجَّل خروجه من كل الأجهزة الآن.",
  "Set temporary password": "تعيين كلمة المرور المؤقتة",
  "Temporary password": "كلمة المرور المؤقتة",
  "Generate": "إنشاء",
  "A strong one is generated for you. Your company's password rules apply if you type your own.":
    "تُنشأ لك كلمة مرور قوية تلقائيًا. إذا كتبت كلمة مرور بنفسك فستنطبق عليها قواعد كلمات المرور في شركتك.",
  "For example, locked out and confirmed in person. It is kept in the audit log.":
    "مثلًا: قُفل حسابه وتم التحقق منه شخصيًا. يُحفظ السبب في سجل التدقيق.",
  "One-time reset link — copy it now, it is not shown again": "رابط إعادة تعيين لمرة واحدة — انسخه الآن، فلن يظهر مرة أخرى",
  "Temporary password set for {name}. They were signed out everywhere and must choose their own password when they next sign in.":
    "تم تعيين كلمة مرور مؤقتة لـ {name}. سُجّل خروجه من كل الأجهزة، وسيُطلب منه اختيار كلمة مرور خاصة به عند تسجيل الدخول التالي.",
  "Temporary password — give it to them in person, it is not shown again": "كلمة المرور المؤقتة — سلّمها شخصيًا، فلن تظهر مرة أخرى",
  "Use at least 10 characters.": "استخدم 10 أحرف على الأقل.",
  "You don't have permission to reset the password for {name}.": "ليست لديك صلاحية إعادة تعيين كلمة المرور لـ {name}.",
  "The temporary password could not be set. Try again in a moment.": "تعذّر تعيين كلمة المرور المؤقتة. حاول مرة أخرى بعد قليل.",

  // The Users list
  "Password not set yet": "لم تُعيَّن كلمة المرور بعد",
  "No login": "بدون تسجيل دخول",
  "Full portal": "البوابة الكاملة",
  "Self-Service only": "الخدمة الذاتية فقط",
  "Manager Self-Service": "الخدمة الذاتية للمدير",
  "Read-only auditor": "مدقق للقراءة فقط",
  "Mobile app": "تطبيق الجوال",
  "Kiosk only": "جهاز الحضور فقط",
  "{count} more roles: {names}": "{count} أدوار أخرى: {names}",
  "Never": "لم يسجّل الدخول",
  "Invited": "مدعو",
  "Deactivated": "معطّل",

  // Create User: the password typed there is only temporary
  "This is a temporary password. Give it to them in person; they choose their own the first time they sign in.":
    "هذه كلمة مرور مؤقتة. سلّمها للمستخدم شخصيًا، وسيختار كلمة المرور الخاصة به عند أول تسجيل دخول.",

  // The sign-in page: choosing your own password after a temporary one
  "Choose your own password": "اختر كلمة المرور الخاصة بك",
  "Your administrator gave you a temporary password. Choose a new one that only you know to finish signing in.":
    "أعطاك مسؤول النظام كلمة مرور مؤقتة. اختر كلمة مرور جديدة لا يعرفها أحد غيرك لإكمال تسجيل الدخول.",
  "New password": "كلمة المرور الجديدة",
  "At least 10 characters, with upper and lower case letters, a number and a symbol.":
    "10 أحرف على الأقل، تتضمن حروفًا كبيرة وصغيرة ورقمًا ورمزًا.",
  "Confirm new password": "تأكيد كلمة المرور الجديدة",
  "Save and sign in": "حفظ وتسجيل الدخول",
  "The two passwords do not match.": "كلمتا المرور غير متطابقتين.",
  "Password does not meet the workspace security policy.": "كلمة المرور لا تستوفي سياسة الأمان في شركتك.",
  "That took too long. Sign in again with the temporary password.": "انتهت المهلة. سجّل الدخول مجددًا بكلمة المرور المؤقتة.",
  "Your password could not be changed. Try again in a moment.": "تعذّر تغيير كلمة المرور. حاول مرة أخرى بعد قليل.",
  "Your new password is saved. Sign in with it.": "تم حفظ كلمة المرور الجديدة. سجّل الدخول بها.",

  // The workspace owner (Users list)
  "Owner": "المالك",
  "Workspace owner: no other administrator can change this account.": "مالك مساحة العمل: لا يمكن لأي مسؤول آخر تغيير هذا الحساب.",
  "Make owner": "تعيين مالكًا",
  "Make workspace owner?": "تعيين مالك مساحة العمل؟",
  "{name} will become the workspace owner, and no other administrator will be able to change their account. You stay an Admin, but you will no longer be protected.":
    "سيصبح {name} مالك مساحة العمل، ولن يتمكن أي مسؤول آخر من تغيير حسابه. ستبقى مسؤولًا، لكن حسابك لن يكون محميًا بعد ذلك.",

  // "Confirm it's you" before an action against an Admin or the owner
  "Confirm it's you": "تأكيد هويتك",
  "This changes an administrator account. Enter your password to continue.": "هذا الإجراء يغيّر حساب مسؤول. أدخل كلمة المرور للمتابعة.",
  "Your password": "كلمة المرور الخاصة بك",
  "From your authenticator app.": "من تطبيق المصادقة.",
  "Confirm": "تأكيد",
  "That didn't match. Check your password and the newest code from your authenticator app.":
    "لم تتطابق البيانات. تحقّق من كلمة المرور ومن أحدث رمز في تطبيق المصادقة.",
  "That password didn't match. Try again.": "كلمة المرور غير صحيحة. حاول مرة أخرى.",
  "Your account is locked after too many wrong attempts. Try again later.": "تم قفل حسابك بعد محاولات خاطئة كثيرة. حاول لاحقًا.",
  "Could not confirm it is you. Try again in a moment.": "تعذّر التأكد من هويتك. حاول مرة أخرى بعد قليل.",
};

export const userPasswordReset = { ar, en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) };
