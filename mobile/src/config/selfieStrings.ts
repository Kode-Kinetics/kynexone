// Selfie attendance and location-check strings, merged into i18n as the `selfie` namespace.
// Kept import-free so tests can check EN/AR key parity and the refusal-key mapping under node.
//
// Wording rules: no face matching is performed, so never say "face verified" — say "selfie attached".
// Arabic: a punch is تسجيل الحضور (never بصمة, which reads as a fingerprint). Nothing implies biometrics
// beyond one photo. Withdrawal is never offered as the way past a required selfie (review 2, item 8).
// Retention mirrors the server's purge job (selfie attendance v2 contract, rule 7).

export const selfieEn = {
  consent: {
    eyebrow: 'Attendance',
    title: 'Selfie attendance',
    subtitle: 'Your choice. You can say no, or change your mind at any time.',
    whatTitle: 'What is captured',
    whatBody:
      'When you clock in or out, the app takes one photo of your face with the front camera. The photo is attached to that punch. Your location is checked as usual.',
    whyTitle: 'Why',
    whyBody:
      'Your company uses the photo to confirm that you made the punch yourself. Nobody compares your face automatically: there is no face recognition.',
    whoTitle: 'Where it is kept',
    whoBody: 'In your company’s KynexOne account, for attendance review only. The photo is not kept on your phone.',
    retentionTitle: 'How long it is kept',
    retentionBody:
      'Each photo is deleted 90 days after the payroll for that month is closed, and no later than 120 days after the work day if payroll is not closed by then. A photo that was never attached to a punch is deleted within 24 hours.',
    choiceTitle: 'If you say no',
    choiceBody:
      'You can still clock in and out without a selfie, exactly as before. Saying no, or withdrawing later, has no effect on your attendance.',
    policyVersion: 'Consent text version {{version}}',
    agree: 'I agree to selfie attendance',
    notNow: 'Not now',
    withdraw: 'Withdraw my consent',
    withdrawConfirmTitle: 'Withdraw consent?',
    withdrawConfirmBody:
      'From now on you will clock in and out without a selfie. Photos not yet used for attendance are deleted; photos attached to past punches are deleted on the normal schedule.',
    withdrawConfirm: 'Withdraw',
    withdrawDeletesUnused: 'Withdrawing deletes any photos not yet used for attendance.',
    givenOn: 'You agreed on {{date}}.',
    givenStatus: 'You have agreed to selfie attendance.',
    notGivenStatus: 'You have not agreed. You clock in and out without a selfie.',
    agreedToast: 'Thank you. A selfie will be part of your punches from now on.',
    withdrawnToast: 'Consent withdrawn. You will clock in and out without a selfie.',
    withdrawnDeletedToast:
      'Consent withdrawn. Your photos not yet used for attendance have been deleted. You will clock in and out without a selfie.',
    withdrawnPendingToast:
      'Consent withdrawn. Your photos not yet used for attendance will be deleted within about 15 minutes. You will clock in and out without a selfie.',
    versionChanged: 'The consent text has been updated. Please read it again before agreeing.',
    featureOff: 'Your company does not use selfie attendance. Nothing is captured.',
    unavailable: 'We couldn’t load your selfie attendance settings. Check your connection and try again.',
    requiredNote: 'Your company asks employees who agreed to attach a selfie to each punch they make from the app.',
  },
  demo: {
    label: 'Demo',
    notice: 'Demo: photos are stored outside Saudi Arabia and deleted automatically 7 days after they are taken.',
  },
  card: {
    consentNeeded: 'Your company offers selfie attendance. Read what it means before deciding.',
    consentNeededAction: 'Read and decide',
    selfieOn: 'A selfie is attached to your punches.',
    selfieOptional: 'You can attach a selfie to your punches.',
    manage: 'Manage',
    geofenceOn: 'Your punch is checked against your work site location.',
  },
  settingsRow: {
    title: 'Selfie attendance',
    on: 'You agreed · tap to review or withdraw',
    off: 'Not agreed · tap to read and decide',
  },
  capture: {
    title: 'Selfie for {{action}}',
    clockIn: 'clock in',
    clockOut: 'clock out',
    hint: 'Hold your phone at eye level and look at the screen.',
    take: 'Take selfie',
    retake: 'Retake',
    use: 'Use this photo',
    skip: 'Clock without a selfie',
    close: 'Close selfie',
    uploading: 'Attaching your selfie…',
    preparing: 'Starting the camera…',
    cameraNeededTitle: 'Camera access needed',
    cameraNeededBody: 'Allow camera access to take your attendance selfie, or clock without one.',
    cameraNeededRequiredBody:
      'Your company asks for a selfie with each punch you make from the app. Allow camera access to take it. If you can’t use the camera, contact HR.',
    cameraProblem: 'The camera could not take the photo. Try again.',
    cameraProblemRequired: 'The camera could not take the photo. Try again. If it keeps happening, contact HR.',
    tryAgain: 'Try again',
    allowCamera: 'Allow camera',
    openSettings: 'Open settings',
    privacy: 'The photo is sent once and deleted from this phone straight away.',
  },
  punch: {
    locationNeededTitle: 'Location needed',
    locationNeededBody: 'Your company checks where you clock in. Allow location access for KynexOne and try again.',
    locating: 'Finding your location…',
    selfieAttached: 'Selfie attached.',
    successTitle: 'Attendance recorded',
    tryAgain: 'Try again',
    takeNewSelfie: 'Take a new selfie',
    takeSelfie: 'Take a selfie',
    withoutSelfie: 'Clock without a selfie',
    openSettings: 'Open settings',
    requiredHelp: 'Wait a moment and try again. If you can’t take a selfie, contact HR.',
    ok: 'OK',
  },
  refusal: {
    outsideGeofence: {
      title: 'You are outside your work site',
      message: 'This punch was made too far from your work site.',
      next: 'Move closer to your site and try again.',
    },
    locationInaccurate: {
      title: 'Location not precise enough',
      message: 'Your phone could not find your location precisely enough.',
      next: 'Turn on precise location for KynexOne, move near a window or outside, and try again.',
    },
    locationMocked: {
      title: 'Location looks simulated',
      message: 'Your phone reported a location from a mock-location app.',
      next: 'Turn off any mock-location or GPS app, then try again.',
    },
    locationRequired: {
      title: 'Location needed',
      message: 'Your company checks where you clock in, and no location was received.',
      next: 'Allow location access for KynexOne and try again.',
    },
    locationInvalid: {
      title: 'Location could not be read',
      message: 'The location from your phone was not valid.',
      next: 'Wait a moment for your phone to find your location, then try again.',
    },
    siteMissing: {
      title: 'Your work site is not set up',
      message: 'Your company checks punch locations, but no work site location has been set up yet.',
      next: 'Ask HR to add your work site location. Then try again.',
    },
    evidenceExpired: {
      title: 'Selfie expired',
      message: 'A selfie can be used for 10 minutes. This one is too old.',
      next: 'Take a new selfie.',
    },
    evidenceUsed: {
      title: 'Selfie already used',
      message: 'Each selfie can be attached to one punch only.',
      next: 'Take a new selfie.',
    },
    evidenceNotFound: {
      title: 'Selfie not found',
      message: 'We could not find the selfie for this punch.',
      next: 'Take a new selfie.',
    },
    consentRequired: {
      title: 'No selfie consent on record',
      message: 'A selfie can only be used after you agree to selfie attendance.',
      next: 'Clock without a selfie, or review your consent first.',
    },
    selfieNotEnabled: {
      title: 'Selfie attendance is off',
      message: 'Your company does not use selfie attendance at the moment.',
      next: 'Clock without a selfie.',
    },
    selfieRequired: {
      title: 'A selfie is needed',
      message: 'Your company asks for a selfie with each punch you make from the app. Take a selfie and try again.',
      next: 'Take a selfie and try again.',
    },
    selfieRateLimited: {
      title: 'Too many selfies',
      message: 'You have taken the maximum number of selfies for this hour.',
      next: 'Clock without a selfie, or wait a while and try again.',
    },
    selfieUnusable: {
      title: 'Photo could not be used',
      message: 'The selfie could not be read or was too large.',
      next: 'Take the selfie again.',
    },
    accessMode: {
      title: 'Not available for this sign-in',
      message: 'This type of sign-in cannot use selfie attendance.',
      next: 'Clock without a selfie, or ask HR about your access.',
    },
    notLinked: {
      title: 'Your login is not linked to an employee',
      message: 'Attendance needs a login linked to your employee record.',
      next: 'Ask HR to link your login to your employee record.',
    },
    appUpdateRequired: {
      title: 'Update the app',
      message: 'Please update the KynexOne app to record attendance at your site.',
      next: 'Update KynexOne from the App Store or Google Play, then try again.',
    },
    mobileAppRequired: {
      title: 'Use the mobile app',
      message: 'Your company requires attendance from the mobile app at your site.',
      next: 'Clock in with the KynexOne mobile app at your site.',
    },
    selfieBusy: {
      title: 'Selfie processing is busy',
      message: 'Selfie processing is busy, try again in a moment.',
      next: 'Wait a moment, then try again.',
    },
    selfieInProgress: {
      title: 'A selfie is already being sent',
      message: 'A selfie is already being sent, wait a moment.',
      next: 'Wait a moment, then try again.',
    },
    network: {
      title: 'Can’t reach KynexOne',
      message: 'Your punch was not recorded.',
      next: 'Check your connection and try again.',
    },
    unknown: {
      title: 'Attendance not recorded',
      message: 'Something went wrong and your punch was not recorded.',
      next: 'Try again. If it keeps happening, contact HR.',
    },
  },
};

export const selfieAr: typeof selfieEn = {
  consent: {
    eyebrow: 'الحضور',
    title: 'الحضور بالصورة الذاتية',
    subtitle: 'القرار لك. يمكنك الرفض أو تغيير رأيك في أي وقت.',
    whatTitle: 'ما الذي يُلتقط',
    whatBody:
      'عند تسجيل الدخول أو الخروج، يلتقط التطبيق صورة واحدة لوجهك بالكاميرا الأمامية. تُرفق الصورة بتسجيل الحضور ذاك، ويُتحقق من موقعك كالمعتاد.',
    whyTitle: 'لماذا',
    whyBody:
      'تستخدم شركتك الصورة للتأكد من أنك سجّلت حضورك بنفسك. لا تُجرى أي مقارنة آلية لوجهك: لا يوجد تعرّف على الوجه.',
    whoTitle: 'أين تُحفظ',
    whoBody: 'في حساب شركتك على KynexOne، لمراجعة الحضور فقط. لا تُحفظ الصورة على هاتفك.',
    retentionTitle: 'مدة الاحتفاظ',
    retentionBody:
      'تُحذف كل صورة بعد 90 يومًا من إقفال مسير رواتب ذلك الشهر، وفي موعد أقصاه 120 يومًا من يوم العمل إذا لم يُقفل المسير حتى ذلك الحين. أما الصورة التي لم تُرفق بأي تسجيل حضور فتُحذف خلال 24 ساعة.',
    choiceTitle: 'إذا رفضت',
    choiceBody:
      'يمكنك تسجيل الدخول والخروج دون صورة ذاتية كما في السابق تمامًا. الرفض أو السحب لاحقًا لا يؤثر في حضورك.',
    policyVersion: 'إصدار نص الموافقة {{version}}',
    agree: 'أوافق على الحضور بالصورة الذاتية',
    notNow: 'ليس الآن',
    withdraw: 'سحب موافقتي',
    withdrawConfirmTitle: 'سحب الموافقة؟',
    withdrawConfirmBody:
      'من الآن ستسجّل الدخول والخروج دون صورة ذاتية. تُحذف الصور التي لم تُستخدم بعد في الحضور، أما الصور المرفقة بتسجيلات حضور سابقة فتُحذف وفق الجدول المعتاد.',
    withdrawConfirm: 'سحب',
    withdrawDeletesUnused: 'يؤدي السحب إلى حذف أي صور لم تُستخدم بعد في الحضور.',
    givenOn: 'وافقت بتاريخ {{date}}.',
    givenStatus: 'لقد وافقت على الحضور بالصورة الذاتية.',
    notGivenStatus: 'لم توافق. تسجّل الدخول والخروج دون صورة ذاتية.',
    agreedToast: 'شكرًا لك. ستُرفق صورة ذاتية بتسجيلات حضورك من الآن.',
    withdrawnToast: 'تم سحب الموافقة. ستسجّل الدخول والخروج دون صورة ذاتية.',
    withdrawnDeletedToast:
      'تم سحب الموافقة، وحُذفت صورك التي لم تُستخدم بعد في الحضور. ستسجّل الدخول والخروج دون صورة ذاتية.',
    withdrawnPendingToast:
      'تم سحب الموافقة. ستُحذف صورك التي لم تُستخدم بعد في الحضور خلال 15 دقيقة تقريبًا. ستسجّل الدخول والخروج دون صورة ذاتية.',
    versionChanged: 'تم تحديث نص الموافقة. يُرجى قراءته مرة أخرى قبل الموافقة.',
    featureOff: 'لا تستخدم شركتك الحضور بالصورة الذاتية. لا يُلتقط أي شيء.',
    unavailable: 'تعذّر تحميل إعدادات الحضور بالصورة الذاتية. تحقّق من اتصالك وحاول مرة أخرى.',
    requiredNote: 'تطلب شركتك من الموظفين الموافقين إرفاق صورة ذاتية بكل تسجيل حضور يجرونه من التطبيق.',
  },
  demo: {
    label: 'عرض تجريبي',
    notice: 'عرض تجريبي: تُحفظ الصور خارج المملكة العربية السعودية وتُحذف تلقائيًا بعد 7 أيام من التقاطها.',
  },
  card: {
    consentNeeded: 'تتيح شركتك الحضور بالصورة الذاتية. اطّلع على ما يعنيه قبل أن تقرر.',
    consentNeededAction: 'اقرأ وقرّر',
    selfieOn: 'تُرفق صورة ذاتية بتسجيلات حضورك.',
    selfieOptional: 'يمكنك إرفاق صورة ذاتية بتسجيلات حضورك.',
    manage: 'إدارة',
    geofenceOn: 'يُتحقق من موقع تسجيل حضورك مقارنةً بموقع عملك.',
  },
  settingsRow: {
    title: 'الحضور بالصورة الذاتية',
    on: 'وافقت · اضغط للمراجعة أو السحب',
    off: 'لم توافق · اضغط للقراءة والقرار',
  },
  capture: {
    title: 'صورة ذاتية لـ{{action}}',
    clockIn: 'تسجيل الدخول',
    clockOut: 'تسجيل الخروج',
    hint: 'أمسك هاتفك بمستوى عينيك وانظر إلى الشاشة.',
    take: 'التقاط صورة ذاتية',
    retake: 'إعادة الالتقاط',
    use: 'استخدام هذه الصورة',
    skip: 'التسجيل دون صورة ذاتية',
    close: 'إغلاق الصورة الذاتية',
    uploading: 'جارٍ إرفاق صورتك الذاتية…',
    preparing: 'جارٍ تشغيل الكاميرا…',
    cameraNeededTitle: 'يلزم الوصول إلى الكاميرا',
    cameraNeededBody: 'اسمح بالوصول إلى الكاميرا لالتقاط صورة الحضور، أو سجّل دونها.',
    cameraNeededRequiredBody:
      'تطلب شركتك صورة ذاتية مع كل تسجيل حضور تجريه من التطبيق. اسمح بالوصول إلى الكاميرا لالتقاطها. إذا تعذّر عليك استخدام الكاميرا، تواصل مع الموارد البشرية.',
    cameraProblem: 'تعذّر على الكاميرا التقاط الصورة. حاول مرة أخرى.',
    cameraProblemRequired: 'تعذّر على الكاميرا التقاط الصورة. حاول مرة أخرى، وإذا تكرر ذلك فتواصل مع الموارد البشرية.',
    tryAgain: 'أعد المحاولة',
    allowCamera: 'السماح بالكاميرا',
    openSettings: 'فتح الإعدادات',
    privacy: 'تُرسل الصورة مرة واحدة وتُحذف من هذا الهاتف فورًا.',
  },
  punch: {
    locationNeededTitle: 'يلزم تحديد الموقع',
    locationNeededBody: 'تتحقق شركتك من مكان تسجيل الحضور. اسمح لتطبيق KynexOne بالوصول إلى الموقع وحاول مرة أخرى.',
    locating: 'جارٍ تحديد موقعك…',
    selfieAttached: 'تم إرفاق الصورة الذاتية.',
    successTitle: 'تم تسجيل الحضور',
    tryAgain: 'حاول مرة أخرى',
    takeNewSelfie: 'التقاط صورة ذاتية جديدة',
    takeSelfie: 'التقاط صورة ذاتية',
    withoutSelfie: 'التسجيل دون صورة ذاتية',
    openSettings: 'فتح الإعدادات',
    requiredHelp: 'انتظر قليلًا ثم حاول مرة أخرى. إذا تعذّر عليك التقاط صورة ذاتية، تواصل مع الموارد البشرية.',
    ok: 'حسنًا',
  },
  refusal: {
    outsideGeofence: {
      title: 'أنت خارج موقع عملك',
      message: 'سُجّل هذا الحضور بعيدًا جدًا عن موقع عملك.',
      next: 'اقترب من موقعك وحاول مرة أخرى.',
    },
    locationInaccurate: {
      title: 'الموقع غير دقيق بما يكفي',
      message: 'لم يتمكن هاتفك من تحديد موقعك بدقة كافية.',
      next: 'فعّل الموقع الدقيق لتطبيق KynexOne، واقترب من نافذة أو اخرج إلى مكان مفتوح، ثم حاول مرة أخرى.',
    },
    locationMocked: {
      title: 'يبدو أن الموقع مُحاكى',
      message: 'أبلغ هاتفك عن موقع صادر من تطبيق لمحاكاة الموقع.',
      next: 'أوقف أي تطبيق لمحاكاة الموقع أو تغيير GPS، ثم حاول مرة أخرى.',
    },
    locationRequired: {
      title: 'يلزم تحديد الموقع',
      message: 'تتحقق شركتك من مكان تسجيل الحضور، ولم يصل أي موقع.',
      next: 'اسمح لتطبيق KynexOne بالوصول إلى الموقع وحاول مرة أخرى.',
    },
    locationInvalid: {
      title: 'تعذّرت قراءة الموقع',
      message: 'الموقع الوارد من هاتفك غير صالح.',
      next: 'انتظر قليلًا حتى يحدد هاتفك موقعك، ثم حاول مرة أخرى.',
    },
    siteMissing: {
      title: 'لم يُضبط موقع عملك',
      message: 'تتحقق شركتك من مواقع تسجيل الحضور، لكن لم يُضبط موقع عملك بعد.',
      next: 'اطلب من الموارد البشرية إضافة موقع عملك، ثم حاول مرة أخرى.',
    },
    evidenceExpired: {
      title: 'انتهت صلاحية الصورة الذاتية',
      message: 'يمكن استخدام الصورة الذاتية لمدة 10 دقائق، وهذه الصورة أقدم من ذلك.',
      next: 'التقط صورة ذاتية جديدة.',
    },
    evidenceUsed: {
      title: 'استُخدمت الصورة الذاتية من قبل',
      message: 'يمكن إرفاق كل صورة ذاتية بتسجيل حضور واحد فقط.',
      next: 'التقط صورة ذاتية جديدة.',
    },
    evidenceNotFound: {
      title: 'لم يُعثر على الصورة الذاتية',
      message: 'تعذّر العثور على الصورة الذاتية لتسجيل الحضور هذا.',
      next: 'التقط صورة ذاتية جديدة.',
    },
    consentRequired: {
      title: 'لا توجد موافقة مسجّلة على الصورة الذاتية',
      message: 'لا يمكن استخدام صورة ذاتية إلا بعد موافقتك على الحضور بالصورة الذاتية.',
      next: 'سجّل دون صورة ذاتية، أو راجع موافقتك أولًا.',
    },
    selfieNotEnabled: {
      title: 'الحضور بالصورة الذاتية متوقف',
      message: 'لا تستخدم شركتك الحضور بالصورة الذاتية حاليًا.',
      next: 'سجّل دون صورة ذاتية.',
    },
    selfieRequired: {
      title: 'تلزم صورة ذاتية',
      message: 'تطلب شركتك صورة ذاتية مع كل تسجيل حضور تجريه من التطبيق. التقط صورة ذاتية وحاول مرة أخرى.',
      next: 'التقط صورة ذاتية وحاول مرة أخرى.',
    },
    selfieRateLimited: {
      title: 'صور ذاتية كثيرة',
      message: 'بلغت الحد الأقصى لعدد الصور الذاتية في هذه الساعة.',
      next: 'سجّل دون صورة ذاتية، أو انتظر قليلًا ثم حاول مرة أخرى.',
    },
    selfieUnusable: {
      title: 'تعذّر استخدام الصورة',
      message: 'تعذّرت قراءة الصورة الذاتية أو كان حجمها كبيرًا جدًا.',
      next: 'التقط الصورة الذاتية مرة أخرى.',
    },
    accessMode: {
      title: 'غير متاح لهذا النوع من الدخول',
      message: 'لا يمكن لهذا النوع من الدخول استخدام الحضور بالصورة الذاتية.',
      next: 'سجّل دون صورة ذاتية، أو اسأل الموارد البشرية عن صلاحياتك.',
    },
    notLinked: {
      title: 'حسابك غير مرتبط بموظف',
      message: 'يتطلب الحضور حسابًا مرتبطًا بسجلك الوظيفي.',
      next: 'اطلب من الموارد البشرية ربط حسابك بسجلك الوظيفي.',
    },
    appUpdateRequired: {
      title: 'حدّث التطبيق',
      message: 'يُرجى تحديث تطبيق KynexOne لتسجيل الحضور في موقعك.',
      next: 'حدّث KynexOne من App Store أو Google Play، ثم حاول مرة أخرى.',
    },
    mobileAppRequired: {
      title: 'استخدم تطبيق الجوال',
      message: 'تشترط شركتك تسجيل الحضور من تطبيق الجوال في موقعك.',
      next: 'سجّل حضورك باستخدام تطبيق KynexOne للجوال في موقعك.',
    },
    selfieBusy: {
      title: 'معالجة الصور الذاتية مشغولة',
      message: 'معالجة الصور الذاتية مشغولة، حاول مرة أخرى بعد لحظات.',
      next: 'انتظر لحظة، ثم حاول مرة أخرى.',
    },
    selfieInProgress: {
      title: 'يجري إرسال صورة ذاتية',
      message: 'يجري إرسال صورة ذاتية بالفعل، انتظر لحظة.',
      next: 'انتظر لحظة، ثم حاول مرة أخرى.',
    },
    network: {
      title: 'تعذّر الاتصال بـ KynexOne',
      message: 'لم يُسجَّل حضورك.',
      next: 'تحقّق من اتصالك وحاول مرة أخرى.',
    },
    unknown: {
      title: 'لم يُسجّل الحضور',
      message: 'حدث خطأ ولم يُسجَّل حضورك.',
      next: 'حاول مرة أخرى. إذا تكرر ذلك، تواصل مع الموارد البشرية.',
    },
  },
};
