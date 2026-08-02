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
    int PeriodMonth);

public sealed record StatutoryDeductionLine(string Code, string Label, decimal EmployeeAmount, decimal EmployerAmount);

public sealed record StatutoryDeductionResult(
    decimal TotalEmployeeDeduction,
    decimal TotalEmployerContribution,
    IReadOnlyList<StatutoryDeductionLine> Lines);

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
    string Nationality);

public sealed record EndOfServiceBreakdown(string Label, decimal Amount);

public sealed record EndOfServiceResult(
    decimal TotalGratuity,
    string ApplicableRule,
    IReadOnlyList<EndOfServiceBreakdown> Breakdown);

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
