using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

public class MigrationImportBatch : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string? ExternalBatchId { get; set; }
    public string PackageChecksum { get; set; } = string.Empty;
    public string PackageType { get; set; } = "OrganizationStructure";
    public string Status { get; set; } = "Previewed";
    public bool DryRun { get; set; }
    public string CurrentSection { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public int ReceivedRows { get; set; }
    public int CreatedRows { get; set; }
    public int UpdatedRows { get; set; }
    public int SkippedRows { get; set; }
    public int ErrorRows { get; set; }
    public string ReconciliationJson { get; set; } = "{}";
    public string ErrorJson { get; set; } = "[]";
    public string ResultJson { get; set; } = "{}";
    public Guid? CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

public sealed record MigrationPackageRequest(
    string? ExternalBatchId,
    Dictionary<string, string> Sections,
    bool DryRun = false);

public sealed record MigrationReconciliationDto(
    Guid BatchId,
    string Status,
    string PackageChecksum,
    int ReceivedRows,
    int CreatedRows,
    int UpdatedRows,
    int SkippedRows,
    int ErrorRows,
    string CurrentSection,
    IReadOnlyDictionary<string, int> SectionCounts,
    IReadOnlyList<string> Errors);

public sealed record MigrationSectionResultDto(string Section, int Received, int Created, int Updated, int Skipped, decimal AmountTotal);

public sealed record MigrationGovernedLedgerDto(
    IReadOnlyDictionary<string, MigrationSectionResultDto> Sections,
    IReadOnlyList<PayrollOpeningBalanceLedgerRow> PayrollOpeningBalances,
    IReadOnlyList<BenefitEnrollmentHistoryLedgerRow> BenefitsEnrollmentHistory,
    IReadOnlyList<MigrationSignoffLedgerRow> ReconciliationSignoffs);

public sealed record PayrollOpeningBalanceLedgerRow(
    string EmployeeCode,
    int Year,
    string BalanceType,
    string ComponentCode,
    decimal Amount,
    string Currency,
    string SourceSystem,
    string SourceRecordId);

public sealed record BenefitEnrollmentHistoryLedgerRow(
    string EmployeeCode,
    string PlanCode,
    string PlanName,
    string CoverageTier,
    DateOnly EffectiveDate,
    DateOnly? EndDate,
    decimal EmployeeContribution,
    decimal EmployerContribution,
    string Currency,
    string Status,
    string SourceSystem,
    string SourceRecordId);

public sealed record MigrationSignoffLedgerRow(
    string ReconciliationType,
    string SourceSystem,
    string PreparedBy,
    string ApprovedBy,
    DateTime SignedAtUtc,
    int VarianceCount,
    decimal VarianceAmount,
    string Status,
    string EvidenceUri);
