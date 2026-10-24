/**
 * Company setup wizard, data-entry steps 2, 3 and 6: format help, hints and suggestions
 * (src/components/companySetup/CompanyStep.tsx, BranchesStep.tsx, AdminStep.tsx; src/lib/companySetupFields.ts).
 * Terms as in ./companyWizard: CR = السجل التجاري; MOL establishment number = رقم المنشأة في وزارة الموارد البشرية;
 * 700 number = الرقم الموحد 700; ZATCA = هيئة الزكاة والضريبة والجمارك.
 */
const ar: Record<string, string> = {
  // Step 2: legal entity
  "As written on the CR certificate.": "كما هو مكتوب في شهادة السجل التجاري.",
  "This name has no Arabic letters. Enter it as written on the CR certificate.": "لا يحتوي هذا الاسم على حروف عربية. أدخله كما هو مكتوب في شهادة السجل التجاري.",
  "The name customers know, if it differs from the legal name.": "الاسم الذي يعرفه العملاء، إن كان مختلفاً عن الاسم القانوني.",
  "Sets the labour law, public holidays and wage-file rules.": "تحدد نظام العمل والعطلات الرسمية وقواعد ملف الأجور.",
  "Filled in from the country. Change it only if salaries are paid in another currency.": "تُعبأ تلقائياً حسب الدولة. غيّرها فقط إذا كانت الرواتب تُدفع بعملة أخرى.",
  "Payroll in KynexOne starts with {month}. You can change it until payroll starts.": "تبدأ الرواتب في KynexOne من {month}. يمكنك تغييره حتى تبدأ الرواتب.",
  "{month} is in the past. You will need opening balances for the months before it.": "{month} شهر مضى. ستحتاج إلى أرصدة افتتاحية للأشهر السابقة له.",
  "Gulf (GCC)": "دول الخليج",
  "Other countries": "دول أخرى",
  // Step 2: registration numbers
  "10 digits, as printed on the CR certificate. Arabic digits are fine.": "10 أرقام كما هي مطبوعة في شهادة السجل التجاري. يمكنك كتابتها بالأرقام العربية.",
  "As printed on the registration certificate or trade licence.": "كما هو مطبوع في شهادة التسجيل أو الرخصة التجارية.",
  "Labour office number, a dash, then the sequence, as shown in Qiwa. Example: 7-1234567. On a phone, type a dot for the dash.": "رقم مكتب العمل، ثم شرطة، ثم الرقم التسلسلي، كما يظهر في منصة قوى. مثال: 7-1234567. على الجوال، اكتب نقطة بدل الشرطة.",
  "Optional. The establishment number your labour ministry gives, if your wage file uses one.": "اختياري. رقم المنشأة الصادر من وزارة العمل، إن كان ملف الأجور يستخدمه.",
  "The establishment’s subscription number on the GOSI portal, digits only.": "رقم اشتراك المنشأة في بوابة التأمينات الاجتماعية، أرقام فقط.",
  "Your social-insurance employer number, if you have one.": "رقم صاحب العمل في التأمينات الاجتماعية، إن وُجد.",
  "The 700 number: 10 digits starting with 7, shown on the CR certificate. Needed for automatic WPS upload with some banks.": "الرقم الموحد 700: عشرة أرقام تبدأ بالرقم 7، ويظهر في شهادة السجل التجاري. تحتاجه بعض البنوك لرفع ملف حماية الأجور تلقائياً.",
  "15 digits, starting and ending with 3, as on the ZATCA VAT certificate.": "15 رقماً تبدأ وتنتهي بالرقم 3، كما في شهادة ضريبة القيمة المضافة من هيئة الزكاة والضريبة والجمارك.",
  "A Saudi VAT number is 15 digits, starting and ending with 3. Check the ZATCA certificate.": "الرقم الضريبي السعودي 15 رقماً يبدأ وينتهي بالرقم 3. راجع شهادة هيئة الزكاة والضريبة والجمارك.",
  "As printed on the tax registration certificate.": "كما هو مطبوع في شهادة التسجيل الضريبي.",
  // Step 2: email and finance
  "{domain} is a public email service. Use the domain of your company email, such as acme.sa.": "{domain} خدمة بريد عامة. استخدم نطاق بريد شركتك، مثل acme.sa.",
  "The code your ERP uses for this company on journal exports. Leave blank to use the CR number. Only a finance user can set it.": "الرمز الذي يستخدمه نظام تخطيط الموارد لهذه الشركة في تصدير القيود. اتركه فارغاً لاستخدام رقم السجل التجاري. يحدده مستخدم المالية فقط.",
  // Step 3: branches and locations
  "Every company needs one head office. Add the other branches people are assigned to. A code is short and permanent, for example HQ or JED; employee numbers can use it.": "تحتاج كل شركة إلى مقر رئيسي واحد. أضف الفروع الأخرى التي يُعيَّن فيها الموظفون. الرمز قصير ودائم، مثل HQ أو JED، ويمكن أن تستخدمه أرقام الموظفين.",
  "Riyadh": "الرياض",
  "Jeddah": "جدة",
  "Makkah": "مكة المكرمة",
  "Taif": "الطائف",
  "Madinah": "المدينة المنورة",
  "Dammam": "الدمام",
  "Al Khobar": "الخبر",
  "Jubail": "الجبيل",
  "Abha": "أبها",
  "Khamis Mushait": "خميس مشيط",
  "Dubai": "دبي",
  "Doha": "الدوحة",
  "Kuwait City": "مدينة الكويت",
  "Manama": "المنامة",
  "Muscat": "مسقط",
  "Cairo": "القاهرة",
  "India (Kolkata)": "الهند (كولكاتا)",
  "Karachi": "كراتشي",
  "London": "لندن",
  "New York": "نيويورك",
  "UTC": "التوقيت العالمي (UTC)",
  // Step 6: company super admin
  "Optional. Shown when KynexOne is used in Arabic.": "اختياري. يظهر عند استخدام KynexOne بالعربية.",
  "Must be an address on {domain}. Type just the name and @{domain} is added for you.": "يجب أن يكون العنوان على {domain}. اكتب الاسم فقط وسنضيف @{domain} تلقائياً.",
  "{domain} is a public email service. Use the person’s company email; KynexOne never creates mailboxes.": "{domain} خدمة بريد عامة. استخدم بريد الشخص في الشركة؛ لا ينشئ KynexOne صناديق بريد.",
};

export const setupDataEntry = {
  en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) as Record<string, string>,
  ar,
};
