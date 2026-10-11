// Setup → GL & rates → ERP API access (components/gl/ErpApiKeysPanel.tsx): machine keys the client's ERP uses to
// read journal export files and confirm or reject them. Keys already translated elsewhere (Active, Expired, Cancel,
// Company, Name, Status, Actions, All companies, Creating…, Downloading…, Expires today) are reused, not redefined.
const ar: Record<string, string> = {
  'ERP API access': 'وصول نظام تخطيط الموارد عبر الواجهة البرمجية',
  'An API key lets your ERP read the journal export files created here and, if you allow it, confirm or reject them. ERP access is off until you create a key, and a key can never be used to sign in or to reach anything else.':
    'يتيح مفتاح الواجهة البرمجية لنظام تخطيط الموارد قراءة ملفات تصدير القيود المنشأة هنا، وتأكيدها أو رفضها إذا سمحت بذلك. يبقى الوصول مغلقًا حتى تنشئ مفتاحًا، ولا يمكن استخدام المفتاح لتسجيل الدخول أو للوصول إلى أي شيء آخر.',
  'To rotate, create a new key, update your ERP, then revoke the old one.':
    'لتبديل المفتاح، أنشئ مفتاحًا جديدًا، وحدّث نظام تخطيط الموارد، ثم ألغِ المفتاح القديم.',
  'Download API description': 'تنزيل وصف الواجهة البرمجية',
  'Create API key': 'إنشاء مفتاح واجهة برمجية',
  'No ERP API keys yet': 'لا توجد مفاتيح واجهة برمجية لنظام تخطيط الموارد بعد',
  'Your ERP cannot reach KynexOne until you create a key.': 'لا يستطيع نظام تخطيط الموارد الوصول إلى KynexOne حتى تنشئ مفتاحًا.',
  'Key starts with': 'بداية المفتاح',
  'What it can do': 'ما يمكنه فعله',
  'Expires': 'ينتهي في',
  'Last used': 'آخر استخدام',
  'Never used': 'لم يُستخدم قط',
  'Revoked': 'ملغى',
  'Revoke': 'إلغاء المفتاح',
  'Revoke {name}': 'إلغاء المفتاح {name}',
  'Revoke {name}?': 'إلغاء المفتاح {name}؟',
  'Revoke key': 'إلغاء المفتاح نهائيًا',
  'Read journal exports': 'قراءة ملفات تصدير القيود',
  'Confirm or reject journal exports': 'تأكيد ملفات تصدير القيود أو رفضها',
  'Created by {name} on {date}': 'أنشأه {name} في {date}',
  'Revoked by {name} on {date}': 'ألغاه {name} في {date}',
  'Revoked on {date}': 'أُلغي في {date}',
  'Only from: {addresses}': 'من هذه العناوين فقط: {addresses}',
  '{count, plural, one {Expires in # day} other {Expires in # days}}':
    '{count, plural, one {ينتهي خلال يوم واحد} two {ينتهي خلال يومين} few {ينتهي خلال # أيام} many {ينتهي خلال # يومًا} other {ينتهي خلال # يوم}}',
  '{count, plural, one {# key expires} other {# keys expire}} within 14 days. Create a replacement and update your ERP before then.':
    '{count, plural, one {ينتهي مفتاح واحد} two {ينتهي مفتاحان} few {تنتهي # مفاتيح} many {ينتهي # مفتاحًا} other {ينتهي # مفتاح}} خلال 14 يومًا. أنشئ مفتاحًا بديلًا وحدّث نظام تخطيط الموارد قبل ذلك.',
  '{date} from {address}': '{date} من العنوان {address}',
  '{name} is revoked. Your ERP can no longer use it.': 'أُلغي المفتاح {name}. لم يعد بإمكان نظام تخطيط الموارد استخدامه.',
  'Your ERP will be refused immediately the next time it uses this key. This cannot be undone.':
    'سيُرفض طلب نظام تخطيط الموارد فورًا في المرة القادمة التي يستخدم فيها هذا المفتاح. لا يمكن التراجع عن ذلك.',
  'This key no longer exists. The list has been refreshed.': 'هذا المفتاح لم يعد موجودًا. تم تحديث القائمة.',
  'Only an administrator can manage ERP API keys. Ask your administrator if your ERP needs access.':
    'يمكن لمسؤول النظام فقط إدارة مفاتيح الواجهة البرمجية. تواصل مع مسؤول النظام إذا كان نظام تخطيط الموارد يحتاج إلى الوصول.',
  'Turn on two-step sign-in to manage ERP API keys': 'فعّل التحقق بخطوتين لإدارة مفاتيح الواجهة البرمجية',
  'An API key gives a machine access to your ledger, so only an administrator with two-step sign-in can create or revoke one. Setting it up takes about a minute; you will sign in again afterwards, then come back here.':
    'يمنح مفتاح الواجهة البرمجية نظامًا آليًا وصولًا إلى دفتر الأستاذ، لذلك لا يستطيع إنشاءه أو إلغاءه إلا مسؤول فعّل التحقق بخطوتين. يستغرق الإعداد دقيقة تقريبًا؛ ستسجّل الدخول مجددًا بعده، ثم عُد إلى هنا.',
  'Set up two-step sign-in': 'إعداد التحقق بخطوتين',
  'Starting…': 'جارٍ البدء…',
  'Create ERP API key': 'إنشاء مفتاح واجهة برمجية لنظام تخطيط الموارد',
  'Create key': 'إنشاء المفتاح',
  'Key name': 'اسم المفتاح',
  'A name your team will recognise, for example "SAP production".': 'اسم يعرفه فريقك، مثل "SAP الإنتاج".',
  'The key only reaches journal exports for this company.': 'لا يصل المفتاح إلا إلى ملفات تصدير القيود لهذه الشركة.',
  'What your ERP may do': 'ما يُسمح لنظام تخطيط الموارد بفعله',
  'Required. Lists and downloads the journal files created here.': 'مطلوب. يعرض ملفات القيود المنشأة هنا وينزّلها.',
  'Optional. Lets your ERP record that it posted a file, or why it refused it.':
    'اختياري. يتيح لنظام تخطيط الموارد تسجيل أنه رحّل الملف، أو سبب رفضه له.',
  'Expires after (days)': 'ينتهي بعد (بالأيام)',
  'The key stops working on {date}.': 'يتوقف المفتاح عن العمل في {date}.',
  'Between 1 and {max} days.': 'بين يوم واحد و{max} يومًا.',
  'Allowed addresses (optional)': 'العناوين المسموح بها (اختياري)',
  'One address or range per line. Leave empty to allow any address. Example: 203.0.113.0/24':
    'عنوان أو نطاق واحد في كل سطر. اتركه فارغًا للسماح بأي عنوان. مثال: 203.0.113.0/24',
  'Enter a name for the key.': 'أدخل اسمًا للمفتاح.',
  'Choose the company this key is for.': 'اختر الشركة التي يخصها هذا المفتاح.',
  'A key must be able to read journal exports.': 'يجب أن يتمكن المفتاح من قراءة ملفات تصدير القيود.',
  'Choose an expiry between 1 and {max} days.': 'اختر مدة انتهاء بين يوم واحد و{max} يومًا.',
  '{value} is not an IP address or range. Put one address or range on each line, for example 203.0.113.0/24.':
    '{value} ليس عنوان IP أو نطاقًا صالحًا. ضع عنوانًا أو نطاقًا واحدًا في كل سطر، مثل 203.0.113.0/24.',
  'Your new ERP API key': 'مفتاح الواجهة البرمجية الجديد',
  'This is the only time the key is shown. Store it in your ERP now. If you lose it, revoke it and create a new one.':
    'هذه هي المرة الوحيدة التي يظهر فيها المفتاح. احفظه في نظام تخطيط الموارد الآن. إذا فقدته، فألغِه وأنشئ مفتاحًا جديدًا.',
  'API key for {name}': 'مفتاح الواجهة البرمجية لـ {name}',
  'Copy key': 'نسخ المفتاح',
  'Copied': 'تم النسخ',
  'Copy failed. Select the key and copy it yourself.': 'تعذّر النسخ. حدّد المفتاح وانسخه بنفسك.',
  'I have stored the key': 'لقد حفظت المفتاح',
};

export const glApiKeys = { ar, en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) };
