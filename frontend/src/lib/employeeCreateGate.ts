/**
 * ONE condition and ONE wording for "the employing company has no country, so no employee can be
 * created under it".
 *
 * <p>THE DEFECT this exists to close: the Add Employee modal already showed the correct amber warning
 * ("Until it is set there is nothing to require and nothing to save here") while its Create Employee
 * button stayed ENABLED. The presenter clicked it and got a failure instead of the warning the UI had
 * already computed. The warning and the button now read the SAME predicate from here, so they cannot
 * disagree — and the message carries a real link to where the fix lives.</p>
 *
 * <p>The server is the authority, not this file: EmployeeManagementService refuses the same create with
 * HomeJurisdiction.CompanyMessage (backend-dotnet/Zayra.Api/Application/Common/HomeJurisdiction.cs),
 * whose wording missingCompanyCountryMessage mirrors exactly. A disabled button is an affordance, not
 * a guard.</p>
 */

/** Where a tenant administrator fixes a company's country (mirrors HomeJurisdiction.CompanyFixLocation). */
export const COMPANY_COUNTRY_FIX_LOCATION = 'Setup → Companies';

/** Deep link to that exact tab — the modal's one clear next action. */
export const COMPANY_COUNTRY_FIX_HREF = '/setup?tab=companies';

/** id of the modal's warning, so the disabled submit button can point at it with aria-describedby. */
export const MISSING_COUNTRY_NOTICE_ID = 'employee-create-missing-country';

/**
 * True when an employing company IS selected and still resolves to no usable country.
 *
 * Kept distinct from "no company chosen yet": that state has a different (and correct) hint, and
 * blocking it would stop a group administrator before they have picked the legal entity at all.
 *
 * @param companySelected whether the form resolved an employing company
 * @param normalizedCountryCode the company's country AFTER normalization (ISO-2, or '' when unusable)
 */
export function isEmployeeCreateBlockedByCountry(
  companySelected: boolean,
  normalizedCountryCode: string | null | undefined,
): boolean {
  return companySelected && (normalizedCountryCode ?? '').trim().length === 0;
}

/**
 * The single sentence both the warning and the disabled button's tooltip speak. Mirrors the server's
 * HomeJurisdiction.CompanyMessage, including naming the company — a message that does not name the
 * company cannot be acted on.
 */
export function missingCompanyCountryMessage(companyName: string | null | undefined): string {
  const name = (companyName ?? '').trim() || 'This company';
  return `${name} has no country set — set it in ${COMPANY_COUNTRY_FIX_LOCATION} before adding employees.`;
}
