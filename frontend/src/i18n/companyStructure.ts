/**
 * Setup → Company structure (Companies, Branches, Locations, Departments): permission reasons, company-tab copy that
 * used to be English only, and the field checks run before a save. Terms as in ./setupExperience and ./companyWizard:
 * الشركة (company), الفرع (branch), الموقع (location), الإدارة (department), المنطقة الزمنية (time zone),
 * الدولة (country), المدينة (city), نطاق السياج الجغرافي (geofence).
 */
const ar: Record<string, string> = {
  'You do not have permission to remove or restore this.': 'ليست لديك صلاحية حذف هذا العنصر أو استعادته.',
};

export const companyStructure = {
  en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) as Record<string, string>,
  ar,
};
