using System.ComponentModel.DataAnnotations.Schema;
using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

/// <summary>
/// Per-company, client-configurable income-tax policy. Opt-in: when a company has no policy
/// row (or <see cref="IsEnabled"/> is false / <see cref="TaxMode"/> is "None"), no tax is
/// withheld — the correct default for GCC jurisdictions with no personal income tax.
///
/// The same policy drives BOTH monthly payroll income tax and bonus tax withholding so the two
/// can never drift apart. <see cref="TaxMode"/> is intentionally a string (not an enum column)
/// so future modes (e.g. "Progressive" bracket tables) can be added without a schema change.
/// </summary>
public class CompanyTaxPolicy : ITenantOwned, ICompanyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>The company this policy applies to. One active policy per company.</summary>
    public Guid? CompanyId { get; set; }

    /// <summary>Master opt-in switch. False ⇒ no tax withheld regardless of mode/rate.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>"None" | "Flat" (extensible: "Progressive"). Default "None".</summary>
    public string TaxMode { get; set; } = TaxModes.None;

    /// <summary>Flat rate as a percentage (e.g. 10.00 = 10%). Used when TaxMode == "Flat".</summary>
    public decimal FlatRatePercent { get; set; }

    /// <summary>Apply tax to monthly salary (taxable components). Default true.</summary>
    public bool AppliesToSalary { get; set; } = true;

    /// <summary>Apply tax to bonuses. Default true.</summary>
    public bool AppliesToBonus { get; set; } = true;

    /// <summary>Country context (informational/labelling; sourced from the company).</summary>
    public string CountryCode { get; set; } = string.Empty;

    /// <summary>State/region/emirate context — supports "as per state rule" labelling.</summary>
    public string StateOrRegion { get; set; } = string.Empty;

    /// <summary>Free-text note describing the basis (e.g. statute reference) for audit.</summary>
    public string Notes { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public Guid? DeletedBy { get; set; }

    /// <summary>Effective rate as a fraction (0.10 for 10%) when enabled and in Flat mode; else 0.</summary>
    [NotMapped]
    public decimal EffectiveSalaryRateFraction =>
        IsEnabled && AppliesToSalary && TaxMode == TaxModes.Flat && FlatRatePercent > 0m
            ? FlatRatePercent / 100m : 0m;

    [NotMapped]
    public decimal EffectiveBonusRateFraction =>
        IsEnabled && AppliesToBonus && TaxMode == TaxModes.Flat && FlatRatePercent > 0m
            ? FlatRatePercent / 100m : 0m;
}

public static class TaxModes
{
    public const string None = "None";
    public const string Flat = "Flat";
    // public const string Progressive = "Progressive"; // reserved for bracket-based PIT
}
