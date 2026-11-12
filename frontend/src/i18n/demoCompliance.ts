/**
 * Saudi Compliance honesty copy: what the Saudization tab says when no band can be stated, and the readiness
 * wording that follows the payroll run. Terms follow ./GLOSSARY.md.
 */
const ar: Record<string, string> = {
  'No band is shown: the MHRSD thresholds for this activity are not loaded or not verified.':
    'لا يُعرض نطاق: حدود وزارة الموارد البشرية والتنمية الاجتماعية لهذا النشاط غير محمَّلة أو لم يتم التحقق منها.',
  'The range ends yesterday (company time zone) by default: today and later days are processed once they have ended.':
    'ينتهي النطاق افتراضيًا بالأمس (بتوقيت الشركة): تُعالَج الأيام الحالية والقادمة بعد انتهائها.',
};

export const demoCompliance = { ar, en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) as Record<string, string> };
