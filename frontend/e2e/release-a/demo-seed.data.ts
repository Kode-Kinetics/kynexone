/**
 * Release A demo company — Masar Holding (مجموعة مسار). The DATA half of the seed; the HTTP half is
 * demo-seed.ts. Source of truth for every figure: the plan of record, §5 "10-minute demo storyline".
 *
 * Everything here is fictional. Identity numbers are generated to be FORMAT-valid only (10 digits,
 * 1 = Saudi national ID, 2 = Iqama, Luhn check digit) and IBANs are ISO 13616 mod-97 valid with real
 * Saudi bank codes, so the product's own validators accept them. None belongs to a real person.
 *
 * Salaries are derived from the grade, not typed per person, using the storyline matrix (§5):
 *   Housing   G1 in kind (accommodation, so no cash allowance); G2–G4 25% of basic; G5 30%.
 *   Transport G1 in kind (bus, no cash allowance); G2–G3 10% of basic; G4–G5 SAR 1,000.
 * so the payroll figures already agree with what the R1 matrix will publish.
 *
 * Load-bearing figures (do not change without changing the storyline):
 *   Mohammed Abdelrahman — basic 8,000, so housing 25% = SAR 2,000 and the housing advance
 *     (3× monthly housing) = SAR 6,000; raising housing to 30% costs +SAR 400/month.
 *   Asif Mahmood — wage 4,800, so a SAR 400 instalment is 8.3% of wage, one absence day is
 *     4,800 / 30 = SAR 160, and the 50% debt cap is SAR 2,400.
 */

export type CompanyKey = 'MFS' | 'MLG';
export type GradeCode = 'G1' | 'G2' | 'G3' | 'G4' | 'G5';
export type Nationality = 'Saudi' | 'Egyptian' | 'Pakistani' | 'Filipino' | 'Indian';

export const TENANT = {
  slug: 'masar',
  name: 'Masar Holding',
  nameAr: 'مجموعة مسار',
  /** Every login and work email lives here. `.test` is reserved (RFC 2606): it can never deliver. */
  emailDomain: 'masar-holding.test',
} as const;

export interface DemoCompany {
  key: CompanyKey;
  legalNameEn: string;
  legalNameAr: string;
  tradeName: string;
  city: string;
  cityAr: string;
  /** Saudi CR numbers are 10 digits; 1010… is Riyadh, 2050… is Dammam. */
  registrationNumber: string;
  /** Saudi VAT numbers are 15 digits, starting and ending with 3. */
  taxNumber: string;
  gosiEmployerId: string;
  qiwaEstablishmentId: string;
  /** MOL establishment number, which WPS files carry as the employer ID. */
  wpsEmployerId: string;
  departments: Array<{ code: string; en: string; ar: string }>;
}

export const COMPANIES: DemoCompany[] = [
  {
    key: 'MFS',
    legalNameEn: 'Masar Facility Services Co.',
    legalNameAr: 'شركة مسار لخدمات المرافق',
    tradeName: 'Masar Facility Services',
    city: 'Riyadh',
    cityAr: 'الرياض',
    registrationNumber: '1010784512',
    taxNumber: '310478451200003',
    gosiEmployerId: '601234570',
    qiwaEstablishmentId: '1-2384571',
    wpsEmployerId: '1-2384571',
    departments: [
      { code: 'EXEC', en: 'Executive Office', ar: 'المكتب التنفيذي' },
      { code: 'HR', en: 'Human Resources', ar: 'الموارد البشرية' },
      { code: 'FIN', en: 'Finance & Accounts', ar: 'المالية والحسابات' },
      { code: 'OPS', en: 'Facility Operations', ar: 'تشغيل المرافق' },
      { code: 'MNT', en: 'Technical Maintenance', ar: 'الصيانة الفنية' },
      { code: 'SOFT', en: 'Soft Services', ar: 'الخدمات المساندة' },
    ],
  },
  {
    key: 'MLG',
    legalNameEn: 'Masar Logistics Co.',
    legalNameAr: 'شركة مسار للخدمات اللوجستية',
    tradeName: 'Masar Logistics',
    city: 'Dammam',
    cityAr: 'الدمام',
    registrationNumber: '2050163348',
    taxNumber: '310163334800003',
    gosiEmployerId: '601234588',
    qiwaEstablishmentId: '7-1163348',
    wpsEmployerId: '7-1163348',
    departments: [
      { code: 'ADM', en: 'Logistics Administration', ar: 'الإدارة اللوجستية' },
      { code: 'FLT', en: 'Fleet Operations', ar: 'عمليات الأسطول' },
      { code: 'WH', en: 'Warehouse', ar: 'المستودعات' },
    ],
  },
];

/** G1–G5 as the storyline names them, with Arabic names. Bands are on GROSS (basic + allowances). */
export const GRADES: Array<{
  code: GradeCode; name: string; nameAr: string; level: number; min: number; mid: number; max: number;
}> = [
  { code: 'G1', name: 'Worker', nameAr: 'عامل', level: 10, min: 1500, mid: 3500, max: 6000 },
  { code: 'G2', name: 'Technician', nameAr: 'فني', level: 20, min: 4000, mid: 6500, max: 9000 },
  { code: 'G3', name: 'Supervisor', nameAr: 'مشرف', level: 30, min: 7000, mid: 10500, max: 14000 },
  { code: 'G4', name: 'Engineer', nameAr: 'مهندس', level: 40, min: 11000, mid: 16500, max: 22000 },
  { code: 'G5', name: 'Manager', nameAr: 'مدير', level: 50, min: 18000, mid: 29000, max: 40000 },
];

/** Monthly cash allowances implied by the storyline matrix for a grade and basic salary. */
export function allowancesFor(grade: GradeCode, basic: number): { housing: number; transport: number } {
  const housingRate = grade === 'G1' ? 0 : grade === 'G5' ? 0.30 : 0.25;
  const transport = grade === 'G1' ? 0 : grade === 'G2' || grade === 'G3' ? Math.round(basic * 0.10) : 1000;
  return { housing: Math.round(basic * housingRate), transport };
}

export interface Term { start: string; end: string | null }

export interface DemoEmployee {
  code: string;
  company: CompanyKey;
  en: string;
  ar: string;
  gender: 'Male' | 'Female';
  nationality: Nationality;
  grade: GradeCode;
  dept: string;
  title: string;
  titleAr: string;
  basic: number;
  /** Employee code of the line manager; omitted for the top of each company. */
  manager?: string;
  maritalStatus: 'Single' | 'Married';
  /** Contract terms, oldest first. The LAST one is the term in force. `end: null` = indefinite. */
  terms: Term[];
  /** Storyline role, for the report and the TODO hooks. */
  cast?: 'faisal' | 'mohammed' | 'ramon-group' | 'asif' | 'line-manager';
}

const fixed = (...pairs: Array<[string, string]>): Term[] => pairs.map(([start, end]) => ({ start, end }));
const indefinite = (start: string): Term[] => [{ start, end: null }];

/** The Ramon Dela Cruz group: twelve people renewing as-is, every current term ending 30 Nov 2026. */
const RAMON_TERM: [string, string] = ['2025-12-01', '2026-11-30'];
const RAMON_PRIOR: [string, string] = ['2024-12-01', '2025-11-30'];

export const EMPLOYEES: DemoEmployee[] = [
  // ── Masar Facility Services Co. (Riyadh) ────────────────────────────────────────────────────
  { code: 'MFS-0001', company: 'MFS', en: 'Turki Al-Subaie', ar: 'تركي السبيعي', gender: 'Male', nationality: 'Saudi', grade: 'G5', dept: 'EXEC', title: 'General Manager', titleAr: 'المدير العام', basic: 26000, maritalStatus: 'Married', terms: indefinite('2018-09-01') },
  { code: 'MFS-0002', company: 'MFS', en: 'Majed Al-Ghamdi', ar: 'ماجد الغامدي', gender: 'Male', nationality: 'Saudi', grade: 'G5', dept: 'FIN', title: 'Finance Manager', titleAr: 'المدير المالي', basic: 19000, manager: 'MFS-0001', maritalStatus: 'Married', terms: indefinite('2020-01-01'), cast: 'line-manager' },
  { code: 'MFS-0003', company: 'MFS', en: 'Mohammed Abdelrahman', ar: 'محمد عبدالرحمن', gender: 'Male', nationality: 'Egyptian', grade: 'G3', dept: 'FIN', title: 'Accountant', titleAr: 'محاسب', basic: 8000, manager: 'MFS-0002', maritalStatus: 'Married', terms: fixed(['2025-02-01', '2026-01-31'], ['2026-02-01', '2027-01-31']), cast: 'mohammed' },
  { code: 'MFS-0005', company: 'MFS', en: 'Abdulaziz Al-Shehri', ar: 'عبدالعزيز الشهري', gender: 'Male', nationality: 'Saudi', grade: 'G5', dept: 'OPS', title: 'Operations Manager', titleAr: 'مدير العمليات', basic: 21000, manager: 'MFS-0001', maritalStatus: 'Married', terms: indefinite('2019-06-01') },
  // Third term ends 31 Dec 2026 after two renewals; the chain began 1 Feb 2023, so it is 3.9 of 4 years old.
  { code: 'MFS-0004', company: 'MFS', en: 'Faisal Al-Qahtani', ar: 'فيصل القحطاني', gender: 'Male', nationality: 'Saudi', grade: 'G4', dept: 'MNT', title: 'Facilities Engineer', titleAr: 'مهندس مرافق', basic: 12000, manager: 'MFS-0005', maritalStatus: 'Married', terms: fixed(['2023-02-01', '2024-01-31'], ['2024-02-01', '2025-01-31'], ['2025-02-01', '2026-12-31']), cast: 'faisal' },
  { code: 'MFS-0006', company: 'MFS', en: 'Nouf Al-Harbi', ar: 'نوف الحربي', gender: 'Female', nationality: 'Saudi', grade: 'G4', dept: 'HR', title: 'HR Business Partner', titleAr: 'شريك أعمال الموارد البشرية', basic: 11000, manager: 'MFS-0001', maritalStatus: 'Single', terms: fixed(['2022-04-01', '2024-03-31'], ['2024-04-01', '2027-03-31']) },
  { code: 'MFS-0007', company: 'MFS', en: 'Rania Saleh', ar: 'رانيا صالح', gender: 'Female', nationality: 'Egyptian', grade: 'G3', dept: 'FIN', title: 'Payroll Accountant', titleAr: 'محاسبة رواتب', basic: 7500, manager: 'MFS-0002', maritalStatus: 'Married', terms: fixed(['2024-05-01', '2025-04-30'], ['2025-05-01', '2027-04-30']) },
  { code: 'MFS-0008', company: 'MFS', en: 'Sanjay Kumar', ar: 'سانجاي كومار', gender: 'Male', nationality: 'Indian', grade: 'G4', dept: 'MNT', title: 'MEP Engineer', titleAr: 'مهندس كهروميكانيك', basic: 11500, manager: 'MFS-0005', maritalStatus: 'Married', terms: fixed(['2022-10-01', '2025-09-30'], ['2025-10-01', '2027-09-30']) },
  { code: 'MFS-0009', company: 'MFS', en: 'Ahmed Fathy', ar: 'أحمد فتحي', gender: 'Male', nationality: 'Egyptian', grade: 'G3', dept: 'MNT', title: 'Maintenance Supervisor', titleAr: 'مشرف صيانة', basic: 7000, manager: 'MFS-0004', maritalStatus: 'Married', terms: fixed(['2021-07-01', '2026-06-30'], ['2026-07-01', '2027-06-30']) },
  { code: 'MFS-0010', company: 'MFS', en: 'Bilal Hussain', ar: 'بلال حسين', gender: 'Male', nationality: 'Pakistani', grade: 'G2', dept: 'MNT', title: 'HVAC Technician', titleAr: 'فني تكييف', basic: 4800, manager: 'MFS-0009', maritalStatus: 'Married', terms: fixed(['2023-03-01', '2026-02-28'], ['2026-03-01', '2027-02-28']) },
  { code: 'MFS-0011', company: 'MFS', en: 'Rajesh Nair', ar: 'راجيش ناير', gender: 'Male', nationality: 'Indian', grade: 'G2', dept: 'MNT', title: 'Electrician', titleAr: 'كهربائي', basic: 4500, manager: 'MFS-0009', maritalStatus: 'Married', terms: fixed(['2022-01-01', '2025-12-31'], ['2026-01-01', '2026-12-31']) },
  { code: 'MFS-0012', company: 'MFS', en: 'Imran Qureshi', ar: 'عمران قريشي', gender: 'Male', nationality: 'Pakistani', grade: 'G2', dept: 'MNT', title: 'Plumber', titleAr: 'سباك', basic: 4200, manager: 'MFS-0009', maritalStatus: 'Single', terms: fixed(['2024-06-01', '2026-05-31'], ['2026-06-01', '2027-05-31']) },
  { code: 'MFS-0013', company: 'MFS', en: 'Khalid Al-Dossary', ar: 'خالد الدوسري', gender: 'Male', nationality: 'Saudi', grade: 'G3', dept: 'SOFT', title: 'Soft Services Supervisor', titleAr: 'مشرف الخدمات المساندة', basic: 8500, manager: 'MFS-0005', maritalStatus: 'Married', terms: indefinite('2021-01-01') },
  { code: 'MFS-0014', company: 'MFS', en: 'Lama Al-Zahrani', ar: 'لمى الزهراني', gender: 'Female', nationality: 'Saudi', grade: 'G2', dept: 'HR', title: 'HR Officer', titleAr: 'أخصائية موارد بشرية', basic: 5500, manager: 'MFS-0006', maritalStatus: 'Single', terms: fixed(['2024-09-01', '2026-08-31'], ['2026-09-01', '2027-08-31']) },
  { code: 'MFS-0015', company: 'MFS', en: 'Yasser Al-Anazi', ar: 'ياسر العنزي', gender: 'Male', nationality: 'Saudi', grade: 'G3', dept: 'OPS', title: 'Facility Coordinator', titleAr: 'منسق مرافق', basic: 7800, manager: 'MFS-0005', maritalStatus: 'Married', terms: fixed(['2023-08-01', '2025-07-31'], ['2025-08-01', '2027-07-31']) },
  // The Ramon Dela Cruz group — renew as-is, ending 30 Nov 2026.
  { code: 'MFS-0016', company: 'MFS', en: 'Ramon Dela Cruz', ar: 'رامون ديلا كروز', gender: 'Male', nationality: 'Filipino', grade: 'G2', dept: 'SOFT', title: 'Housekeeping Team Leader', titleAr: 'قائد فريق النظافة', basic: 4500, manager: 'MFS-0013', maritalStatus: 'Married', terms: fixed(RAMON_PRIOR, RAMON_TERM), cast: 'ramon-group' },
  { code: 'MFS-0017', company: 'MFS', en: 'Jose Santos', ar: 'خوسيه سانتوس', gender: 'Male', nationality: 'Filipino', grade: 'G1', dept: 'SOFT', title: 'Cleaner', titleAr: 'عامل نظافة', basic: 2400, manager: 'MFS-0016', maritalStatus: 'Married', terms: fixed(RAMON_PRIOR, RAMON_TERM), cast: 'ramon-group' },
  { code: 'MFS-0018', company: 'MFS', en: 'Maria Reyes', ar: 'ماريا رييس', gender: 'Female', nationality: 'Filipino', grade: 'G1', dept: 'SOFT', title: 'Housekeeper', titleAr: 'عاملة تدبير منزلي', basic: 2400, manager: 'MFS-0016', maritalStatus: 'Single', terms: fixed(RAMON_PRIOR, RAMON_TERM), cast: 'ramon-group' },
  { code: 'MFS-0019', company: 'MFS', en: 'Mark Villanueva', ar: 'مارك فيلانويفا', gender: 'Male', nationality: 'Filipino', grade: 'G1', dept: 'SOFT', title: 'Cleaner', titleAr: 'عامل نظافة', basic: 2400, manager: 'MFS-0016', maritalStatus: 'Single', terms: fixed(RAMON_TERM), cast: 'ramon-group' },
  { code: 'MFS-0020', company: 'MFS', en: 'Arnel Bautista', ar: 'أرنيل باوتيستا', gender: 'Male', nationality: 'Filipino', grade: 'G2', dept: 'SOFT', title: 'Pest Control Technician', titleAr: 'فني مكافحة حشرات', basic: 4100, manager: 'MFS-0013', maritalStatus: 'Married', terms: fixed(RAMON_PRIOR, RAMON_TERM), cast: 'ramon-group' },
  { code: 'MFS-0021', company: 'MFS', en: 'Jennifer Garcia', ar: 'جينيفر غارسيا', gender: 'Female', nationality: 'Filipino', grade: 'G1', dept: 'SOFT', title: 'Housekeeper', titleAr: 'عاملة تدبير منزلي', basic: 2400, manager: 'MFS-0016', maritalStatus: 'Married', terms: fixed(RAMON_TERM), cast: 'ramon-group' },
  { code: 'MFS-0022', company: 'MFS', en: 'Mohammad Rafiq', ar: 'محمد رفيق', gender: 'Male', nationality: 'Pakistani', grade: 'G1', dept: 'SOFT', title: 'Cleaner', titleAr: 'عامل نظافة', basic: 2300, manager: 'MFS-0016', maritalStatus: 'Married', terms: fixed(RAMON_PRIOR, RAMON_TERM), cast: 'ramon-group' },
  { code: 'MFS-0023', company: 'MFS', en: 'Suresh Pillai', ar: 'سوريش بيلاي', gender: 'Male', nationality: 'Indian', grade: 'G1', dept: 'SOFT', title: 'Gardener', titleAr: 'عامل حدائق', basic: 2300, manager: 'MFS-0016', maritalStatus: 'Married', terms: fixed(RAMON_PRIOR, RAMON_TERM), cast: 'ramon-group' },
  { code: 'MFS-0024', company: 'MFS', en: 'Anil Thomas', ar: 'أنيل توماس', gender: 'Male', nationality: 'Indian', grade: 'G1', dept: 'SOFT', title: 'Laundry Attendant', titleAr: 'عامل مغسلة', basic: 2300, manager: 'MFS-0016', maritalStatus: 'Single', terms: fixed(RAMON_TERM), cast: 'ramon-group' },
  { code: 'MFS-0025', company: 'MFS', en: 'Waqas Ahmed', ar: 'وقاص أحمد', gender: 'Male', nationality: 'Pakistani', grade: 'G1', dept: 'SOFT', title: 'Cleaner', titleAr: 'عامل نظافة', basic: 2300, manager: 'MFS-0016', maritalStatus: 'Single', terms: fixed(RAMON_PRIOR, RAMON_TERM), cast: 'ramon-group' },
  { code: 'MFS-0026', company: 'MFS', en: 'Mahmoud Saeed', ar: 'محمود سعيد', gender: 'Male', nationality: 'Egyptian', grade: 'G2', dept: 'SOFT', title: 'Housekeeping Technician', titleAr: 'فني نظافة', basic: 4000, manager: 'MFS-0013', maritalStatus: 'Married', terms: fixed(RAMON_PRIOR, RAMON_TERM), cast: 'ramon-group' },
  { code: 'MFS-0027', company: 'MFS', en: 'Rodel Mendoza', ar: 'رودل مندوزا', gender: 'Male', nationality: 'Filipino', grade: 'G1', dept: 'SOFT', title: 'Cleaner', titleAr: 'عامل نظافة', basic: 2400, manager: 'MFS-0016', maritalStatus: 'Single', terms: fixed(RAMON_TERM), cast: 'ramon-group' },
  // Rest of Facility Services.
  { code: 'MFS-0028', company: 'MFS', en: 'Hamad Al-Rashidi', ar: 'حمد الرشيدي', gender: 'Male', nationality: 'Saudi', grade: 'G2', dept: 'OPS', title: 'Facility Technician', titleAr: 'فني مرافق', basic: 5200, manager: 'MFS-0015', maritalStatus: 'Single', terms: fixed(['2025-04-01', '2027-03-31']) },
  { code: 'MFS-0029', company: 'MFS', en: 'Shaimaa Mostafa', ar: 'شيماء مصطفى', gender: 'Female', nationality: 'Egyptian', grade: 'G3', dept: 'FIN', title: 'Procurement Officer', titleAr: 'أخصائية مشتريات', basic: 7200, manager: 'MFS-0002', maritalStatus: 'Married', terms: fixed(['2023-11-01', '2025-10-31'], ['2025-11-01', '2027-10-31']) },
  { code: 'MFS-0030', company: 'MFS', en: 'Arvind Menon', ar: 'أرفيند مينون', gender: 'Male', nationality: 'Indian', grade: 'G3', dept: 'OPS', title: 'HSE Officer', titleAr: 'أخصائي صحة وسلامة', basic: 7600, manager: 'MFS-0005', maritalStatus: 'Married', terms: fixed(['2024-02-01', '2026-01-31'], ['2026-02-01', '2027-01-31']) },
  { code: 'MFS-0031', company: 'MFS', en: 'Junaid Akhtar', ar: 'جنيد أختر', gender: 'Male', nationality: 'Pakistani', grade: 'G2', dept: 'MNT', title: 'Carpenter', titleAr: 'نجار', basic: 4300, manager: 'MFS-0009', maritalStatus: 'Married', terms: fixed(['2023-05-01', '2026-04-30'], ['2026-05-01', '2027-04-30']) },
  { code: 'MFS-0032', company: 'MFS', en: 'Abeer Al-Qahtani', ar: 'عبير القحطاني', gender: 'Female', nationality: 'Saudi', grade: 'G3', dept: 'OPS', title: 'Customer Service Lead', titleAr: 'قائدة خدمة العملاء', basic: 7000, manager: 'MFS-0015', maritalStatus: 'Married', terms: fixed(['2023-01-01', '2025-12-31'], ['2026-01-01', '2026-12-31']) },

  // ── Masar Logistics Co. (Dammam) ────────────────────────────────────────────────────────────
  { code: 'MLG-0001', company: 'MLG', en: 'Saud Al-Dosari', ar: 'سعود الدوسري', gender: 'Male', nationality: 'Saudi', grade: 'G5', dept: 'ADM', title: 'General Manager', titleAr: 'المدير العام', basic: 24000, maritalStatus: 'Married', terms: indefinite('2019-11-01') },
  { code: 'MLG-0002', company: 'MLG', en: 'Nasser Al-Mutairi', ar: 'ناصر المطيري', gender: 'Male', nationality: 'Saudi', grade: 'G4', dept: 'FLT', title: 'Fleet Manager', titleAr: 'مدير الأسطول', basic: 13000, manager: 'MLG-0001', maritalStatus: 'Married', terms: fixed(['2021-03-01', '2026-02-28'], ['2026-03-01', '2027-02-28']) },
  { code: 'MLG-0004', company: 'MLG', en: 'Tariq Mehmood', ar: 'طارق محمود', gender: 'Male', nationality: 'Pakistani', grade: 'G3', dept: 'FLT', title: 'Transport Supervisor', titleAr: 'مشرف نقل', basic: 7400, manager: 'MLG-0002', maritalStatus: 'Married', terms: fixed(['2020-08-01', '2026-07-31'], ['2026-08-01', '2027-07-31']) },
  // Wage SAR 4,800 — see the header. G1, so housing and transport are in kind.
  { code: 'MLG-0003', company: 'MLG', en: 'Asif Mahmood', ar: 'آصف محمود', gender: 'Male', nationality: 'Pakistani', grade: 'G1', dept: 'FLT', title: 'Driver', titleAr: 'سائق', basic: 4800, manager: 'MLG-0004', maritalStatus: 'Married', terms: fixed(['2024-03-01', '2026-02-28'], ['2026-03-01', '2027-02-28']), cast: 'asif' },
  { code: 'MLG-0005', company: 'MLG', en: 'Zubair Khan', ar: 'زبير خان', gender: 'Male', nationality: 'Pakistani', grade: 'G1', dept: 'FLT', title: 'Heavy Vehicle Driver', titleAr: 'سائق معدات ثقيلة', basic: 4200, manager: 'MLG-0004', maritalStatus: 'Married', terms: fixed(['2023-10-01', '2025-09-30'], ['2025-10-01', '2027-09-30']) },
  { code: 'MLG-0006', company: 'MLG', en: 'Shahid Iqbal', ar: 'شاهد إقبال', gender: 'Male', nationality: 'Pakistani', grade: 'G1', dept: 'FLT', title: 'Driver', titleAr: 'سائق', basic: 3800, manager: 'MLG-0004', maritalStatus: 'Single', terms: fixed(['2025-01-01', '2025-12-31'], ['2026-01-01', '2026-12-31']) },
  { code: 'MLG-0007', company: 'MLG', en: 'Ravi Shankar', ar: 'رافي شانكار', gender: 'Male', nationality: 'Indian', grade: 'G1', dept: 'FLT', title: 'Driver', titleAr: 'سائق', basic: 3800, manager: 'MLG-0004', maritalStatus: 'Married', terms: fixed(['2024-07-01', '2026-06-30'], ['2026-07-01', '2027-06-30']) },
  { code: 'MLG-0008', company: 'MLG', en: 'Manoj Varghese', ar: 'مانوج فرغيز', gender: 'Male', nationality: 'Indian', grade: 'G2', dept: 'FLT', title: 'Fleet Mechanic', titleAr: 'ميكانيكي أسطول', basic: 5000, manager: 'MLG-0002', maritalStatus: 'Married', terms: fixed(['2022-05-01', '2026-04-30'], ['2026-05-01', '2027-04-30']) },
  { code: 'MLG-0009', company: 'MLG', en: 'Eduardo Ramos', ar: 'إدواردو راموس', gender: 'Male', nationality: 'Filipino', grade: 'G2', dept: 'FLT', title: 'Auto Electrician', titleAr: 'كهربائي سيارات', basic: 4600, manager: 'MLG-0002', maritalStatus: 'Married', terms: fixed(['2023-09-01', '2025-08-31'], ['2025-09-01', '2027-08-31']) },
  { code: 'MLG-0010', company: 'MLG', en: 'Hussein Abdelaziz', ar: 'حسين عبدالعزيز', gender: 'Male', nationality: 'Egyptian', grade: 'G3', dept: 'WH', title: 'Warehouse Supervisor', titleAr: 'مشرف مستودع', basic: 7200, manager: 'MLG-0001', maritalStatus: 'Married', terms: fixed(['2021-10-01', '2025-09-30'], ['2025-10-01', '2027-09-30']) },
  // Ends 31 Oct 2026: the near-term exception on the renewal dashboard.
  { code: 'MLG-0011', company: 'MLG', en: 'Gaurav Sharma', ar: 'غوراف شارما', gender: 'Male', nationality: 'Indian', grade: 'G2', dept: 'WH', title: 'Inventory Controller', titleAr: 'مراقب مخزون', basic: 4800, manager: 'MLG-0010', maritalStatus: 'Single', terms: fixed(['2024-11-01', '2025-10-31'], ['2025-11-01', '2026-10-31']) },
  { code: 'MLG-0012', company: 'MLG', en: 'Noel Aquino', ar: 'نويل أكينو', gender: 'Male', nationality: 'Filipino', grade: 'G1', dept: 'WH', title: 'Forklift Operator', titleAr: 'مشغل رافعة شوكية', basic: 3200, manager: 'MLG-0010', maritalStatus: 'Single', terms: fixed(['2025-02-01', '2026-01-31'], ['2026-02-01', '2027-01-31']) },
  { code: 'MLG-0013', company: 'MLG', en: 'Kamran Ali', ar: 'كامران علي', gender: 'Male', nationality: 'Pakistani', grade: 'G1', dept: 'WH', title: 'Storekeeper', titleAr: 'أمين مستودع', basic: 3000, manager: 'MLG-0010', maritalStatus: 'Married', terms: fixed(['2023-06-01', '2026-05-31'], ['2026-06-01', '2027-05-31']) },
  { code: 'MLG-0014', company: 'MLG', en: 'Abdullah Al-Harthi', ar: 'عبدالله الحارثي', gender: 'Male', nationality: 'Saudi', grade: 'G2', dept: 'WH', title: 'Warehouse Coordinator', titleAr: 'منسق مستودع', basic: 5600, manager: 'MLG-0010', maritalStatus: 'Single', terms: fixed(['2025-06-01', '2027-05-31']) },
  { code: 'MLG-0015', company: 'MLG', en: 'Reem Al-Shammari', ar: 'ريم الشمري', gender: 'Female', nationality: 'Saudi', grade: 'G3', dept: 'ADM', title: 'Logistics Coordinator', titleAr: 'منسقة لوجستية', basic: 8000, manager: 'MLG-0001', maritalStatus: 'Single', terms: indefinite('2023-01-01') },
  { code: 'MLG-0016', company: 'MLG', en: 'Ahmed Hamdy', ar: 'أحمد حمدي', gender: 'Male', nationality: 'Egyptian', grade: 'G3', dept: 'ADM', title: 'Accountant', titleAr: 'محاسب', basic: 7800, manager: 'MLG-0001', maritalStatus: 'Married', terms: fixed(['2022-09-01', '2026-08-31'], ['2026-09-01', '2027-08-31']) },
  { code: 'MLG-0017', company: 'MLG', en: 'Sultan Al-Otaibi', ar: 'سلطان العتيبي', gender: 'Male', nationality: 'Saudi', grade: 'G4', dept: 'FLT', title: 'Operations Engineer', titleAr: 'مهندس عمليات', basic: 11000, manager: 'MLG-0002', maritalStatus: 'Married', terms: fixed(['2024-01-01', '2025-12-31'], ['2026-01-01', '2027-12-31']) },
  { code: 'MLG-0018', company: 'MLG', en: 'Fahad Al-Yami', ar: 'فهد اليامي', gender: 'Male', nationality: 'Saudi', grade: 'G2', dept: 'FLT', title: 'Dispatcher', titleAr: 'منسق حركة', basic: 5000, manager: 'MLG-0002', maritalStatus: 'Single', terms: fixed(['2025-03-01', '2027-02-28']) },
];

/**
 * Mohammed's dependants (storyline: "wife and 2 children"). NOT SEEDED YET — `employee_dependents`
 * exists in the schema but has no API, controller or importer on main. Kept here so the R0/R2 slice
 * that adds the endpoint only has to post these rows (see TODO(R2) in demo-seed.ts).
 */
export const MOHAMMED_DEPENDANTS = [
  { fullName: 'Hoda Ibrahim', fullNameAr: 'هدى إبراهيم', relationship: 'Spouse', dateOfBirth: '1991-04-12' },
  { fullName: 'Youssef Mohammed Abdelrahman', fullNameAr: 'يوسف محمد عبدالرحمن', relationship: 'Child', dateOfBirth: '2017-09-03' },
  { fullName: 'Salma Mohammed Abdelrahman', fullNameAr: 'سلمى محمد عبدالرحمن', relationship: 'Child', dateOfBirth: '2020-02-21' },
] as const;

// ── Personas ─────────────────────────────────────────────────────────────────────────────────

export interface DemoPersona {
  key: string;
  email: string;
  fullName: string;
  role: string;
  storylineRole: string;
  /** Employee code for logins that ARE an employee (invitation flow, linked by Employee.UserAccountId). */
  employeeCode?: string;
}

const at = (local: string) => `${local}@${TENANT.emailDomain}`;

/**
 * Every persona a scene logs in as. Passwords are NEVER here: demo-seed.ts generates them (or takes
 * DEMO_MASAR_PASSWORD) and writes them to the git-ignored e2e/.auth/demo-masar.credentials.json.
 *
 * The Group HR Director is the tenant's own administrator (role Admin, the tenant super-admin), which
 * the storyline needs: she publishes the group matrix and decides in the Approval Center.
 * The two HR Managers are deliberately peers: scene 6:30 needs HR user 1 unable to verify their own
 * upload and HR user 2 able to.
 */
export const PERSONAS: DemoPersona[] = [
  { key: 'hrDirector', email: at('nora.aldosari'), fullName: 'Nora Al-Dosari', role: 'Admin', storylineRole: 'Group HR Director (super-admin)' },
  { key: 'hrManager', email: at('khalid.alfaraj'), fullName: 'Khalid Al-Faraj', role: 'HR Manager', storylineRole: 'HR Manager (HR user 1)' },
  { key: 'hrUser2', email: at('sara.alzahrani'), fullName: 'Sara Al-Zahrani', role: 'HR Manager', storylineRole: 'Second HR user (HR user 2)' },
  { key: 'payroll', email: at('hani.aljuhani'), fullName: 'Hani Al-Juhani', role: 'Payroll Manager', storylineRole: 'Payroll' },
  { key: 'finance', email: at('abdullah.almalki'), fullName: 'Abdullah Al-Malki', role: 'Finance Approver', storylineRole: 'Finance' },
  { key: 'lineManager', email: at('majed.alghamdi'), fullName: 'Majed Al-Ghamdi', role: 'Manager', storylineRole: "Line manager (Mohammed's manager)", employeeCode: 'MFS-0002' },
  { key: 'mohammed', email: at('mohammed.abdelrahman'), fullName: 'Mohammed Abdelrahman', role: 'Employee', storylineRole: 'Employee (Mohammed)', employeeCode: 'MFS-0003' },
  { key: 'asif', email: at('asif.mahmood'), fullName: 'Asif Mahmood', role: 'Employee', storylineRole: 'Employee (Asif)', employeeCode: 'MLG-0003' },
];

// ── Loans (live feature: loan types, policies, grade loan limits) ───────────────────────────────

/**
 * `facility` is the grade-limit code the API derives from `code` (GradeLoanLimitResolver.FacilityCodeFor:
 * 'LOAN_' + the code upper-cased, anything not A–Z/0–9 turned into '_'). `catalogued` says the facility must be
 * one of the Release A catalogue's own components (EntitlementComponentRules.Catalogue), not the generic
 * LOAN_<type> fallback every other loan type gets.
 *
 * The housing advance MUST be code HOUSING_ADVANCE: code HOUSING would give LOAN_HOUSING, a generic loan with no
 * × housing option, and the matrix, package and renewal screens would never recognise it as the housing advance.
 * FacilityCodeFor deliberately does not alias HOUSING, because live tenants already hold LOAN_HOUSING grids.
 */
export const LOAN_TYPES = [
  { code: 'PERSONAL', nameEn: 'Personal Loan', nameAr: 'قرض شخصي', maxAmount: 100000, maxInstallments: 24, facility: 'LOAN_PERSONAL', catalogued: false },
  // The storyline's "housing advance": G2+ up to 3x monthly housing, one outstanding at a time.
  { code: 'HOUSING_ADVANCE', nameEn: 'Housing Advance', nameAr: 'سلفة سكن', maxAmount: 30000, maxInstallments: 12, facility: 'LOAN_HOUSING_ADVANCE', catalogued: true },
] as const;

/**
 * The loan facilities the Release A catalogue declares itself (EntitlementComponentRules.Catalogue, the rules
 * with IsLoanFacility), with the value types each allows. unit/demoSeedLoanCatalogue.spec.ts pins this list to
 * the C# source, so the two cannot drift. Any other LOAN_* code is the generic fallback (For()), which allows
 * GENERIC_LOAN_VALUE_TYPES.
 */
export const CATALOGUE_LOAN_FACILITIES: Record<string, readonly string[]> = {
  LOAN_HOUSING_ADVANCE: ['Amount', 'MultipleOfBasic', 'MultipleOfGross', 'MultipleOfHousing', 'EligibilityOnly'],
};
export const GENERIC_LOAN_VALUE_TYPES: readonly string[] = ['Amount', 'MultipleOfBasic', 'MultipleOfGross', 'EligibilityOnly'];

/** Port of GradeLoanLimitResolver.FacilityCodeFor (C#). Kept byte-for-byte equivalent; see the unit spec. */
export function facilityCodeFor(loanTypeCode: string): string {
  const cleaned = (loanTypeCode ?? '').trim().toUpperCase().replace(/[^A-Z0-9]/g, '_').replace(/^_+|_+$/g, '');
  const code = `LOAN_${cleaned.length === 0 ? 'TYPE' : cleaned}`;
  return code.length > 64 ? code.slice(0, 64) : code;
}

/**
 * Every problem with the declared grade-limited loan types, before the seed writes anything. Empty = fine.
 * Each grade-limited type must: exist in LOAN_TYPES; derive the facility it declares; when catalogued, be in the
 * catalogue; and use only value types its facility allows.
 */
export function loanCatalogueProblems(
  types: ReadonlyArray<{ code: string; facility: string; catalogued: boolean }> = LOAN_TYPES,
  limits: Record<string, Record<string, { valueType: string }>> = GRADE_LOAN_LIMITS,
  catalogue: Record<string, readonly string[]> = CATALOGUE_LOAN_FACILITIES,
): string[] {
  const problems: string[] = [];
  for (const [code, grid] of Object.entries(limits)) {
    const type = types.find((t) => t.code === code);
    if (!type) { problems.push(`grade limits are declared for loan type ${code}, which LOAN_TYPES does not create`); continue; }
    const derived = facilityCodeFor(type.code);
    if (derived !== type.facility) {
      problems.push(`loan type ${code} derives facility ${derived}, not the declared ${type.facility}`);
      continue;
    }
    const allowed = type.catalogued ? catalogue[type.facility] : GENERIC_LOAN_VALUE_TYPES;
    if (!allowed) { problems.push(`loan type ${code}: ${type.facility} is not in the entitlement catalogue`); continue; }
    if (!type.catalogued && catalogue[type.facility]) {
      problems.push(`loan type ${code}: ${type.facility} is a catalogue component; mark it catalogued`);
    }
    for (const [grade, cell] of Object.entries(grid)) {
      if (!allowed.includes(cell.valueType)) problems.push(`loan type ${code} ${grade}: ${cell.valueType} is not allowed for ${type.facility}`);
    }
  }
  return problems;
}

/**
 * Grade loan limits. The housing advance is "3x monthly housing", stated as the equivalent multiple of
 * BASIC: 3 x 25% = 0.75x basic for G2–G4 and 3 x 30% = 0.9x for G5. That gives Mohammed (basic 8,000)
 * exactly the storyline's SAR 6,000. G1 is in-kind housing, so not eligible. (R2 added MultipleOfHousing
 * for LOAN_HOUSING_ADVANCE; the seed keeps the basic multiple so the figures do not depend on it.)
 */
export const GRADE_LOAN_LIMITS: Record<string, Record<GradeCode,
  { eligible: boolean; valueType: 'MultipleOfBasic' | 'EligibilityOnly'; rate?: number; note: string }>> = {
  PERSONAL: {
    G1: { eligible: true, valueType: 'MultipleOfBasic', rate: 1, note: 'Up to 1x basic' },
    G2: { eligible: true, valueType: 'MultipleOfBasic', rate: 1.5, note: 'Up to 1.5x basic' },
    G3: { eligible: true, valueType: 'MultipleOfBasic', rate: 2, note: 'Up to 2x basic' },
    G4: { eligible: true, valueType: 'MultipleOfBasic', rate: 3, note: 'Up to 3x basic' },
    G5: { eligible: true, valueType: 'MultipleOfBasic', rate: 4, note: 'Up to 4x basic' },
  },
  HOUSING_ADVANCE: {
    G1: { eligible: false, valueType: 'EligibilityOnly', note: 'Housing provided in kind' },
    G2: { eligible: true, valueType: 'MultipleOfBasic', rate: 0.75, note: '3x monthly housing (25% of basic)' },
    G3: { eligible: true, valueType: 'MultipleOfBasic', rate: 0.75, note: '3x monthly housing (25% of basic)' },
    G4: { eligible: true, valueType: 'MultipleOfBasic', rate: 0.75, note: '3x monthly housing (25% of basic)' },
    G5: { eligible: true, valueType: 'MultipleOfBasic', rate: 0.9, note: '3x monthly housing (30% of basic)' },
  },
};

/**
 * Asif's personal loan: SAR 4,800 over 12 months at SAR 400, disbursed in April 2026 under the
 * previous system and carried in at the 1 Aug 2026 go-live with 3 of 12 repaid. The seeded August and
 * September payrolls each take one instalment, so before October 5 are repaid and SAR 2,800 is owed;
 * the October payslip in the storyline then reads "6 of 12 left, SAR 2,400 still owed".
 */
export const GO_LIVE_CUTOVER = '2026-08-01';
export const ASIF_LOAN = {
  employeeCode: 'MLG-0003', loanNumber: 'MASAR-LN-2026-0417', loanTypeCode: 'PERSONAL',
  original: 4800, instalment: 400, total: 12, paidBeforeCutover: 3, outstandingAtCutover: 3600,
  firstUnpaidDue: '2026-08-25', disbursed: '2026-04-20', sourceSystem: 'Previous HR system', sourceRecordId: 'LN-0417',
} as const;

/**
 * The tenant's overtime policy. Without an ACTIVE one, POST /api/overtime/requests refuses every request
 * ("An active overtime policy is required"), so /ess/overtime cannot be demonstrated. KSA Art 107: the hourly
 * wage is basic / 240 (30 days x 8 hours) and an overtime hour adds 50% of it, including on rest days and
 * public holidays, so every day category is 1.5x. The payroll run floors these at the statutory rate anyway.
 * Caps are the product defaults: 4 h a day, 60 h a month (720 h a year), rounded to the nearest 15 minutes.
 */
export const OVERTIME_POLICY = {
  code: 'MASAR-OT-KSA', name: 'Masar Holding overtime (KSA Art 107)', hourlyRateBasis: 'BasicSalary',
  standardMonthlyHours: 240, minimumMinutes: 30, maximumMinutesPerDay: 240, monthlyCapMinutes: 3600,
  roundingRule: 'Nearest15', requiresApproval: true, allowCompOffConversion: false,
  regularDayMultiplier: 1.5, weekendMultiplier: 1.5, holidayMultiplier: 1.5,
} as const;

/** Asif's one absence day: Sunday 4 Oct 2026, a KSA working day, so it lands on the October payslip. */
export const ASIF_ABSENCE_DATE = '2026-10-04';

/** Payroll months processed and locked by the seed (both companies). */
export const PAYROLL_MONTHS: Array<{ year: number; month: number }> = [
  { year: 2026, month: 8 },
  { year: 2026, month: 9 },
];

// ── Release A storyline values the seed CANNOT write yet (consumed by the TODO hooks) ───────────

/** §5 matrix, for R1 to publish through its API once it exists. */
export const ENTITLEMENT_MATRIX = {
  effectiveFrom: '2026-11-01',
  housing: { G1: 'in kind (accommodation)', G2: '25% of basic', G3: '25% of basic', G4: '25% of basic', G5: '30% of basic' },
  transport: { G1: 'in kind (bus)', G2: '10% of basic', G3: '10% of basic', G4: 'SAR 1,000', G5: 'SAR 1,000' },
  airTicket: { basis: 'home-leave ticket per contract, non-Saudi', G1: '1x Economy, employee only', G2: '1x Economy, employee only', G3: '1x Economy, employee only', G4: 'Economy, family up to 3', G5: 'Business, family up to 4' },
  medical: { G1: 'CchiBasic, family', G2: 'CchiBasic, family', G3: 'B', G4: 'A', G5: 'VIP' },
  education: { G4: 'SAR 10,000 per child, up to 2', G5: 'SAR 15,000 per child, up to 3' },
  perDiem: { G1: 'SAR 150/day', G2: 'SAR 150/day', G3: 'SAR 250/day', G4: 'SAR 350/day', G5: 'SAR 500/day' },
  housingAdvance: 'G2+ up to 3x monthly housing, max 1 outstanding',
  companyOverrides: { MLG: { education: 'Skip', perDiemG1: 'SAR 200/day (Tailored)' } },
} as const;

/** Asif's traffic fine (storyline 8:00): SAR 150, counted toward the Art 92 cap. */
export const ASIF_TRAFFIC_FINE = { employeeCode: 'MLG-0003', amount: 150, period: '2026-10', reason: 'Traffic violation - company vehicle' } as const;
