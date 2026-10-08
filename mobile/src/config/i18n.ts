// ============================================================
// ZAYRA MOBILE — i18n (Arabic / English)
// ============================================================

import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import { Alert, I18nManager } from 'react-native';
import { reloadAppAsync } from 'expo';
import AsyncStorage from '@react-native-async-storage/async-storage';
import { STORAGE_KEYS } from '@/config';
import {
  applyDirection,
  markRestartPrompted,
  offerRestartOnce,
  readStoredLanguage,
  type AppLanguage,
} from './languageDirection';
import { mfaAr, mfaEn } from './mfaStrings';
import { welcomeAr, welcomeEn } from './welcomeStrings';
import { selfieAr, selfieEn } from './selfieStrings';

export type { AppLanguage };

export const en = {
  common: {
    loading: 'Loading...',
    error: 'Something went wrong',
    retry: 'Retry',
    cancel: 'Cancel',
    confirm: 'Confirm',
    save: 'Save',
    submit: 'Submit',
    approve: 'Approve',
    reject: 'Reject',
    sendBack: 'Send Back',
    close: 'Close',
    back: 'Back',
    next: 'Next',
    done: 'Done',
    edit: 'Edit',
    delete: 'Delete',
    upload: 'Upload',
    download: 'Download',
    search: 'Search',
    filter: 'Filter',
    all: 'All',
    today: 'Today',
    yesterday: 'Yesterday',
    thisMonth: 'This Month',
    noData: 'No data available',
    noResults: 'No results found',
    required: 'This field is required',
    success: 'Success',
    pending: 'Pending',
    approved: 'Approved',
    rejected: 'Rejected',
    cancelled: 'Cancelled',
    expired: 'Expired',
    days: 'days',
    hours: 'hours',
    minutes: 'minutes',
  },
  auth: {
    login: 'Sign In',
    logout: 'Sign Out',
    username: 'Username',
    password: 'Password',
    tenantId: 'Company ID',
    forgotPassword: 'Forgot Password?',
    resetPassword: 'Reset Password',
    changePassword: 'Change Password',
    oldPassword: 'Current Password',
    newPassword: 'New Password',
    confirmPassword: 'Confirm Password',
    biometricLogin: 'Sign in with Biometrics',
    sessionExpired: 'Your session has expired. Please sign in again.',
    accountLocked: 'Your account is locked. Contact HR.',
    invalidCredentials: 'Invalid username or password.',
    firstLogin: 'Welcome! Please set your password to continue.',
  },
  mfa: mfaEn,
  signin: welcomeEn,
  selfie: selfieEn,
  nav: {
    home: 'Home',
    attendance: 'Attendance',
    leave: 'Leave',
    payslips: 'Payslips',
    more: 'More',
    team: 'Team',
    approvals: 'Approvals',
    profile: 'Profile',
    documents: 'Documents',
    requests: 'Requests',
    policies: 'Policies',
    notifications: 'Notifications',
    aiAssistant: 'AI Assistant',
    settings: 'Settings',
  },
  dashboard: {
    welcome: 'Good {{period}}, {{name}}',
    morning: 'morning',
    afternoon: 'afternoon',
    evening: 'evening',
    clockIn: 'Clock In',
    clockOut: 'Clock Out',
    todayStatus: "Today's Status",
    leaveBalance: 'Leave Balance',
    pendingRequests: 'Pending Requests',
    latestPayslip: 'Latest Payslip',
    expiringDocuments: 'Expiring Documents',
    upcomingHolidays: 'Upcoming Holidays',
    teamPresent: 'Present Today',
    teamAbsent: 'Absent',
    pendingApprovals: 'Pending Approvals',
  },
  attendance: {
    title: 'Attendance',
    clockIn: 'Clock In',
    clockOut: 'Clock Out',
    breakIn: 'Break In',
    breakOut: 'Break Out',
    clockingIn: 'Clocking in...',
    clockingOut: 'Clocking out...',
    locationRequired: 'Location access is required for attendance.',
    geofenceViolation: 'You are outside the approved work location.',
    present: 'Present',
    absent: 'Absent',
    late: 'Late',
    halfDay: 'Half Day',
    onLeave: 'On Leave',
    holiday: 'Holiday',
    weekend: 'Weekend',
    missingPunch: 'Missing Punch',
    correction: 'Correction Request',
    history: 'Attendance History',
    monthView: 'Month View',
    workHours: 'Work Hours',
    shift: 'Shift',
    workLocation: 'Work Location',
    lateBy: 'Late by {{minutes}} min',
    missingPunchRequest: 'Request Correction',
  },
  leave: {
    title: 'Leave',
    balance: 'Leave Balance',
    apply: 'Apply for Leave',
    history: 'Leave History',
    type: 'Leave Type',
    startDate: 'Start Date',
    endDate: 'End Date',
    halfDay: 'Half Day',
    morning: 'Morning',
    afternoon: 'Afternoon',
    reason: 'Reason',
    attachment: 'Attachment',
    totalDays: 'Total Days',
    available: 'Available',
    used: 'Used',
    pending: 'Pending',
    cancel: 'Cancel Leave',
    teamCalendar: 'Team Leave Calendar',
  },
  overtime: {
    title: 'Overtime',
    submit: 'Submit OT Request',
    date: 'Date',
    startTime: 'Start Time',
    endTime: 'End Time',
    reason: 'Reason',
    project: 'Project / Cost Center',
    total: 'Total Hours',
    calculation: 'OT Calculation',
    compOff: 'Convert to Comp-Off',
    history: 'OT History',
  },
  payslips: {
    title: 'Payslips',
    download: 'Download Payslip',
    gross: 'Gross Salary',
    net: 'Net Salary',
    deductions: 'Total Deductions',
    earnings: 'Earnings',
    period: 'Pay Period',
    paymentStatus: 'Payment Status',
    wpsRef: 'WPS Reference',
    ytd: 'Year to Date',
    paid: 'Paid',
    processed: 'Processed',
  },
  profile: {
    title: 'My Profile',
    employeeId: 'Employee ID',
    fullName: 'Full Name',
    jobTitle: 'Job Title',
    department: 'Department',
    manager: 'Reporting Manager',
    email: 'Email',
    phone: 'Phone',
    nationality: 'Nationality',
    joiningDate: 'Date of Joining',
    contractType: 'Contract Type',
    emergencyContacts: 'Emergency Contacts',
    identityDocs: 'Identity Documents',
    requestUpdate: 'Request Profile Update',
  },
  documents: {
    title: 'Documents',
    upload: 'Upload Document',
    expiring: 'Expiring Soon',
    expired: 'Expired',
    valid: 'Valid',
    expiresOn: 'Expires {{date}}',
    daysLeft: '{{days}} days left',
    passport: 'Passport',
    iqama: 'Iqama',
    emiratesId: 'Emirates ID',
    visa: 'Visa',
    nationalId: 'National ID',
  },
  requests: {
    title: 'HR Requests',
    new: 'New Request',
    type: 'Request Type',
    salaryCertificate: 'Salary Certificate',
    experienceLetter: 'Experience Letter',
    noc: 'NOC',
    complaint: 'Complaint',
    grievance: 'Grievance',
    general: 'General HR Request',
    subject: 'Subject',
    description: 'Description',
    status: 'Status',
    comments: 'Comments',
    addComment: 'Add Comment',
    slaDeadline: 'SLA Deadline',
    open: 'Open',
    inProgress: 'In Progress',
    resolved: 'Resolved',
    closed: 'Closed',
  },
  approvals: {
    title: 'Approvals',
    pending: 'Pending',
    history: 'History',
    approveConfirm: 'Approve this request?',
    rejectConfirm: 'Reject this request?',
    reason: 'Reason (required for rejection)',
    comment: 'Comment',
    requestedBy: 'Requested by',
    requestedOn: 'Requested on',
    details: 'Request Details',
  },
  notifications: {
    title: 'Notifications',
    markAllRead: 'Mark all as read',
    noNotifications: 'No notifications',
    preferences: 'Notification Preferences',
  },
  aiAssistant: {
    title: 'AI HR Assistant',
    placeholder: 'Ask anything about your leave, attendance, payslips...',
    thinking: 'Thinking...',
    disclaimer: 'AI responses are advisory only and may not reflect the latest data.',
    suggestedQuestions: 'Suggested Questions',
  },
  settings: {
    language: 'Language',
    languageChanged: 'Language changed',
    changedToEnglish: 'Changed to English',
    changedToArabic: 'Changed to Arabic',
    restartTitle: 'Restart to finish',
    restartBody: 'The layout switches direction the next time KynexOne starts. Close the app completely and open it again.',
    restartNow: 'Restart now',
    later: 'Later',
  },
};

/** The shape every language must match: same namespaces and keys, string values. */
export type TranslationResources = typeof en;

// Arabic translations — typed against `en`, so a key missing here fails `tsc`.
const ar: TranslationResources = {
  common: {
    loading: 'جار التحميل...',
    error: 'حدث خطأ ما',
    retry: 'إعادة المحاولة',
    cancel: 'إلغاء',
    confirm: 'تأكيد',
    save: 'حفظ',
    submit: 'إرسال',
    approve: 'موافقة',
    reject: 'رفض',
    sendBack: 'إرجاع',
    close: 'إغلاق',
    back: 'رجوع',
    next: 'التالي',
    done: 'تم',
    edit: 'تعديل',
    delete: 'حذف',
    upload: 'رفع',
    download: 'تنزيل',
    search: 'بحث',
    filter: 'تصفية',
    all: 'الكل',
    today: 'اليوم',
    yesterday: 'أمس',
    thisMonth: 'هذا الشهر',
    noData: 'لا تتوفر بيانات',
    noResults: 'لا توجد نتائج',
    required: 'هذا الحقل مطلوب',
    success: 'نجح',
    pending: 'قيد الانتظار',
    approved: 'موافق عليه',
    rejected: 'مرفوض',
    cancelled: 'ملغي',
    expired: 'منتهي الصلاحية',
    days: 'أيام',
    hours: 'ساعات',
    minutes: 'دقائق',
  },
  auth: {
    login: 'تسجيل الدخول',
    logout: 'تسجيل الخروج',
    username: 'اسم المستخدم',
    password: 'كلمة المرور',
    tenantId: 'رمز الشركة',
    forgotPassword: 'نسيت كلمة المرور؟',
    resetPassword: 'إعادة تعيين كلمة المرور',
    changePassword: 'تغيير كلمة المرور',
    oldPassword: 'كلمة المرور الحالية',
    newPassword: 'كلمة المرور الجديدة',
    confirmPassword: 'تأكيد كلمة المرور',
    biometricLogin: 'تسجيل الدخول ببصمة الأصبع',
    sessionExpired: 'انتهت جلستك. يرجى تسجيل الدخول مجدداً.',
    accountLocked: 'حسابك مقفل. تواصل مع الموارد البشرية.',
    invalidCredentials: 'اسم المستخدم أو كلمة المرور غير صحيحة.',
    firstLogin: 'مرحباً! يرجى تعيين كلمة المرور للمتابعة.',
  },
  mfa: mfaAr,
  signin: welcomeAr,
  selfie: selfieAr,
  nav: {
    home: 'الرئيسية',
    attendance: 'الحضور',
    leave: 'الإجازات',
    payslips: 'قسائم الراتب',
    more: 'المزيد',
    team: 'الفريق',
    approvals: 'الموافقات',
    profile: 'الملف الشخصي',
    documents: 'المستندات',
    requests: 'الطلبات',
    policies: 'السياسات',
    notifications: 'الإشعارات',
    aiAssistant: 'المساعد الذكي',
    settings: 'الإعدادات',
  },
  dashboard: {
    welcome: 'صباح الخير، {{name}}',
    morning: 'صباحاً',
    afternoon: 'مساءً',
    evening: 'مساءً',
    clockIn: 'تسجيل الحضور',
    clockOut: 'تسجيل الانصراف',
    todayStatus: 'حالة اليوم',
    leaveBalance: 'رصيد الإجازات',
    pendingRequests: 'الطلبات المعلقة',
    latestPayslip: 'آخر قسيمة راتب',
    expiringDocuments: 'مستندات منتهية الصلاحية',
    upcomingHolidays: 'الإجازات القادمة',
    teamPresent: 'حاضرون اليوم',
    teamAbsent: 'غائبون',
    pendingApprovals: 'موافقات معلقة',
  },
  attendance: {
    title: 'الحضور',
    clockIn: 'تسجيل الحضور',
    clockOut: 'تسجيل الانصراف',
    breakIn: 'بدء الاستراحة',
    breakOut: 'نهاية الاستراحة',
    clockingIn: 'جار تسجيل الحضور...',
    clockingOut: 'جار تسجيل الانصراف...',
    locationRequired: 'مطلوب الوصول إلى الموقع للحضور.',
    geofenceViolation: 'أنت خارج موقع العمل المعتمد.',
    present: 'حاضر',
    absent: 'غائب',
    late: 'متأخر',
    halfDay: 'نصف يوم',
    onLeave: 'في إجازة',
    holiday: 'عطلة',
    weekend: 'نهاية الأسبوع',
    missingPunch: 'تسجيل حضور مفقود',
    correction: 'طلب تصحيح',
    history: 'سجل الحضور',
    monthView: 'عرض شهري',
    workHours: 'ساعات العمل',
    shift: 'الوردية',
    workLocation: 'موقع العمل',
    lateBy: 'تأخر {{minutes}} دقيقة',
    missingPunchRequest: 'طلب تصحيح',
  },
  leave: {
    title: 'الإجازات',
    balance: 'رصيد الإجازات',
    apply: 'تقديم طلب إجازة',
    history: 'سجل الإجازات',
    type: 'نوع الإجازة',
    startDate: 'تاريخ البداية',
    endDate: 'تاريخ النهاية',
    halfDay: 'نصف يوم',
    morning: 'صباحي',
    afternoon: 'مسائي',
    reason: 'السبب',
    attachment: 'مرفق',
    totalDays: 'إجمالي الأيام',
    available: 'متاح',
    used: 'مستخدم',
    pending: 'معلق',
    cancel: 'إلغاء الإجازة',
    teamCalendar: 'تقويم إجازات الفريق',
  },
  overtime: {
    title: 'العمل الإضافي',
    submit: 'تقديم طلب عمل إضافي',
    date: 'التاريخ',
    startTime: 'وقت البدء',
    endTime: 'وقت الانتهاء',
    reason: 'السبب',
    project: 'المشروع / مركز التكلفة',
    total: 'إجمالي الساعات',
    calculation: 'حساب العمل الإضافي',
    compOff: 'تحويل إلى إجازة تعويضية',
    history: 'سجل العمل الإضافي',
  },
  payslips: {
    title: 'قسائم الراتب',
    download: 'تنزيل قسيمة الراتب',
    gross: 'الراتب الإجمالي',
    net: 'الراتب الصافي',
    deductions: 'إجمالي الخصومات',
    earnings: 'المستحقات',
    period: 'فترة الاستحقاق',
    paymentStatus: 'حالة الدفع',
    wpsRef: 'رقم مرجع WPS',
    ytd: 'منذ بداية العام',
    paid: 'مدفوع',
    processed: 'تمت المعالجة',
  },
  profile: {
    title: 'ملفي الشخصي',
    employeeId: 'رقم الموظف',
    fullName: 'الاسم الكامل',
    jobTitle: 'المسمى الوظيفي',
    department: 'القسم',
    manager: 'المدير المباشر',
    email: 'البريد الإلكتروني',
    phone: 'الهاتف',
    nationality: 'الجنسية',
    joiningDate: 'تاريخ الانضمام',
    contractType: 'نوع العقد',
    emergencyContacts: 'جهات الاتصال الطارئة',
    identityDocs: 'وثائق الهوية',
    requestUpdate: 'طلب تحديث الملف',
  },
  documents: {
    title: 'المستندات',
    upload: 'رفع مستند',
    expiring: 'ينتهي قريباً',
    expired: 'منتهي الصلاحية',
    valid: 'ساري',
    expiresOn: 'ينتهي {{date}}',
    daysLeft: 'متبقي {{days}} يوم',
    passport: 'جواز السفر',
    iqama: 'الإقامة',
    emiratesId: 'هوية الإمارات',
    visa: 'التأشيرة',
    nationalId: 'الهوية الوطنية',
  },
  requests: {
    title: 'طلبات الموارد البشرية',
    new: 'طلب جديد',
    type: 'نوع الطلب',
    salaryCertificate: 'شهادة راتب',
    experienceLetter: 'خطاب خبرة',
    noc: 'شهادة عدم ممانعة',
    complaint: 'شكوى',
    grievance: 'تظلم',
    general: 'طلب عام',
    subject: 'الموضوع',
    description: 'الوصف',
    status: 'الحالة',
    comments: 'التعليقات',
    addComment: 'إضافة تعليق',
    slaDeadline: 'موعد SLA',
    open: 'مفتوح',
    inProgress: 'قيد المعالجة',
    resolved: 'تم الحل',
    closed: 'مغلق',
  },
  approvals: {
    title: 'الموافقات',
    pending: 'معلقة',
    history: 'السجل',
    approveConfirm: 'الموافقة على هذا الطلب؟',
    rejectConfirm: 'رفض هذا الطلب؟',
    reason: 'السبب (مطلوب للرفض)',
    comment: 'تعليق',
    requestedBy: 'طلب بواسطة',
    requestedOn: 'طلب في',
    details: 'تفاصيل الطلب',
  },
  notifications: {
    title: 'الإشعارات',
    markAllRead: 'تحديد الكل كمقروء',
    noNotifications: 'لا توجد إشعارات',
    preferences: 'إعدادات الإشعارات',
  },
  aiAssistant: {
    title: 'المساعد الذكي',
    placeholder: 'اسأل عن إجازاتك أو حضورك أو راتبك...',
    thinking: 'جار التفكير...',
    disclaimer: 'ردود الذكاء الاصطناعي استشارية فقط.',
    suggestedQuestions: 'أسئلة مقترحة',
  },
  settings: {
    language: 'اللغة',
    languageChanged: 'تم تغيير اللغة',
    changedToEnglish: 'تم التغيير إلى الإنجليزية',
    changedToArabic: 'تم التغيير إلى العربية',
    restartTitle: 'أعد التشغيل للإكمال',
    restartBody: 'سيتغير اتجاه الواجهة عند تشغيل KynexOne في المرة القادمة. أغلق التطبيق تمامًا ثم افتحه مجددًا.',
    restartNow: 'إعادة التشغيل الآن',
    later: 'لاحقًا',
  },
};

/**
 * Language at cold start, synchronously. The persisted choice is in AsyncStorage (async), but the
 * native layout direction is already decided before JS runs: I18nManager.isRTL reflects the last
 * forceRTL() call, which only takes effect on a restart. Starting from it means the first frame's
 * text matches the first frame's layout; `restoreLanguage()` then applies the stored choice.
 */
function bootLanguage(): AppLanguage {
  return I18nManager.isRTL ? 'ar' : 'en';
}

i18n.use(initReactI18next).init({
  compatibilityJSON: 'v3',
  resources: { en: { translation: en }, ar: { translation: ar } },
  lng: bootLanguage(),
  fallbackLng: 'en',
  interpolation: { escapeValue: false },
});

/** Remembers which language a restart was last offered for, so the offer is made once (see languageDirection.ts). */
const RESTART_PROMPTED_KEY = `${STORAGE_KEYS.LANGUAGE}_restart_prompted`;

function promptRestart(): void {
  Alert.alert(i18n.t('settings.restartTitle'), i18n.t('settings.restartBody'), [
    { text: i18n.t('settings.later'), style: 'cancel' },
    {
      text: i18n.t('settings.restartNow'),
      onPress: () => {
        reloadAppAsync('Language direction changed').catch((error) =>
          console.warn('[i18n] Reload failed; the direction applies on next launch:', error));
      },
    },
  ]);
}

/**
 * Runs once, at import: apply the user's saved language. Text switches now and the native direction
 * is set for the next start. Resolves to the language and whether the running layout disagrees.
 */
async function restoreLanguage(): Promise<{ lang: AppLanguage; mismatch: boolean } | null> {
  const stored = await readStoredLanguage(AsyncStorage, STORAGE_KEYS.LANGUAGE);
  if (!stored) return null;
  if (i18n.language !== stored) await i18n.changeLanguage(stored);
  return { lang: stored, mismatch: applyDirection(stored, I18nManager) };
}

const restored = restoreLanguage().catch((error) => {
  console.warn('[i18n] Could not restore the saved language:', error);
  return null;
});

/**
 * Call from App after the first screen has mounted (InteractionManager.runAfterInteractions): if the
 * saved language disagrees with the native direction (the choice was made but the app never fully
 * restarted), offer a restart — once per language, never in a loop. Not at import: Android drops an
 * Alert raised before an Activity has a window, and the prompt was then marked as shown anyway.
 */
export async function promptRestartIfNeeded(): Promise<void> {
  const state = await restored;
  if (!state) return;
  await offerRestartOnce(state.lang, state.mismatch, AsyncStorage, RESTART_PROMPTED_KEY, promptRestart);
}

/**
 * The user picked a language: translate now, persist the choice, and set the native direction.
 * Resolves to `{ restartRequired: true }` when the layout direction changes on the next start;
 * the caller (SettingsScreen) offers the restart, and startup will not offer it a second time.
 */
export async function setLanguage(lang: AppLanguage): Promise<{ restartRequired: boolean }> {
  await i18n.changeLanguage(lang);
  try {
    await AsyncStorage.setItem(STORAGE_KEYS.LANGUAGE, JSON.stringify(lang));
  } catch (error) {
    console.warn('[i18n] Could not persist the language choice:', error);
  }
  const restartRequired = applyDirection(lang, I18nManager);
  if (restartRequired) await markRestartPrompted(lang, AsyncStorage, RESTART_PROMPTED_KEY);
  return { restartRequired };
}


export default i18n;
