// Session, connectivity, app-lock and update-safety copy (`safety.*`). Import-free so
// tests/strings.test.ts can check EN/AR parity under node --test.

const en = {
  errors: {
    forbidden: 'You do not have permission to perform this action.',
    locked: 'Your account is locked. Please contact HR.',
    offline: 'No connection. Check your network and try again.',
    timeout: 'The server took too long to answer. Please try again.',
    server: 'The service is having trouble right now. Please try again shortly.',
  },
  offline: {
    banner: "You're offline. Some information may be out of date.",
    bannerA11y: 'You are offline',
    sessionTitle: "We can't reach KynexOne",
    sessionBody: 'You are still signed in. We will reconnect automatically, or you can try now.',
    retryNow: 'Try now',
    retryingIn: 'Retrying in {{seconds}}s',
    signOut: 'Sign out instead',
  },
  update: {
    title: 'Please update the app',
    body: 'This version of KynexOne no longer matches the server, so some information cannot be shown safely. Update from the store, then open the app again.',
    degraded: 'This section is unavailable until you update the app.',
    dismiss: 'Not now',
  },
  lock: {
    title: 'KynexOne is locked',
    body: 'Verify it is you to see payslips, loans and your profile.',
    unlock: 'Unlock',
    prompt: 'Unlock KynexOne',
    failed: 'Could not verify you. Please try again.',
    notAvailable: 'Set up a screen lock on this device to protect your payroll information.',
    settingsTitle: 'App lock',
    settingsBody: 'Ask for Face ID, fingerprint or your device passcode when you return to the app.',
    timeoutLabel: 'Lock after',
    immediately: 'Immediately',
    minutes: '{{value}} min',
    a11yLocked: 'The app is locked. Double tap Unlock to verify.',
  },
};

const ar: typeof en = {
  errors: {
    forbidden: 'ليست لديك صلاحية تنفيذ هذا الإجراء.',
    locked: 'حسابك مقفل. يرجى التواصل مع الموارد البشرية.',
    offline: 'لا يوجد اتصال. تحقق من الشبكة وحاول مرة أخرى.',
    timeout: 'استغرق الخادم وقتًا طويلًا في الرد. يرجى المحاولة مرة أخرى.',
    server: 'الخدمة تواجه مشكلة حاليًا. يرجى المحاولة بعد قليل.',
  },
  offline: {
    banner: 'أنت غير متصل. قد تكون بعض المعلومات قديمة.',
    bannerA11y: 'أنت غير متصل بالإنترنت',
    sessionTitle: 'تعذّر الوصول إلى KynexOne',
    sessionBody: 'ما زلت مسجّل الدخول. سنعيد الاتصال تلقائيًا، أو يمكنك المحاولة الآن.',
    retryNow: 'حاول الآن',
    retryingIn: 'إعادة المحاولة خلال {{seconds}} ث',
    signOut: 'تسجيل الخروج بدلًا من ذلك',
  },
  update: {
    title: 'يرجى تحديث التطبيق',
    body: 'لم يعد هذا الإصدار من KynexOne متوافقًا مع الخادم، لذلك لا يمكن عرض بعض المعلومات بأمان. حدّث التطبيق من المتجر ثم افتحه مرة أخرى.',
    degraded: 'هذا القسم غير متاح حتى تقوم بتحديث التطبيق.',
    dismiss: 'ليس الآن',
  },
  lock: {
    title: 'تم قفل KynexOne',
    body: 'تحقق من هويتك لعرض قسائم الراتب والقروض وملفك الشخصي.',
    unlock: 'فتح القفل',
    prompt: 'فتح قفل KynexOne',
    failed: 'تعذّر التحقق من هويتك. يرجى المحاولة مرة أخرى.',
    notAvailable: 'فعّل قفل الشاشة على هذا الجهاز لحماية معلومات الرواتب.',
    settingsTitle: 'قفل التطبيق',
    settingsBody: 'اطلب بصمة الوجه أو الإصبع أو رمز الجهاز عند العودة إلى التطبيق.',
    timeoutLabel: 'القفل بعد',
    immediately: 'فورًا',
    minutes: '{{value}} د',
    a11yLocked: 'التطبيق مقفل. اضغط مرتين على فتح القفل للتحقق.',
  },
};

export const safetyStrings = { en, ar };
