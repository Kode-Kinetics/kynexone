/** Small strings added by the demo round-1 fixes (leave balance wording, offer channel choices). English is the key; Arabic is below. */
const ar: Record<string, string> = {
  '{available} days available': '{available} يوماً متاحة',
  'In person': 'حضورياً',
  'Choose a channel': 'اختر القناة',
};

export const demoFixes = { ar, en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) };
