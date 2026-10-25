// Sign-in security (views/SignInSecurityPage.tsx, components/account/SignInSecurityCard.tsx), the
// "Trust this browser" choice on the sign-in code step (views/LoginPage.tsx) and the menu entries
// that lead to it (layouts/TopBar.tsx, layouts/Sidebar.tsx). Plain words only: "two-step sign-in"
// («التحقق بخطوتين», as glApiKeys already says), "authenticator app" («تطبيق المصادقة»), "code"
// («الرمز»). Keys already translated elsewhere (Cancel, Close, Copied, Copy key, Expires, Last used,
// Loading…, Try again, Turning on…) are reused, not redefined.
const ar: Record<string, string> = {
  // The sign-in page
  "{days, plural, one {Trust this browser for # day} other {Trust this browser for # days}}":
    "{days, plural, one {الوثوق بهذا المتصفح ليوم واحد} two {الوثوق بهذا المتصفح ليومين} few {الوثوق بهذا المتصفح لمدة # أيام} many {الوثوق بهذا المتصفح لمدة # يومًا} other {الوثوق بهذا المتصفح لمدة # يوم}}",
  "You won't be asked for a code on this browser until then. Don't use on a shared computer.":
    "لن يُطلب منك رمز على هذا المتصفح حتى ذلك الحين. لا تستخدم هذا الخيار على جهاز مشترك.",
  "Two-step sign-in is on, and you were signed out everywhere for your security. Sign in again with your password and a code from your authenticator app.":
    "تم تفعيل التحقق بخطوتين، وسُجّل خروجك من كل الأجهزة حفاظًا على أمانك. سجّل الدخول مجددًا بكلمة المرور ورمز من تطبيق المصادقة.",
  "Two-step sign-in is off, and you were signed out everywhere for your security. Sign in again with your password.":
    "تم إيقاف التحقق بخطوتين، وسُجّل خروجك من كل الأجهزة حفاظًا على أمانك. سجّل الدخول مجددًا بكلمة المرور.",

  "Two-step sign-in": "التحقق بخطوتين",
  "6-digit code": "الرمز المكوّن من 6 أرقام",
  "Lost your phone?": "فقدت هاتفك؟",
  "Ask your company's administrator to reset two-step sign-in, or use one of your backup codes. After a reset, you'll set it up again the next time you sign in.":
    "اطلب من مسؤول النظام في شركتك إعادة تعيين التحقق بخطوتين، أو استخدم أحد رموزك الاحتياطية. بعد إعادة التعيين ستُعدّه من جديد عند تسجيل الدخول التالي.",
  "Set up two-step sign-in": "إعداد التحقق بخطوتين",
  "If you lose this phone, your administrator can reset it, or you can use one of your backup codes.":
    "إذا فقدت هذا الهاتف، يمكن لمسؤول النظام إعادة تعيينه، أو يمكنك استخدام أحد رموزك الاحتياطية.",
  "Two-step sign-in setup expired. Please sign in again.": "انتهت صلاحية إعداد التحقق بخطوتين. يُرجى تسجيل الدخول مرة أخرى.",
  "Two-step sign-in is on. Sign in again to continue.": "تم تفعيل التحقق بخطوتين. يُرجى تسجيل الدخول مرة أخرى للمتابعة.",

  // Menus and the page
  "Sign-in security": "أمان تسجيل الدخول",
  "My account": "حسابي",
  "How you sign in to KynexOne, and the browsers that skip the code.": "طريقة تسجيل دخولك إلى KynexOne، والمتصفحات التي لا تطلب الرمز.",

  // Status
  "Two-step sign-in is on": "التحقق بخطوتين مُفعّل",
  "Two-step sign-in is off": "التحقق بخطوتين غير مُفعّل",
  "When you sign in, you enter your password and then a 6-digit code from the authenticator app on your phone.":
    "عند تسجيل الدخول تُدخل كلمة المرور ثم رمزًا من 6 أرقام من تطبيق المصادقة على هاتفك.",
  "You sign in with your password only. Turn on two-step sign-in to also ask for a 6-digit code from an authenticator app on your phone, so a stolen password is not enough.":
    "تسجّل الدخول الآن بكلمة المرور فقط. فعّل التحقق بخطوتين ليُطلب أيضًا رمز من 6 أرقام من تطبيق المصادقة على هاتفك، فلا تكفي كلمة مرور مسروقة للدخول.",
  "Turn on two-step sign-in": "تفعيل التحقق بخطوتين",
  "Turn off two-step sign-in": "إيقاف التحقق بخطوتين",
  "Required for your role, because you can see or change payroll, banking or access.":
    "مطلوب لدورك، لأنك تستطيع الاطلاع على الرواتب أو البيانات المصرفية أو صلاحيات الوصول أو تغييرها.",
  "Required by your company for everyone.": "تشترطه شركتك على جميع المستخدمين.",
  "Two-step sign-in is switched off while the product is in testing": "التحقق بخطوتين متوقف أثناء مرحلة اختبار المنتج.",
  "Your sign-in settings could not be loaded.": "تعذّر تحميل إعدادات تسجيل الدخول.",
  "Two-step sign-in is on. You were signed out everywhere else.": "تم تفعيل التحقق بخطوتين. سُجّل خروجك من كل الأجهزة الأخرى.",
  "Two-step sign-in is off. You were signed out everywhere else.": "تم إيقاف التحقق بخطوتين. سُجّل خروجك من كل الأجهزة الأخرى.",
  "Two-step sign-in is already on for your account.": "التحقق بخطوتين مُفعّل لحسابك بالفعل.",
  "Two-step sign-in is already off for your account.": "التحقق بخطوتين غير مُفعّل لحسابك بالفعل.",

  // Turn on
  "Set up your authenticator app": "إعداد تطبيق المصادقة",
  "Open your authenticator app (for example Google Authenticator or Microsoft Authenticator), add an account and scan this QR code.":
    "افتح تطبيق المصادقة (مثل Google Authenticator أو Microsoft Authenticator)، وأضف حسابًا، ثم امسح رمز QR هذا.",
  "QR code to add KynexOne to your authenticator app": "رمز QR لإضافة KynexOne إلى تطبيق المصادقة",
  "The QR code could not be drawn. Enter the key below instead.": "تعذّر عرض رمز QR. أدخل المفتاح أدناه بدلًا منه.",
  "Can't scan it? Enter this key in the app instead.": "لا يمكنك المسح؟ أدخل هذا المفتاح في التطبيق بدلًا من ذلك.",
  "The key could not be copied. Select it and copy it yourself.": "تعذّر نسخ المفتاح. حدّده وانسخه بنفسك.",
  "Enter the 6-digit code the app shows": "أدخل الرمز المكوّن من 6 أرقام الذي يعرضه التطبيق",
  "Turning it on signs you out everywhere else, and you may need to sign in again here.":
    "يؤدي التفعيل إلى تسجيل خروجك من كل الأجهزة الأخرى، وقد تحتاج إلى تسجيل الدخول مجددًا هنا.",
  "Turn on": "تفعيل",
  "Start again": "البدء من جديد",
  "Setup could not start. Try again in a moment.": "تعذّر بدء الإعداد. حاول مرة أخرى بعد قليل.",
  "This setup has expired. Start again to get a new QR code.": "انتهت صلاحية هذا الإعداد. ابدأ من جديد للحصول على رمز QR جديد.",
  "That code didn't match. Enter the newest code from the app, and check that your phone sets its time automatically.":
    "الرمز غير مطابق. أدخل أحدث رمز من التطبيق، وتأكد من أن هاتفك يضبط الوقت تلقائيًا.",
  "Two-step sign-in could not be turned on. Try again in a moment.": "تعذّر تفعيل التحقق بخطوتين. حاول مرة أخرى بعد قليل.",

  // Turn off
  "Current 6-digit code from your authenticator app": "الرمز الحالي المكوّن من 6 أرقام من تطبيق المصادقة",
  "I understand that only my password will protect this account, and every browser I trusted will be forgotten.":
    "أفهم أن كلمة المرور وحدها ستحمي هذا الحساب، وأنه سيُلغى الوثوق بكل المتصفحات التي وثقت بها.",
  "Turning it off signs you out everywhere else, and you may need to sign in again here.":
    "يؤدي الإيقاف إلى تسجيل خروجك من كل الأجهزة الأخرى، وقد تحتاج إلى تسجيل الدخول مجددًا هنا.",
  "Turn off": "إيقاف",
  "Turning off…": "جارٍ الإيقاف…",
  "That code didn't match. Enter the newest code from your authenticator app. Each code works only once.":
    "الرمز غير مطابق. أدخل أحدث رمز من تطبيق المصادقة. كل رمز يُستخدم مرة واحدة فقط.",
  "Two-step sign-in could not be turned off. Try again in a moment.": "تعذّر إيقاف التحقق بخطوتين. حاول مرة أخرى بعد قليل.",

  // Trusted browsers
  "Trusted browsers": "المتصفحات الموثوقة",
  "{days, plural, one {A trusted browser skips the code for # day. Your company sets this period.} other {A trusted browser skips the code for # days. Your company sets this period.}}":
    "{days, plural, one {لا يطلب المتصفح الموثوق الرمز لمدة يوم واحد. تحدّد شركتك هذه المدة.} two {لا يطلب المتصفح الموثوق الرمز لمدة يومين. تحدّد شركتك هذه المدة.} few {لا يطلب المتصفح الموثوق الرمز لمدة # أيام. تحدّد شركتك هذه المدة.} many {لا يطلب المتصفح الموثوق الرمز لمدة # يومًا. تحدّد شركتك هذه المدة.} other {لا يطلب المتصفح الموثوق الرمز لمدة # يوم. تحدّد شركتك هذه المدة.}}",
  "Your company has switched off trusted browsers, so every sign-in asks for a code.":
    "أوقفت شركتك خاصية المتصفحات الموثوقة، لذلك يُطلب الرمز عند كل تسجيل دخول.",
  "Your trusted browsers could not be loaded.": "تعذّر تحميل المتصفحات الموثوقة.",
  "No trusted browsers. Every sign-in asks for a code.": "لا توجد متصفحات موثوقة. يُطلب الرمز عند كل تسجيل دخول.",
  "This browser": "هذا المتصفح",
  "Unknown browser": "متصفح غير معروف",
  "Trusted since": "موثوق منذ",
  "Not used since you trusted it": "لم يُستخدم منذ الوثوق به",
  "IP address": "عنوان IP",
  // "Forget" a browser is «إلغاء الوثوق» (stop trusting), never «نسيان», which reads as a memory lapse.
  "Forget": "إلغاء الوثوق",
  "Forget {browser}": "إلغاء الوثوق بـ {browser}",
  "Forgetting…": "جارٍ إلغاء الوثوق…",
  "Forget all browsers": "إلغاء الوثوق بكل المتصفحات",
  "Forget all browsers?": "إلغاء الوثوق بكل المتصفحات؟",
  "Every browser, this one included, will ask for a code the next time you sign in.":
    "سيُطلب الرمز على كل متصفح، بما فيه هذا المتصفح، عند تسجيل الدخول التالي.",
  "This browser is forgotten. It will ask for a code the next time you sign in.":
    "لم يعد هذا المتصفح موثوقًا. سيُطلب الرمز عند تسجيل الدخول التالي.",
  "{browser} is forgotten. It will ask for a code the next time you sign in.":
    "لم يعد {browser} موثوقًا. سيُطلب الرمز عند تسجيل الدخول التالي.",
  "That browser was already forgotten. The list is up to date.": "سبق إلغاء الوثوق بهذا المتصفح. القائمة محدّثة الآن.",
  "The browser could not be forgotten. Try again in a moment.": "تعذّر إلغاء الوثوق بالمتصفح. حاول مرة أخرى بعد قليل.",
  "The browsers could not be forgotten. Try again in a moment.": "تعذّر إلغاء الوثوق بالمتصفحات. حاول مرة أخرى بعد قليل.",
  "All browsers are forgotten. Each will ask for a code the next time you sign in.":
    "لم يعد أي متصفح موثوقًا. سيُطلب الرمز على كل متصفح عند تسجيل الدخول التالي.",
};

// Backup codes («الرموز الاحتياطية»): the code step, the "Save your backup codes" step, the card.
Object.assign(ar, {
  "Use a backup code instead": "استخدم رمزًا احتياطيًا بدلًا من ذلك",
  "Use the app code instead": "استخدم رمز التطبيق بدلًا من ذلك",
  "Backup code": "الرمز الاحتياطي",
  "Enter one of the backup codes you saved when you turned on two-step sign-in.":
    "أدخل أحد الرموز الاحتياطية التي حفظتها عند تفعيل التحقق بخطوتين.",
  "That backup code didn't work. Check it, or use another one. Each code works once.":
    "لم يعمل هذا الرمز الاحتياطي. تحقّق منه أو استخدم رمزًا آخر، فكل رمز يعمل مرة واحدة.",
  "Enter one of your backup codes.": "أدخل أحد رموزك الاحتياطية.",
  "Save your backup codes": "احفظ رموزك الاحتياطية",
  "If you can't use your phone to sign in, enter one of these codes instead.":
    "إذا تعذّر عليك استخدام هاتفك لتسجيل الدخول، فأدخل أحد هذه الرموز بدلًا منه.",
  "These replace your old backup codes, which no longer work.": "تحلّ هذه الرموز محل رموزك الاحتياطية القديمة، والتي لم تعد تعمل.",
  "Each code works once. Keep them somewhere safe, away from your phone.":
    "يعمل كل رمز مرة واحدة فقط. احتفظ بها في مكان آمن بعيدًا عن هاتفك.",
  "KynexOne backup codes": "الرموز الاحتياطية لحساب KynexOne",
  "Copy all": "نسخ الكل",
  "The codes are copied. Paste them somewhere safe.": "تم نسخ الرموز. الصقها في مكان آمن.",
  "The codes could not be copied. Download them instead.": "تعذّر نسخ الرموز. نزّلها بدلًا من ذلك.",
  "I've saved these codes": "حفظتُ هذه الرموز",
  "Backup codes: {count} left": "الرموز الاحتياطية المتبقية: {count}",
  "Only a few backup codes are left. Get new ones before they run out.":
    "لم يتبقَّ إلا عدد قليل من الرموز الاحتياطية. احصل على رموز جديدة قبل أن تنفد.",
  "You have no backup codes left. Get new ones so you can still sign in without your phone.":
    "لم يتبقَّ لديك أي رمز احتياطي. احصل على رموز جديدة لتتمكن من تسجيل الدخول دون هاتفك.",
  "Get new backup codes": "الحصول على رموز احتياطية جديدة",
  "Your old codes stop working. Only the new ones will sign you in.":
    "ستتوقف رموزك القديمة عن العمل، ولن يُقبل لتسجيل الدخول إلا الرموز الجديدة.",
  "Get new codes": "الحصول على رموز جديدة",
  "Getting new codes…": "جارٍ إنشاء رموز جديدة…",
  "Your new backup codes are ready. The old ones no longer work.": "رموزك الاحتياطية الجديدة جاهزة، ولم تعد الرموز القديمة تعمل.",
  "New backup codes could not be made. Try again in a moment.": "تعذّر إنشاء رموز احتياطية جديدة. حاول مرة أخرى بعد قليل.",
  "Two-step sign-in is off for your account, so there are no backup codes.":
    "التحقق بخطوتين غير مُفعّل لحسابك، لذلك لا توجد رموز احتياطية.",

  // An administrator resets another user's two-step sign-in (User Management → Users).
  "Reset two-step sign-in": "إعادة تعيين التحقق بخطوتين",
  "Reset two-step sign-in?": "إعادة تعيين التحقق بخطوتين؟",
  "{name} will be signed out everywhere and set up two-step sign-in again the next time they sign in. Their trusted browsers and backup codes stop working.":
    "سيُسجَّل خروج {name} من كل الأجهزة، وسيُعدّ التحقق بخطوتين من جديد عند تسجيل الدخول التالي. وستتوقف متصفحاته الموثوقة ورموزه الاحتياطية عن العمل.",
  "For example, a lost or replaced phone. It is kept in the audit log.": "مثل فقدان الهاتف أو استبداله. يُحفظ السبب في سجل التدقيق.",
  "Enter a reason of 3 to 500 characters.": "أدخل سببًا يتراوح بين 3 و500 حرف.",
  "Resetting…": "جارٍ إعادة التعيين…",
  "Two-step sign-in reset for {name}": "تمت إعادة تعيين التحقق بخطوتين لـ {name}",
  "You can't reset your own two-step sign-in. Ask another administrator to do it.":
    "لا يمكنك إعادة تعيين التحقق بخطوتين لحسابك. اطلب ذلك من مسؤول آخر.",
  "Two-step sign-in is already off for {name}. Nothing was changed.": "التحقق بخطوتين غير مُفعّل أصلًا لـ {name}. لم يتغيّر شيء.",
  "{name} is no longer one of your company's users. The list has been refreshed.":
    "لم يعد {name} من مستخدمي شركتك. تم تحديث القائمة.",
  "You don't have permission to reset two-step sign-in for {name}.": "ليست لديك صلاحية إعادة تعيين التحقق بخطوتين لـ {name}.",
  "Two-step sign-in could not be reset. Try again in a moment.": "تعذّرت إعادة تعيين التحقق بخطوتين. حاول مرة أخرى بعد قليل.",

  // Tenant Admin → Security: the company's two-step sign-in policy.
  "Who must use two-step sign-in": "من يجب عليه استخدام التحقق بخطوتين",
  "People who can see or change payroll, banking or access":
    "من يستطيعون الاطلاع على الرواتب أو البيانات المصرفية أو صلاحيات الوصول أو تغييرها",
  "Always required": "مطلوب دائمًا",
  "Also require it for everyone else": "طلبه أيضًا من جميع الآخرين",
  "When you turn this on and save, everyone who hasn't set it up yet is signed out, and sets it up the next time they sign in.":
    "عند تفعيل هذا الخيار والحفظ، يُسجَّل خروج كل من لم يُعدّه بعد، وسيُعدّه عند تسجيل الدخول التالي.",
  "7 days": "7 أيام",
  "14 days": "14 يومًا",
  "30 days (default)": "30 يومًا (الافتراضي)",
  "90 days": "90 يومًا",
  "{days, plural, one {# day (current)} other {# days (current)}}":
    "{days, plural, one {يوم واحد (الحالي)} two {يومان (الحالي)} few {# أيام (الحالي)} many {# يومًا (الحالي)} other {# يوم (الحالي)}}",
  "Shorter is safer. Shortening it also shortens browsers already trusted.":
    "المدة الأقصر أكثر أمانًا. وتقصيرها يُقصّر أيضًا مدة المتصفحات الموثوقة حاليًا.",

  // Tenant Admin → Security: sessions and lockout. Two limits end a session: inactivity, and the
  // maximum session length counted from sign-in («الجلسة»).
  "Sessions and lockout": "الجلسات وقفل الحساب",
  "Sign out after inactivity": "تسجيل الخروج بعد عدم النشاط",
  "(minutes)": "(بالدقائق)",
  "Ends a session nobody has used for this long.": "تنتهي الجلسة التي لم يستخدمها أحد طوال هذه المدة.",
  "Maximum session length": "الحد الأقصى لمدة الجلسة",
  "Everyone signs in again after this long, even if they are active. Shorter is safer.":
    "يسجّل الجميع الدخول من جديد بعد هذه المدة، حتى لو كانوا نشطين. المدة الأقصر أكثر أمانًا.",
  "4 hours": "4 ساعات",
  "8 hours": "8 ساعات",
  "12 hours (default)": "12 ساعة (الافتراضي)",
  "24 hours": "24 ساعة",
  "72 hours (3 days)": "72 ساعة (3 أيام)",
  "168 hours (7 days)": "168 ساعة (7 أيام)",
  "{hours, plural, one {# hour (current)} other {# hours (current)}}":
    "{hours, plural, one {ساعة واحدة (الحالي)} two {ساعتان (الحالي)} few {# ساعات (الحالي)} many {# ساعة (الحالي)} other {# ساعة (الحالي)}}",
  "Maximum session length must be between 1 and 168 hours.": "يجب أن يكون الحد الأقصى لمدة الجلسة بين ساعة واحدة و168 ساعة.",
  "Maximum failed attempts": "الحد الأقصى للمحاولات الفاشلة",
  "Lockout duration": "مدة قفل الحساب",

  // The sign-in page, after a refresh refused because the session reached its maximum length.
  "Your session has ended. Sign in again.": "انتهت جلستك. سجّل الدخول مجددًا.",
});

export const signInSecurity = { ar, en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) };
