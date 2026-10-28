import { expect, test } from '@playwright/test';
import { LOCALE_DICTS } from '../src/i18n/translations';
import { SERVER_CODE_KEYS } from '../src/lib/hire/problem';

/** 40-ARCHITECTURE section 3.1: every code the server can send the wizard. The UI renders from the code, never from `detail`. */
const HARD_FIELD_CODES = ['required', 'value_too_long', 'invalid_format', 'id_format_invalid', 'id_checksum_invalid', 'id_prefix_nationality_mismatch', 'nationality_unknown', 'dob_future', 'dob_out_of_range', 'joining_out_of_range', 'probation_order', 'negative_amount', 'amount_overflow', 'iban_invalid', 'phone_invalid', 'email_invalid', 'photo_url_scheme', 'list_too_long', 'contract_end_before_start', 'contract_end_without_fixed_term'];
const BUSINESS_CODES = ['invalid_reference', 'invalid_company', 'override_reason_required', 'override_not_permitted', 'idempotency_key_reuse_mismatch', 'company_country_missing', 'work_email_conflict', 'cost_centre_reason_required', 'possible_duplicate', 'warnings_require_acknowledgement', 'rate_limited', 'draft_changed_elsewhere', 'draft_basis_required'];
const WARNING_CODES = ['dob_under_15', 'under_minimum_age', 'juvenile_15_17', 'age_over_70', 'joining_backdated_over_30d', 'joining_backdated', 'joining_over_180d_future', 'probation_over_180', 'probation_over_limit', 'id_expired', 'id_expiring_90d', 'salary_below_wage_floor', 'basic_below_50pct', 'grade_band_out_of_range', 'mobile_foreign_for_company', 'duplicate_probable'];

test('everyServerProblemCode_hasEnAndArMessage', () => {
  for (const code of [...HARD_FIELD_CODES, ...BUSINESS_CODES, ...WARNING_CODES]) {
    const key = SERVER_CODE_KEYS[code];
    expect(key, `no message key for ${code}`).toBeTruthy();
    expect(LOCALE_DICTS.en[key], `en ${code}`).toBe(key);
    expect(LOCALE_DICTS.ar[key], `ar ${code}`).toBeTruthy();
    expect(LOCALE_DICTS.ar[key], `ar ${code} must differ from English`).not.toBe(key);
  }
});
