using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

/// <summary>How a benchmark set's percentiles are expressed. Only monthly figures are accepted, so they compare
/// directly with monthly pay of record without an annualisation guess.</summary>
public static class PayBenchmarkBases
{
    public const string MonthlyBasic = "MonthlyBasic";
    public const string MonthlyGross = "MonthlyGross";
    public static readonly IReadOnlyList<string> All = [MonthlyBasic, MonthlyGross];
}

/// <summary>
/// One imported pay survey / benchmark file, with its provenance.
///
/// <para>WHY A TABLE. KynexOne ships no market data and never will: every figure comes from a file the customer owns
/// and is licensed to use. The provenance (provider, licence or usage-rights statement, survey date, geography,
/// currency, pay basis, the file's SHA-256 and who imported it) is the evidence that the figures may be shown at
/// all, so it lives once per import and every row points at it. A set flagged synthetic is labelled
/// "SYNTHETIC — NOT MARKET DATA" wherever it is shown. HR owns it; kept until retired by HR (rows are small).</para>
/// </summary>
public class PayBenchmarkSet : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string LicenceStatement { get; set; } = string.Empty;
    public DateOnly SurveyDate { get; set; }
    /// <summary>ISO 3166 alpha-2 country the survey covers (e.g. "SA").</summary>
    public string Geography { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public string PayBasis { get; set; } = PayBenchmarkBases.MonthlyBasic;
    public bool IsSynthetic { get; set; }
    public string SourceFileName { get; set; } = string.Empty;
    public string SourceFileSha256 { get; set; } = string.Empty;
    public int RowCount { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid? ImportedByUserId { get; set; }
    public string ImportedByName { get; set; } = string.Empty;
    public DateTime ImportedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? RetiredAtUtc { get; set; }
}

/// <summary>
/// One job family × level row of an imported benchmark set, and the designation/grade it is mapped to.
///
/// <para>WHY A TABLE. The percentiles are the data; the mapping is on the row so a worksheet line finds its market
/// figure with one indexed lookup (designation first, then grade). A later import of the same family/level carries
/// the mapping forward. Lifetime = its set (cascade).</para>
/// </summary>
public class PayBenchmarkRow : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid SetId { get; set; }
    public string JobFamily { get; set; } = string.Empty;
    public string Level { get; set; } = string.Empty;
    public int SampleSize { get; set; }
    public decimal P25 { get; set; }
    public decimal P50 { get; set; }
    public decimal P75 { get; set; }
    public decimal P90 { get; set; }
    public Guid? DesignationId { get; set; }
    public Guid? GradeId { get; set; }
}
