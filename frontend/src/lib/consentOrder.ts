/** The consent text is shown in both languages; the session's own language comes first (CD65). */
export const consentOrder = (locale: string): Array<'en' | 'ar'> => (locale === 'ar' ? ['ar', 'en'] : ['en', 'ar']);
