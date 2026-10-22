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
  "Your role can see or change payroll, banking or access, so your company requires two-step sign-in.":
    "يتيح لك دورك الاطلاع على الرواتب أو البيانات المصرفية أو صلاحيات الوصول أو تغييرها، لذلك تشترط شركتك التحقق بخطوتين.",
  "Your company requires two-step sign-in for everyone.": "تشترط شركتك التحقق بخطوتين على جميع المستخدمين.",
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
    "أفهم أن كلمة المرور وحدها ستحمي هذا الحساب، وأن كل المتصفحات التي وثقت بها ستُنسى.",
  "Turning it off signs you out everywhere else, and you may need to sign in again here.":
    "يؤدي الإيقاف إلى تسجيل خروجك من كل الأجهزة الأخرى، وقد تحتاج إلى تسجيل الدخول مجددًا هنا.",
  "Turn off": "إيقاف",
  "Turning off…": "جارٍ الإيقاف…",
  "That code didn't match. Enter the newest code from your authenticator app. Each code works only once.":
    "الرمز غير مطابق. أدخل أحدث رمز من تطبيق المصادقة. كل رمز يُستخدم مرة واحدة فقط.",
  "Two-step sign-in could not be turned off. Try again in a moment.": "تعذّر إيقاف التحقق بخطوتين. حاول مرة أخرى بعد قليل.",

  // Trusted browsers
  "Trusted browsers": "المتصفحات الموثوقة",
  "{days, plural, one {A trusted browser doesn't ask for a code for # day after you trust it. Forget any browser you don't recognise or no longer use.} other {A trusted browser doesn't ask for a code for # days after you trust it. Forget any browser you don't recognise or no longer use.}}":
    "{days, plural, one {لا يطلب المتصفح الموثوق رمزًا لمدة يوم واحد بعد أن تثق به. انسَ أي متصفح لا تعرفه أو لم تعد تستخدمه.} two {لا يطلب المتصفح الموثوق رمزًا لمدة يومين بعد أن تثق به. انسَ أي متصفح لا تعرفه أو لم تعد تستخدمه.} few {لا يطلب المتصفح الموثوق رمزًا لمدة # أيام بعد أن تثق به. انسَ أي متصفح لا تعرفه أو لم تعد تستخدمه.} many {لا يطلب المتصفح الموثوق رمزًا لمدة # يومًا بعد أن تثق به. انسَ أي متصفح لا تعرفه أو لم تعد تستخدمه.} other {لا يطلب المتصفح الموثوق رمزًا لمدة # يوم بعد أن تثق به. انسَ أي متصفح لا تعرفه أو لم تعد تستخدمه.}}",
  "Your company has switched off trusted browsers, so every sign-in asks for a code.":
    "أوقفت شركتك خاصية المتصفحات الموثوقة، لذلك يُطلب الرمز عند كل تسجيل دخول.",
  "Your trusted browsers could not be loaded.": "تعذّر تحميل المتصفحات الموثوقة.",
  "No trusted browsers. Every sign-in asks for a code.": "لا توجد متصفحات موثوقة. يُطلب الرمز عند كل تسجيل دخول.",
  "This browser": "هذا المتصفح",
  "Unknown browser": "متصفح غير معروف",
  "Trusted since": "موثوق منذ",
  "Not used yet": "لم يُستخدم بعد",
  "Network address": "عنوان الشبكة",
  "Forget": "نسيان",
  "Forget {browser}": "نسيان {browser}",
  "Forgetting…": "جارٍ النسيان…",
  "Forget all browsers": "نسيان كل المتصفحات",
  "Forget all browsers?": "نسيان كل المتصفحات؟",
  "Every browser, this one included, will ask for a code the next time you sign in.":
    "سيطلب كل متصفح، بما فيه هذا المتصفح، رمزًا في المرة القادمة التي تسجّل فيها الدخول.",
  "This browser is forgotten. It will ask for a code the next time you sign in.":
    "تم نسيان هذا المتصفح. سيطلب رمزًا في المرة القادمة التي تسجّل فيها الدخول.",
  "{browser} is forgotten. It will ask for a code the next time you sign in.":
    "تم نسيان {browser}. سيطلب رمزًا في المرة القادمة التي تسجّل فيها الدخول.",
  "That browser was already forgotten. The list is up to date.": "تم نسيان هذا المتصفح من قبل. القائمة محدّثة الآن.",
  "The browser could not be forgotten. Try again in a moment.": "تعذّر نسيان المتصفح. حاول مرة أخرى بعد قليل.",
  "The browsers could not be forgotten. Try again in a moment.": "تعذّر نسيان المتصفحات. حاول مرة أخرى بعد قليل.",
  "All browsers are forgotten. Each will ask for a code the next time you sign in.":
    "تم نسيان كل المتصفحات. سيطلب كل منها رمزًا في المرة القادمة التي تسجّل فيها الدخول.",
};

export const signInSecurity = { ar, en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) };
