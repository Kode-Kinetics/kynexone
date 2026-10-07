import type { ReleaseADict } from './types';

/** Access screen privilege-ceiling strings (roles above the caller's own access are greyed out, with the reason). */
export const accessCeiling: ReleaseADict = {
  en: {
    'Above your access': 'Above your access',
    'Roles above your own access are greyed out, with the reason.': 'Roles above your own access are greyed out, with the reason.',
    'You cannot change your own roles. Another administrator must do it.': 'You cannot change your own roles. Another administrator must do it.',
    'You do not hold this permission, so you cannot grant it.': 'You do not hold this permission, so you cannot grant it.',
    'Roles updated.': 'Roles updated.',
    'Roles could not be updated.': 'Roles could not be updated.',
    'Not editable by you': 'Not editable by you',
    'Account still active — an Admin must deactivate it': 'Account still active — an Admin must deactivate it',
  },
  ar: {
    'Above your access': 'خارج صلاحياتك',
    'Roles above your own access are greyed out, with the reason.': 'الأدوار التي تتجاوز صلاحياتك معطّلة، مع توضيح السبب.',
    'You cannot change your own roles. Another administrator must do it.': 'لا يمكنك تغيير أدوارك بنفسك. يجب أن يقوم بذلك مسؤول آخر.',
    'You do not hold this permission, so you cannot grant it.': 'أنت لا تملك هذه الصلاحية، لذلك لا يمكنك منحها.',
    'Roles updated.': 'تم تحديث الأدوار.',
    'Roles could not be updated.': 'تعذّر تحديث الأدوار.',
    'Not editable by you': 'لا يمكنك تعديله',
    'Account still active — an Admin must deactivate it': 'الحساب لا يزال نشطاً — يجب أن يعطّله مسؤول نظام',
  },
};
