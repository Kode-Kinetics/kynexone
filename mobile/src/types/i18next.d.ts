// ============================================================
// Typed translation keys
// ============================================================
//
// Registers the English resources as i18next's resource shape, so `t('dashboard.leaveBalanse')`
// — a typo or a key that does not exist — fails `tsc` (npm run typecheck / CI) instead of
// rendering the raw key on a phone. Arabic is typed against the same shape in config/i18n.ts,
// so a key added to English and forgotten in Arabic fails too.

import 'i18next';
import type { TranslationResources } from '@/config/i18n';

declare module 'i18next' {
  interface CustomTypeOptions {
    defaultNS: 'translation';
    resources: { translation: TranslationResources };
  }
}
