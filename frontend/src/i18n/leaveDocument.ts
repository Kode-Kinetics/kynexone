/**
 * The supporting document of a leave request (a sick note and the like) opened from the HR leave screens and the
 * approvals screen. Only the leave's own employee and Admin / HR Manager / HR Officer ever see the action.
 */
const ar: Record<string, string> = {
  'View document': 'عرض المستند',
  'The supporting document could not be opened. It may have been removed.': 'تعذّر فتح المستند الداعم، فربما أُزيل.',
};

export const leaveDocument = { ar, en: Object.fromEntries(Object.keys(ar).map(key => [key, key])) as Record<string, string> };
