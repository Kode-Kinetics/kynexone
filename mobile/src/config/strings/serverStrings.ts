// The server screen: choosing the KynexOne installation this app talks to (typed or scanned from the QR code in
// Tenant Admin). Merged into i18n under `server.*`.
//
// Rules: every key in `en` must exist in `ar` (tests/strings.test.ts checks parity). Keep this file import-free so
// tests can load it under node --test.

const en = {
  eyebrow: 'Your organisation',
  title: 'Connect to KynexOne',
  subtitle: 'Enter the address your organisation gave you, or scan the QR code your HR or IT team shows you.',
  addressLabel: 'Server address',
  addressPlaceholder: 'hr.your-company.com',
  connect: 'Connect',
  scan: 'Scan QR code',
  stopScanning: 'Cancel scan',
  checking: 'Checking the server…',
  connectedTo: 'Connected to {{name}}',
  change: 'Change server',
  cameraDenied: 'Allow camera access to scan the code, or type the address instead.',
  errors: {
    empty: 'Enter the server address.',
    invalid: "That doesn't look like a web address.",
    httpsRequired: 'The address must start with https://.',
    loopback: "That address points at this phone, not at your organisation's server.",
    credentials: "Don't include a user name or password in the address.",
    notKynexOneQr: "That QR code isn't a KynexOne server code.",
    unreachable: "Can't reach that server. Check the address and your connection, then try again.",
    notKynexOne: "That address doesn't answer like a KynexOne server.",
    notSetUp: "That KynexOne server hasn't been set up yet. Ask your IT team.",
  },
};

const ar = {
  eyebrow: 'مؤسستك',
  title: 'الاتصال بـ KynexOne',
  subtitle: 'أدخل العنوان الذي زوّدتك به مؤسستك، أو امسح رمز QR الذي يعرضه لك فريق الموارد البشرية أو تقنية المعلومات.',
  addressLabel: 'عنوان الخادم',
  addressPlaceholder: 'hr.your-company.com',
  connect: 'اتصال',
  scan: 'مسح رمز QR',
  stopScanning: 'إلغاء المسح',
  checking: 'جارٍ التحقق من الخادم…',
  connectedTo: 'متصل بـ {{name}}',
  change: 'تغيير الخادم',
  cameraDenied: 'اسمح بالوصول إلى الكاميرا لمسح الرمز، أو اكتب العنوان بدلاً من ذلك.',
  errors: {
    empty: 'أدخل عنوان الخادم.',
    invalid: 'هذا لا يبدو عنوان ويب صحيحاً.',
    httpsRequired: 'يجب أن يبدأ العنوان بـ https://.',
    loopback: 'هذا العنوان يشير إلى هذا الهاتف، وليس إلى خادم مؤسستك.',
    credentials: 'لا تضع اسم مستخدم أو كلمة مرور في العنوان.',
    notKynexOneQr: 'رمز QR هذا ليس رمز خادم KynexOne.',
    unreachable: 'تعذّر الوصول إلى هذا الخادم. تحقّق من العنوان ومن اتصالك ثم أعد المحاولة.',
    notKynexOne: 'هذا العنوان لا يستجيب كخادم KynexOne.',
    notSetUp: 'لم يتم إعداد خادم KynexOne هذا بعد. تواصل مع فريق تقنية المعلومات.',
  },
};

export const serverStrings = { en, ar };
