namespace Zayra.Api.Application.CountryPack;

// ── Jurisdiction constants ────────────────────────────────────────────────────

public static class CountryCodes
{
    public const string Saudi   = "SAU";
    public const string Qatar   = "QAT";
    public const string UAE     = "ARE";
    public const string Kuwait  = "KWT";
    public const string Oman    = "OMN";
    public const string Bahrain = "BHR";
}

public static class Jurisdictions
{
    public const string KsaMainland   = "KSA-mainland";
    public const string QatarMainland = "QAT-mainland";
    public const string UAEMainland   = "UAE-mainland";
    public const string Difc          = "UAE-DIFC";
    public const string Adgm          = "UAE-ADGM";
}

// ── Salary breakdown ──────────────────────────────────────────────────────────
// GCC statutory bases (GOSI, GPSSA, GRSIA, EOSB) are computed on basic or
// basic+housing — NOT gross.  The breakdown is provided by the caller so
// each pack can apply the correct base without reaching back to the DB.

public sealed record SalaryBreakdown(
    decimal Basic,
    decimal HousingAllowance,
    decimal TransportAllowance,
    decimal OtherAllowances)
{
    public decimal Gross => Basic + HousingAllowance + TransportAllowance + OtherAllowances;
    // KSA GOSI "covered wage" = basic + housing; subject to ceiling set in StatutoryRule
    public decimal GosiCoveredWage => Basic + HousingAllowance;
    // UAE GPSSA contribution base (same logic)
    public decimal GpssaBase => Basic + HousingAllowance;
}

// ── Statutory deduction ───────────────────────────────────────────────────────

public sealed record StatutoryDeductionInput(
    Guid EmployeeId,
    Guid CompanyId,
    SalaryBreakdown Salary,
    string Nationality,
    string ContractType,
    int PeriodYear,
    int PeriodMonth)
{
    /// <summary>
    /// S1/A3 — the PERSON dimension the rate lookup has always lacked.
    ///
    /// <para>Every statutory rule in this product is keyed by PERIOD. Since 3 July 2024 the Saudi
    /// schedule is also keyed by the INDIVIDUAL: first-time entrants to the insured labour market are
    /// on a separate, rising ladder while existing subscribers stay on 9%/9%. UAE Federal Decree-Law
    /// 57/2023 created the same two-cohort split for Emiratis from 31 October 2023. Without a person
    /// dimension the system is structurally incapable of expressing "this employee is on the new
    /// scheme and this one is not", whatever rows you put in StatutoryRule.</para>
    ///
    /// <para>F02 — <see cref="SocialInsuranceFirstRegisteredOn"/> is now populated from
    /// <c>Employee.GosiFirstRegisteredOn</c>. The KSA pack resolves the person's cohort from it
    /// (<see cref="GosiCohorts"/>) and looks the annuities rates up by cohort AND period
    /// (<c>KsaGosiCohortSchedule</c>). The new-entrant schedule itself is still not implemented — its
    /// rates and step dates need the official source ([SME]) — so a new entrant is computed on the
    /// pre-reform schedule, the payslip says so, and the validator BLOCKS that person. Packs must treat
    /// null as "unknown", never as "new entrant".</para>
    /// </summary>
    public DateOnly? DateOfBirth { get; init; }

    /// <inheritdoc cref="DateOfBirth"/>
    public DateOnly? SocialInsuranceFirstRegisteredOn { get; init; }

    /// <summary>
    /// F02 — a cohort to use VERBATIM instead of resolving one from
    /// <see cref="SocialInsuranceFirstRegisteredOn"/>. Set only by reconstruction paths (GOSI
    /// reconciliation), which must recompute a slip on the cohort it was PROCESSED on — frozen on
    /// <c>PayrollSlip.GosiCohort</c> — not on whatever the employee record says today. Null on the run path.
    /// </summary>
    public string? SocialInsuranceCohort { get; init; }
}

public sealed record StatutoryDeductionLine(string Code, string Label, decimal EmployeeAmount, decimal EmployerAmount)
{
    /// <summary>
    /// The rate the line was computed at, as a decimal FRACTION of <see cref="ContributoryWage"/>
    /// (0.09 = 9%), or null for a pack that does not state one. A witness, not an input: it lets a
    /// preview or report show the rate the payslip actually applied instead of reading a second store.
    /// </summary>
    public decimal? Rate { get; init; }

    /// <summary>The clamped contributory wage the rate was applied to, or null when not stated.</summary>
    public decimal? ContributoryWage { get; init; }
}

public sealed record StatutoryDeductionResult(
    decimal TotalEmployeeDeduction,
    decimal TotalEmployerContribution,
    IReadOnlyList<StatutoryDeductionLine> Lines)
{
    /// <summary>
    /// F02 — the person-level cohort the lines were computed on (<see cref="GosiCohorts"/>), or null when
    /// no cohort applies (an expatriate, a GCC national, a pack without a cohort dimension). Frozen on the
    /// payslip so validation and reconciliation judge what was actually computed.
    /// </summary>
    public string? SocialInsuranceCohort { get; init; }

    /// <summary>
    /// F02 — the calculation explanation for these lines in plain language: which cohort, which rates,
    /// and "unverified" / "not modelled" when that is the truth. Null when the pack gives none.
    /// </summary>
    public string? Basis { get; init; }
}

// ── End of service ───────────────────────────────────────────────────────────
// Service dates (not pre-computed years) are required because proration and
// rounding rules are country-specific (day-count vs month-count vs week-count).

public sealed record EndOfServiceInput(
    Guid EmployeeId,
    Guid CompanyId,
    SalaryBreakdown Salary,
    DateOnly ServiceStartDate,
    DateOnly ServiceEndDate,
    string TerminationReason,
    string ContractType,
    string Nationality)
{
    /// <summary>
    /// S1/A1 — the tenant's CONFIGURED EOSB wage: the sum of every PayComponent flagged
    /// <c>EosbIncluded</c>, including components that have no structure-field slot in
    /// <see cref="Salary"/> (a Fixed "car allowance", a PercentOfBasic component, …).
    ///
    /// <para>It is a FLOOR-RAISER, never a floor-lowerer. A pack applies its own statutory base and
    /// then takes the greater of the two, so tenant configuration can only ever be MORE generous than
    /// statute — you cannot configure your way under Saudi Labour Law Art. 84. Zero means "the tenant
    /// has no EOSB-flagged component catalog", and the pack falls back to statute alone.</para>
    /// </summary>
    public decimal ConfiguredEosbWage { get; init; }

    /// <summary>
    /// S1/A8 — calendar days of UNPAID leave taken inside the service period. UAE Decree-Law 33/2021
    /// Art. 51 excludes these from the service period for gratuity expressly; KSA and Qatar turn on
    /// "continuous service" and are configurable per pack (see <c>eosb.exclude_unpaid_leave</c>).
    /// </summary>
    public int UnpaidLeaveDays { get; init; }

    /// <summary>
    /// S1 — the tenant's own EOSB policy from <c>GCCComplianceSetting</c>: the three fields the
    /// Saudi compliance-config screen has always let a customer edit
    /// (<c>EosbYears1To5Rate</c> / <c>EosbYearsAbove5Rate</c> / <c>EosbMinYears</c>) and which
    /// nothing read. The <c>EosbEnabled</c> toggle beside them DOES work, so the panel looks live.
    ///
    /// <para>The same rule governs these as governs <see cref="ConfiguredEosbWage"/>: configuration
    /// may be MORE generous than statute and never less. An enhanced days-per-year rate is honoured;
    /// one below the statutory floor is refused and named. A minimum-service year count above the
    /// statutory gate is refused outright, because in every GCC jurisdiction modelled here that would
    /// deny a statutory entitlement — KSA Art. 84 in particular grants the award from day one on an
    /// employer-initiated termination.</para>
    /// </summary>
    public EosbPolicyOverride? Policy { get; init; }
}

/// <summary>
/// S1 — the tenant-configured EOSB policy, as entered on the country compliance-config screen.
/// Zero / null means "not configured", which is different from "configured to zero".
/// </summary>
public sealed record EosbPolicyOverride(
    decimal? Tier1DaysPerYear,
    decimal? Tier2DaysPerYear,
    int? MinServiceYears);

public sealed record EndOfServiceBreakdown(string Label, decimal Amount);

public sealed record EndOfServiceResult(
    decimal TotalGratuity,
    string ApplicableRule,
    IReadOnlyList<EndOfServiceBreakdown> Breakdown)
{
    /// <summary>
    /// S1 — statutory notices the pack wants surfaced ON THE SETTLEMENT, not buried in a snapshot
    /// column. This is the channel a pack uses to say "I included transport on a [COUNSEL] default"
    /// or "this jurisdiction's post-2020 service is a trustee obligation you must NOT pay as cash".
    /// The controller copies them verbatim into the settlement's warning list.
    /// </summary>
    public IReadOnlyList<string> Notices { get; init; } = Array.Empty<string>();

    /// <summary>
    /// S1 — the wage the pack actually awarded on, after it applied its own statutory base. The
    /// settlement displays and persists THIS, not the controller's guess, so "what wage was this
    /// gratuity computed on?" has one answer and it is the one the money came from.
    /// </summary>
    public decimal AppliedWageBase { get; init; }
}

// ── Wage protection employee row ──────────────────────────────────────────────
// The exporter receives the full union of all WPS format fields so no pack
// needs to reach back to the DB mid-export.

public sealed record WpsEmployee(
    int EmployeeId,     // matches Employee.Id (int PK throughout this system)
    string EmployeeCode,
    string FullNameEn,
    string FullNameAr,
    string Nationality,
    string NationalId,        // QID (QA), Emirates ID (UAE), National ID (KSA)
    string IbanOrAccount,
    string BankCode,
    SalaryBreakdown Salary,
    decimal NetPay);

// ── Wage protection export ────────────────────────────────────────────────────

public sealed record WageProtectionExportInput(
    Guid TenantId,
    Guid CompanyId,
    Guid PayrollRunId,
    int PeriodYear,
    int PeriodMonth,
    string EstablishmentId,   // KSA: Mudad employer ID; UAE: MOHRE establishment; QA: CR number
    string EmployerIban,
    string CompanyNameEn,
    string CompanyNameAr,
    IReadOnlyList<WpsEmployee> Employees);

public sealed record WageProtectionExportResult(
    byte[] FileBytes,
    string FileName,
    string Format,
    int RecordCount);

// ── Nationalization tracking ──────────────────────────────────────────────────
// The caller pre-computes headcount from the employee roster so the tracker
// stays pure (no DB queries inside the strategy).

public sealed record NationalizationInput(
    Guid TenantId,
    Guid CompanyId,
    int TotalHeadcount,
    int NationalHeadcount);

public enum NationalizationComplianceStatus { Compliant, AtRisk, NonCompliant, NotApplicable }

public sealed record NationalizationResult(
    double TargetRatio,
    double CurrentRatio,
    int TotalHeadcount,
    int NationalHeadcount,
    NationalizationComplianceStatus Status,
    string SchemeLabel);

// ── Localization profile ──────────────────────────────────────────────────────

public sealed record LocalizationProfile(
    string CurrencyCode,
    string CurrencySymbol,
    string LocaleCode,
    bool IsRtl,
    string DateFormat,
    string CalendarSystem);

// ── Pack descriptor ───────────────────────────────────────────────────────────
// Static metadata returned by ICountryPackDescriptor — lets controllers surface
// the resolved pack's behaviour without branching on country code.

public sealed record PackDescriptor(
    string CountryCode,
    string CountryNameEn,
    string CountryNameAr,
    string SocialInsuranceScheme,
    string SocialInsuranceDescription,
    string EosbFormula,
    string WpsFormat,
    string WpsFormatLabel,
    string NationalizationScheme);

// ── Country registry entry ────────────────────────────────────────────────────

public sealed record AvailableJurisdiction(string Code, string Label);

public sealed record AvailableCountryPack(
    string CountryCode,
    string NameEn,
    string NameAr,
    IReadOnlyList<AvailableJurisdiction> Jurisdictions);
