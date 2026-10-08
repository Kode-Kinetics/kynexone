import type { ReleaseADict } from './types';

/**
 * Employee sign-in access, HR side: the Self-service card, list column and filters, Add Employee's
 * work email and success panel, "Add work emails", and the bilingual sign-in slip
 * (components/employeeAccess/*). Wording is the Saudi HR / Arabic SME copy deck (contract
 * Amendment 2–3). In Arabic the code is «رمز التفعيل»; HR-facing words never include user, link,
 * access mode, staged or invitation.
 *
 * Shared words already in the dictionaries are reused, not repeated here: Cancel, Close, Print,
 * Save, Saving…, Back, Try again, Checking…, Everyone, and Work email (employeeLogin slice).
 */
const en: Record<string, string> = {
  // States (pill + filter chips)
  'Waiting for work email': 'Waiting for work email',
  'No access yet': 'No access yet',
  'Code given, not signed in yet': 'Code given, not signed in yet',
  'Using KynexOne': 'Using KynexOne',
  'Access stopped': 'Access stopped',
  'Needs admin help': 'Needs admin help',

  // Buttons and confirmations
  'Add work email': 'Add work email',
  'Give access': 'Give access',
  'Give new code': 'Give new code',
  'Reset sign-in': 'Reset sign-in',
  'The old code will stop working. Continue?': 'The old code will stop working. Continue?',
  "Reset {name}'s sign-in? Their current password keeps working until they use the new code. Then print a new slip for them.":
    "Reset {name}'s sign-in? Their current password keeps working until they use the new code. Then print a new slip for them.",
  'Reset and print': 'Reset and print',
  'Some selected employees already have a code. Their old codes will stop working. Continue?':
    'Some selected employees already have a code. Their old codes will stop working. Continue?',

  // Card
  'Self-service': 'Self-service',
  'Checking self-service…': 'Checking self-service…',
  'Self-service could not be checked.': 'Self-service could not be checked.',
  'Add a work email so {name} can sign in.': 'Add a work email so {name} can sign in.',
  'No code has been given yet.': 'No code has been given yet.',
  'The last code expired on {date}.': 'The last code expired on {date}.',
  'Code given by {issuer}. Valid until {date}.': 'Code given by {issuer}. Valid until {date}.',
  'Last signed in on {date}.': 'Last signed in on {date}.',
  'Access stopped because the employee has left the company.': 'Access stopped because the employee has left the company.',
  'Access stopped by a system admin.': 'Access stopped by a system admin.',
  'The company email ending (for example @evostel.com) is not set up. Ask your system admin to add it in company settings.':
    'The company email ending (for example @evostel.com) is not set up. Ask your system admin to add it in company settings.',
  'This email is already used to sign in by someone else. Ask your system admin to fix it.':
    'This email is already used to sign in by someone else. Ask your system admin to fix it.',

  // Skip reasons
  'No work email yet.': 'No work email yet.',
  'Access has been stopped for this employee.': 'Access has been stopped for this employee.',
  "You can't give access to yourself.": "You can't give access to yourself.",
  'This person has more permissions than you.': 'This person has more permissions than you.',
  'This person has admin permissions. A security admin must reset their sign-in.':
    'This person has admin permissions. A security admin must reset their sign-in.',
  'This needs a system admin first.': 'This needs a system admin first.',
  "You set this person's work email, so another HR colleague must give access.":
    "You set this person's work email, so another HR colleague must give access.",
  "Use Reset sign-in on the person's profile.": "Use Reset sign-in on the person's profile.",
  'Reset sign-in is done one person at a time, from their profile.': 'Reset sign-in is done one person at a time, from their profile.',
  'Waiting for approval, so not included: {n}.': 'Waiting for approval, so not included: {n}.',
  "Your company's KynexOne plan is full.": "Your company's KynexOne plan is full.",
  "Ask an HR Manager to reset this person's sign-in.": "Ask an HR Manager to reset this person's sign-in.",
  'Waiting for approval. You can give access once {name} is approved.': 'Waiting for approval. You can give access once {name} is approved.',
  'Email sign-in code': 'Email sign-in code',
  'Employee {id}': 'Employee {id}',
  'Slips ready: {n}. Skipped: {m}.': 'Slips ready: {n}. Skipped: {m}.',
  "Email isn't set up, so print the sign-in slips and hand them out.": "Email isn't set up, so print the sign-in slips and hand them out.",
  'Sign-in codes emailed: {n}.': 'Sign-in codes emailed: {n}.',
  'You entered these work emails, so print the slips and hand them over in person.':
    'You entered these work emails, so print the slips and hand them over in person.',
  'The sign-in codes could not be created': 'The sign-in codes could not be created',

  // Employees list and Add Employee
  'Print sign-in slips ({n})': 'Print sign-in slips ({n})',
  'Email sign-in codes ({n})': 'Email sign-in codes ({n})',
  'Add work emails': 'Add work emails',
  'This is also how they sign in to KynexOne.': 'This is also how they sign in to KynexOne.',
  'Use': 'Use',
  'Work email must end in @{domain}.': 'Work email must end in @{domain}.',
  "Work email can't contain '+'.": "Work email can't contain '+'.",
  'Work email can only use English letters, numbers, dots, dashes and underscores before the @.':
    'Work email can only use English letters, numbers, dots, dashes and underscores before the @.',
  '{name} has been added.': '{name} has been added.',
  'Print sign-in slip': 'Print sign-in slip',
  'Later': 'Later',

  // Add work emails
  'Paste two columns from Excel: employee number, then work email. You can also upload a CSV file.':
    'Paste two columns from Excel: employee number, then work email. You can also upload a CSV file.',
  'Employee numbers and work emails': 'Employee numbers and work emails',
  'Upload a CSV file': 'Upload a CSV file',
  'Check the list': 'Check the list',
  '{count, plural, one {# row found.} other {# rows found.}}': '{count, plural, one {# row found.} other {# rows found.}}',
  '{count, plural, one {# line could not be read.} other {# lines could not be read.}}':
    '{count, plural, one {# line could not be read.} other {# lines could not be read.}}',
  'Send at most {max} rows at a time.': 'Send at most {max} rows at a time.',
  'Ready: {a} · Not found: {b} · Wrong email ending: {c} · Already used: {d}':
    'Ready: {a} · Not found: {b} · Wrong email ending: {c} · Already used: {d}',
  'No employee has the number {code}.': 'No employee has the number {code}.',
  '{email} does not end in @{domain}.': '{email} does not end in @{domain}.',
  'This email is already used by another employee.': 'This email is already used by another employee.',
  "This person already signs in with {username}; their sign-in won't change.":
    "This person already signs in with {username}; their sign-in won't change.",
  'This row repeats an employee number or email from earlier in the list.':
    'This row repeats an employee number or email from earlier in the list.',
  'Save ({n})': 'Save ({n})',
  '{count, plural, one {# work email saved.} other {# work emails saved.}}': '{count, plural, one {# work email saved.} other {# work emails saved.}}',
  'Give access to these employees now ({n})?': 'Give access to these employees now ({n})?',
  'Not now': 'Not now',

  // Slip print view and the slip
  'Sign-in slips': 'Sign-in slips',
  'These slips contain codes that will not be shown again after you close this page. If printing fails, give a new code.':
    'These slips contain codes that will not be shown again after you close this page. If printing fails, give a new code.',
  'Preparing the slips…': 'Preparing the slips…',
  'Sheet {number}': 'Sheet {number}',
  'Your KynexOne sign-in': 'Your KynexOne sign-in',
  'Employee number': 'Employee number',
  'Your username': 'Your username',
  'Your welcome code': 'Your welcome code',
  'QR code that opens the welcome page': 'QR code that opens the welcome page',
  'Valid until {date}': 'Valid until {date}',
  'Scan the QR code with your phone camera. No camera? Open {appUrl} and tap "First time? Use your welcome code".':
    'Scan the QR code with your phone camera. No camera? Open {appUrl} and tap "First time? Use your welcome code".',
  'Choose your own password. Use at least 10 characters.': 'Choose your own password. Use at least 10 characters.',
  'Next time, sign in with your username and your password.': 'Next time, sign in with your username and your password.',
  "Use this code once. Don't share it. HR will never ask for your password.":
    "Use this code once. Don't share it. HR will never ask for your password.",
  'If you need help, contact your HR team.': 'If you need help, contact your HR team.',
};

const ar: Record<string, string> = {
  'Waiting for work email': 'بانتظار البريد الوظيفي',
  'No access yet': 'لم يُمنح الدخول بعد',
  'Code given, not signed in yet': 'تم تسليم الرمز، ولم يتم تسجيل الدخول بعد',
  'Using KynexOne': 'الدخول مُفعَّل',
  'Access stopped': 'تم إيقاف الدخول',
  'Needs admin help': 'بحاجة إلى مسؤول النظام',

  'Add work email': 'إضافة البريد الإلكتروني الوظيفي',
  'Give access': 'منح الدخول',
  'Give new code': 'إصدار رمز جديد',
  'Reset sign-in': 'إعادة ضبط الدخول',
  'The old code will stop working. Continue?': 'سيتوقف الرمز السابق عن العمل. هل تريد المتابعة؟',
  "Reset {name}'s sign-in? Their current password keeps working until they use the new code. Then print a new slip for them.":
    'هل تريد إعادة ضبط دخول {name}؟ تبقى كلمة المرور الحالية صالحة حتى يستخدم الرمز الجديد، ثم تُطبع ورقة دخول جديدة.',
  'Reset and print': 'إعادة الضبط والطباعة',
  'Some selected employees already have a code. Their old codes will stop working. Continue?':
    'لدى بعض الموظفين المحددين رمز سابق، وسيتوقف عن العمل. هل تريد المتابعة؟',

  'Self-service': 'الخدمة الذاتية',
  'Checking self-service…': 'جارٍ التحقق من الخدمة الذاتية…',
  'Self-service could not be checked.': 'تعذّر التحقق من الخدمة الذاتية.',
  'Add a work email so {name} can sign in.': 'أضف البريد الإلكتروني الوظيفي ليتمكّن {name} من تسجيل الدخول.',
  'No code has been given yet.': 'لم يُصدَر رمز تفعيل بعد.',
  'The last code expired on {date}.': 'انتهت صلاحية آخر رمز بتاريخ {date}.',
  'Code given by {issuer}. Valid until {date}.': 'أصدر {issuer} الرمز، وهو صالح حتى {date}.',
  'Last signed in on {date}.': 'آخر تسجيل دخول بتاريخ {date}.',
  'Access stopped because the employee has left the company.': 'تم إيقاف الدخول بسبب انتهاء خدمة الموظف.',
  'Access stopped by a system admin.': 'تم إيقاف الدخول من قِبل مسؤول النظام.',
  'The company email ending (for example @evostel.com) is not set up. Ask your system admin to add it in company settings.':
    'لم يُحدَّد نطاق البريد الإلكتروني للشركة (مثل ‎@evostel.com). يُرجى الطلب من مسؤول النظام إضافته في إعدادات الشركة.',
  'This email is already used to sign in by someone else. Ask your system admin to fix it.':
    'هذا البريد مستخدم لتسجيل دخول شخص آخر. يُرجى التواصل مع مسؤول النظام لمعالجته.',

  'No work email yet.': 'لا يوجد بريد إلكتروني وظيفي بعد.',
  'Access has been stopped for this employee.': 'تم إيقاف دخول هذا الموظف.',
  "You can't give access to yourself.": 'لا يمكنك إصدار رمز لنفسك.',
  'This person has more permissions than you.': 'لدى هذا الشخص صلاحيات أعلى من صلاحياتك.',
  'This person has admin permissions. A security admin must reset their sign-in.':
    'لدى هذا الشخص صلاحيات إدارية، ويجب أن يعيد مسؤول الأمن ضبط دخوله.',
  'This needs a system admin first.': 'يتطلب ذلك تدخّل مسؤول النظام أولاً.',
  "You set this person's work email, so another HR colleague must give access.":
    'أنت من أدخل البريد الوظيفي لهذا الموظف، لذا يجب أن يمنح الدخول زميل آخر في الموارد البشرية.',
  "Use Reset sign-in on the person's profile.": 'استخدم إعادة ضبط الدخول من ملف الموظف.',
  'Reset sign-in is done one person at a time, from their profile.': 'تتم إعادة ضبط الدخول لموظف واحد في كل مرة، من ملفه.',
  'Waiting for approval, so not included: {n}.': 'بانتظار الاعتماد، لذا لم يُدرَجوا: {n}.',
  "Your company's KynexOne plan is full.": 'اكتمل عدد المستخدمين في باقة KynexOne الخاصة بشركتك.',
  "Ask an HR Manager to reset this person's sign-in.": 'اطلب من مدير الموارد البشرية إعادة ضبط دخول هذا الموظف.',
  'Waiting for approval. You can give access once {name} is approved.': 'بانتظار الاعتماد. يمكنك منح الدخول بعد اعتماد {name}.',
  'Email sign-in code': 'إرسال رمز التفعيل بالبريد الإلكتروني',
  'Employee {id}': 'الموظف {id}',
  'Slips ready: {n}. Skipped: {m}.': 'الأوراق الجاهزة: {n}. تم تخطي: {m}.',
  "Email isn't set up, so print the sign-in slips and hand them out.": 'إرسال البريد الإلكتروني غير مُفعَّل، لذا اطبع أوراق تسجيل الدخول وسلّمها للموظفين.',
  'Sign-in codes emailed: {n}.': 'تم إرسال رموز التفعيل بالبريد الإلكتروني: {n}.',
  'You entered these work emails, so print the slips and hand them over in person.':
    'أنت من أدخل هذه العناوين، لذا اطبع الأوراق وسلّمها للموظفين شخصياً.',
  'The sign-in codes could not be created': 'تعذّر إصدار رموز التفعيل',

  'Print sign-in slips ({n})': 'طباعة أوراق تسجيل الدخول ({n})',
  'Email sign-in codes ({n})': 'إرسال رموز التفعيل بالبريد الإلكتروني ({n})',
  'Add work emails': 'إضافة البريد الإلكتروني الوظيفي',
  'This is also how they sign in to KynexOne.': 'ويُستخدم أيضاً لتسجيل الدخول إلى KynexOne.',
  'Use': 'استخدام',
  'Work email must end in @{domain}.': 'يجب أن ينتهي البريد الإلكتروني الوظيفي بـ ‎@{domain}.',
  "Work email can't contain '+'.": "لا يمكن أن يحتوي البريد الوظيفي على علامة '+'.",
  'Work email can only use English letters, numbers, dots, dashes and underscores before the @.':
    'يمكن أن يحتوي البريد الإلكتروني الوظيفي قبل علامة @ على أحرف إنجليزية وأرقام ونقاط وشرطات فقط.',
  '{name} has been added.': 'تمت إضافة {name}.',
  'Print sign-in slip': 'طباعة ورقة تسجيل الدخول',
  'Later': 'لاحقاً',

  'Paste two columns from Excel: employee number, then work email. You can also upload a CSV file.':
    'الصق عمودين من Excel: الرقم الوظيفي ثم البريد الإلكتروني الوظيفي. ويمكنك أيضاً رفع ملف CSV.',
  'Employee numbers and work emails': 'الأرقام الوظيفية وعناوين البريد الإلكتروني الوظيفي',
  'Upload a CSV file': 'رفع ملف CSV',
  'Check the list': 'التحقق من القائمة',
  '{count, plural, one {# row found.} other {# rows found.}}': '{count, plural, one {تم العثور على صف واحد.} other {عدد الصفوف: #.}}',
  '{count, plural, one {# line could not be read.} other {# lines could not be read.}}':
    '{count, plural, one {تعذّرت قراءة سطر واحد.} other {عدد الأسطر التي تعذّرت قراءتها: #.}}',
  'Send at most {max} rows at a time.': 'الحد الأقصى في كل مرة: {max} صف.',
  'Ready: {a} · Not found: {b} · Wrong email ending: {c} · Already used: {d}':
    'جاهز: {a} · غير موجود: {b} · نطاق بريد خاطئ: {c} · مستخدم مسبقاً: {d}',
  'No employee has the number {code}.': 'لا يوجد موظف بالرقم الوظيفي {code}.',
  '{email} does not end in @{domain}.': 'البريد {email} لا ينتهي بـ ‎@{domain}.',
  'This email is already used by another employee.': 'هذا البريد مستخدم لموظف آخر.',
  "This person already signs in with {username}; their sign-in won't change.": 'هذا الموظف يسجّل الدخول بالفعل باسم {username}، ولن يتغيّر.',
  'This row repeats an employee number or email from earlier in the list.': 'هذا الصف يكرّر رقماً وظيفياً أو بريداً إلكترونياً ورد سابقاً في القائمة.',
  'Save ({n})': 'حفظ ({n})',
  '{count, plural, one {# work email saved.} other {# work emails saved.}}':
    '{count, plural, one {تم حفظ بريد وظيفي واحد.} other {عدد عناوين البريد الوظيفي المحفوظة: #.}}',
  'Give access to these employees now ({n})?': 'هل تريد منح الدخول لهؤلاء الموظفين الآن ({n})؟',
  'Not now': 'ليس الآن',

  'Sign-in slips': 'أوراق تسجيل الدخول',
  'These slips contain codes that will not be shown again after you close this page. If printing fails, give a new code.':
    'تحتوي هذه الأوراق على رموز لن تظهر مرة أخرى بعد إغلاق الصفحة. إذا تعذّرت الطباعة، أصدر رمزاً جديداً.',
  'Preparing the slips…': 'جارٍ تجهيز الأوراق…',
  'Sheet {number}': 'الورقة {number}',
  'Your KynexOne sign-in': 'بيانات الدخول إلى KynexOne',
  'Employee number': 'الرقم الوظيفي',
  'Your username': 'اسم المستخدم',
  'Your welcome code': 'رمز التفعيل',
  'QR code that opens the welcome page': 'رمز QR يفتح صفحة الترحيب',
  'Valid until {date}': 'صالح حتى {date}',
  'Scan the QR code with your phone camera. No camera? Open {appUrl} and tap "First time? Use your welcome code".':
    'امسح رمز QR بكاميرا جوالك. إذا لم تتمكن، افتح {appUrl} واضغط «أول مرة؟ استخدم رمز التفعيل».',
  'Choose your own password. Use at least 10 characters.': 'اختر كلمة مرور خاصة بك، لا تقل عن 10 خانات.',
  'Next time, sign in with your username and your password.': 'في المرات القادمة، سجّل الدخول باسم المستخدم وكلمة المرور.',
  "Use this code once. Don't share it. HR will never ask for your password.":
    'استخدم هذا الرمز مرة واحدة فقط. لا تشاركه مع أحد. لن تطلب منك الموارد البشرية كلمة المرور أبداً.',
  'If you need help, contact your HR team.': 'للمساعدة، يُرجى التواصل مع فريق الموارد البشرية.',
};

export const employeeAccess: ReleaseADict = { en, ar };
