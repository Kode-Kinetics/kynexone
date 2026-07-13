using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

public class PayrollRun : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public string Status { get; set; } = "Draft";
    public decimal TotalGrossSalary { get; set; }
    public decimal TotalDeductions { get; set; }
    public decimal TotalNetSalary { get; set; }
    // Employer statutory cost (GOSI/GPSSA/GRSIA employer side) — not deducted from employee net.
    public decimal TotalEmployerStatutoryCost { get; set; }
    public int EmployeeCount { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public Guid? ProcessedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAtUtc { get; set; }
    public DateTime? LockedAtUtc { get; set; }
    public string ErpPostingStatus { get; set; } = ErpPostingStatuses.NotReady;
    public DateTime? ErpPostingStatusChangedAtUtc { get; set; }
    public string? ErpPostingReference { get; set; }
    public string? ErpPostingFailureReason { get; set; }
    // Void tracking — populated only when Status == "Voided".
    public string? VoidReason { get; set; }
    public DateTime? VoidedAtUtc { get; set; }
    public Guid? VoidedByUserId { get; set; }
    public string? VoidedByName { get; set; }
}

public static class ErpPostingStatuses
{
    public const string NotReady = "NotReady";
    public const string ReadyForErp = "ReadyForErp";
    public const string Exported = "Exported";
    public const string Posted = "Posted";
    public const string Rejected = "Rejected";

    public static readonly string[] All = { NotReady, ReadyForErp, Exported, Posted, Rejected };
}

public static class ErpPostingTransitions
{
    private static readonly IReadOnlyDictionary<string, string[]> Allowed =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [ErpPostingStatuses.NotReady] = new[] { ErpPostingStatuses.ReadyForErp },
            [ErpPostingStatuses.ReadyForErp] = new[] { ErpPostingStatuses.Exported, ErpPostingStatuses.Posted, ErpPostingStatuses.Rejected },
            [ErpPostingStatuses.Exported] = new[] { ErpPostingStatuses.Posted, ErpPostingStatuses.Rejected },
            [ErpPostingStatuses.Rejected] = new[] { ErpPostingStatuses.Exported, ErpPostingStatuses.Posted },
            [ErpPostingStatuses.Posted] = Array.Empty<string>(),
        };

    public static bool IsAllowed(string from, string to) =>
        Allowed.TryGetValue(from, out var next) && next.Contains(to, StringComparer.OrdinalIgnoreCase);

    public static string[] AllowedFrom(string from) =>
        Allowed.TryGetValue(from, out var next) ? next : Array.Empty<string>();
}

public class PayrollSlip : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Legal-entity scope. Backfilled by CompanyScopeBackfill; required for new operational writes.</summary>
    public Guid? CompanyId { get; set; }
    public Guid RunId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeCode { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public decimal BasicSalary { get; set; }
    public decimal HousingAllowance { get; set; }
    public decimal TransportAllowance { get; set; }
    public decimal OtherAllowances { get; set; }
    public decimal GrossSalary { get; set; }
    public decimal Deductions { get; set; }
    public decimal NetSalary { get; set; }
    public string Status { get; set; } = "Draft";
    // Statutory deduction totals — split for reporting without re-querying PayrollDeductions.
    // EmployeeStatutoryTotal reduces employee net pay; EmployerStatutoryTotal does NOT.
    public decimal EmployeeStatutoryTotal { get; set; }
    public decimal EmployerStatutoryTotal { get; set; }
    // Compliance: YTD accumulators (populated during Process, from all prior locked runs in same year)
    public decimal YtdGross { get; set; }
    public decimal YtdDeductions { get; set; }
    public decimal YtdNet { get; set; }
    // Compliance: loan/advance deductions this period (for payslip line-item)
    public decimal LoanDeductions { get; set; }
}
