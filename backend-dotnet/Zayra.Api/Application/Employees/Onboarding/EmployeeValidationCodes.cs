namespace Zayra.Api.Application.Employees.Onboarding;

/// <summary>
/// Machine-readable codes of the employee input contract. The client reads these (never the English
/// fallback text); the same strings appear in the shared problem fixtures.
/// </summary>
public static class EmployeeProblemCodes
{
    // Problem "code" values.
    public const string ValidationFailed = "validation_failed";                 // 400
    public const string InvalidReference = "invalid_reference";                 // 422
    public const string InvalidCompany = "invalid_company";                     // 422
    public const string OverrideReasonRequired = "override_reason_required";    // 422
    public const string OverrideNotPermitted = "override_not_permitted";        // 422
    public const string IdempotencyKeyReuseMismatch = "idempotency_key_reuse_mismatch"; // 422
    public const string WarningsRequireAcknowledgement = "warnings_require_acknowledgement"; // 409
}

/// <summary>Per-field error codes carried under <c>errors.{field}[]</c> of a 400 <c>validation_failed</c>.</summary>
public static class EmployeeFieldErrors
{
    public const string Required = "required";
    public const string ValueTooLong = "value_too_long";
    public const string InvalidFormat = "invalid_format";
    public const string IdFormatInvalid = "id_format_invalid";
    public const string IdChecksumInvalid = "id_checksum_invalid";
    public const string IdPrefixNationalityMismatch = "id_prefix_nationality_mismatch";
    public const string NationalityUnknown = "nationality_unknown";
    public const string DobFuture = "dob_future";
    public const string DobOutOfRange = "dob_out_of_range";
    public const string JoiningOutOfRange = "joining_out_of_range";
    public const string ProbationOrder = "probation_order";
    public const string NegativeAmount = "negative_amount";
    public const string AmountOverflow = "amount_overflow";
    public const string IbanInvalid = "iban_invalid";
    public const string PhoneInvalid = "phone_invalid";
    public const string EmailInvalid = "email_invalid";
    public const string PhotoUrlScheme = "photo_url_scheme";
    public const string ListTooLong = "list_too_long";
    // CD17: contract dates are data integrity.
    public const string ContractEndBeforeStart = "contract_end_before_start";
    public const string ContractEndWithoutFixedTerm = "contract_end_without_fixed_term";
}

/// <summary>Statutory-floor warning codes (409 <c>warnings_require_acknowledgement</c>).</summary>
public static class EmployeeWarningCodes
{
    /// <summary>CD15: age AT THE JOINING DATE below 15. A warning (reason + employees.override_validation), never a block.</summary>
    public const string UnderMinimumAge = "under_minimum_age";
    /// <summary>CD14: joining more than 30 days in the past. Supersedes <see cref="JoiningBackdatedOver30d"/>.</summary>
    public const string JoiningBackdated = "joining_backdated";
    public const string DobUnder15 = "dob_under_15";
    public const string Juvenile15To17 = "juvenile_15_17";
    public const string AgeOver70 = "age_over_70";
    public const string JoiningBackdatedOver30d = "joining_backdated_over_30d";
    public const string JoiningOver180dFuture = "joining_over_180d_future";
    public const string ProbationOver180 = "probation_over_180";
    public const string ProbationOverLimit = "probation_over_limit";
    public const string IdExpired = "id_expired";
    public const string IdExpiring90d = "id_expiring_90d";
    public const string SalaryBelowWageFloor = "salary_below_wage_floor";
    public const string BasicBelow50Pct = "basic_below_50pct";
    public const string GradeBandOutOfRange = "grade_band_out_of_range";
    public const string MobileForeignForCompany = "mobile_foreign_for_company";
    public const string IdPrefixNationalityMismatch = "id_prefix_nationality_mismatch";
    public const string DuplicateProbable = "duplicate_probable";

    /// <summary>The one hard-error code a caller holding the override permission may waive with a reason (CD5).</summary>
    public const string IdChecksumInvalid = "id_checksum_invalid";
}
