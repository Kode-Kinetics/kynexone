import { msg } from '../i18n/translations';

/**
 * What removing a country payroll key changes, in words, for the confirm dialog. Only `weekend_days`
 * is read by payroll today (it sets the work week); every other key is stored for reporting and
 * integrations. Returns dictionary keys.
 */
export function countryRuleRemovalEffect(ruleKey: string): string {
  return ruleKey.trim().toLowerCase() === 'weekend_days'
    ? msg('Removing weekend days changes the work week used for payroll proration and attendance. Countries without this key fall back to the Friday–Saturday default.')
    : msg('This key is stored for reporting and integrations only; removing it does not change payroll.');
}
