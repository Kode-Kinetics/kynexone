using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

public static class GosiBranches
{
    public const string Annuities           = "Annuities";
    public const string SANED               = "SANED";
    public const string OccupationalHazards = "OccupationalHazards";
}

public static class GosiPayers
{
    public const string Employee = "Employee";
    public const string Employer = "Employer";
}

public static class GosiClassifications
{
    public const string Saudi    = "Saudi";
    public const string GCC      = "GCC";
    public const string NonSaudi = "NonSaudi";
}

/// <summary>
/// Effective-dated GOSI contribution rule.
/// TenantId == Guid.Empty is a system-wide default (queried with IgnoreQueryFilters).
/// Tenant-specific overrides carry the tenant's own Guid and take precedence over defaults.
/// </summary>
public class GosiContributionRule : ITenantOwned
{
    public Guid     Id                   { get; set; } = Guid.NewGuid();
    public Guid     TenantId             { get; set; }   // Guid.Empty = system default
    public string   CountryCode          { get; set; } = "SA";
    public string   Classification       { get; set; } = GosiClassifications.Saudi;
    public string   Branch               { get; set; } = GosiBranches.Annuities;
    public string   Payer                { get; set; } = GosiPayers.Employee;

    /// <summary>
    /// The contribution rate as a decimal FRACTION of the contributory wage — 9% is <c>0.09</c>,
    /// 0.75% is <c>0.0075</c>. NEVER a percentage.
    ///
    /// <para>This is the same unit <c>StatutoryRule.RuleValue</c> carries for
    /// <c>gosi.saudi_employee_rate</c> / <c>gosi.saudi_employer_rate</c> / <c>gosi.saned_rate</c>
    /// (<c>Infrastructure/Seed/StatutoryRuleSeeder.cs</c>), which is what the payslip and the GOSI
    /// filing read through <c>KsaDeductionCalculator</c>. Until 2026-09 this column was a PERCENT
    /// and <c>GosiCalculationService</c> divided by 100, so one statutory fact lived in two stores
    /// in opposite units. Every write path validates through
    /// <c>StatutoryValueUnits.ValidateGosiBranchRate</c>; stored as <c>numeric(9,6)</c>.</para>
    /// </summary>
    public decimal  Rate                 { get; set; }
    public decimal? MinContributoryWage  { get; set; }
    public decimal? MaxContributoryWage  { get; set; }
    public DateOnly EffectiveFrom        { get; set; }
    public DateOnly? EffectiveTo         { get; set; }
    public bool     IsActive             { get; set; } = true;
    public string?  SourceReference      { get; set; }  // e.g. "GOSI Circular 2023/1"
    public string?  Notes                { get; set; }
    public DateTime CreatedAtUtc         { get; set; } = DateTime.UtcNow;
    public Guid?    CreatedBy            { get; set; }
}
