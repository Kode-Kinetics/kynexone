/** Strings for the settled-service rules: a leaver whose final settlement is approved or paid comes back only through Rehire. English is the key; Arabic is below. */
const ar: Record<string, string> = {
  'Not yet measured': 'لم يُقَس بعد',
  'Already settled — use Rehire': 'تمت تسويته — استخدم إعادة التعيين',
  'The start date must be after the last working day that was already settled.': 'يجب أن يكون تاريخ البدء بعد آخر يوم عمل تمت تسويته.',
};

export const settledService = { ar, en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) };
