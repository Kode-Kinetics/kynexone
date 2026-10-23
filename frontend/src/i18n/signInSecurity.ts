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
  "Ask your company's administrator to reset two-step sign-in. You'll set it up again the next time you sign in.":
    "اطلب من مسؤول النظام في شركتك إعادة تعيين التحقق بخطوتين، وستُعدّه من جديد عند تسجيل الدخول التالي.",
  "Set up two-step sign-in": "إعداد التحقق بخطوتين",
  "If you lose this phone, your administrator can reset it.": "إذا فقدت هذا الهاتف، يمكن لمسؤول النظام إعادة تعيينه.",
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

export const signInSecurity = { ar, en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) };
