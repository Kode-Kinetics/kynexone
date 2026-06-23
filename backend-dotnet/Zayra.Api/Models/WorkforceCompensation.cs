using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

public class LeavePolicyEligibility : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid LeavePolicyId { get; set; }
    public string CountryCode { get; set; } = string.Empty;
    public Guid? CompanyId { get; set; }
    public Guid? BranchId { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? GradeId { get; set; }
    public string EmploymentType { get; set; } = string.Empty;
    public string ContractType { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class LeaveAccrualRule : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid LeavePolicyId { get; set; }
    public string AccrualFrequency { get; set; } = "Monthly";
    public decimal AccrualDays { get; set; }
    public int CarryForwardExpiryDays { get; set; }
    public decimal CarryForwardMaxDays { get; set; }
    public bool NegativeBalanceAllowed { get; set; }
    public bool IsActive { get; set; } = true;
}

public class LeaveRequestDate : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid LeaveRequestId { get; set; }
    public DateOnly LeaveDate { get; set; }
    public decimal DayValue { get; set; } = 1;
    public bool IsPublicHoliday { get; set; }
    public bool IsWeekend { get; set; }
}

public class LeaveAttachment : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid LeaveRequestId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string StorageUrl { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
}

public class OvertimePolicy : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Guid? BranchId { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? GradeId { get; set; }
    public string HourlyRateBasis { get; set; } = "BasicSalary";
    public decimal FixedHourlyRate { get; set; }
    public int StandardMonthlyHours { get; set; } = 240;
    public int MinimumMinutes { get; set; } = 30;
    public int MaximumMinutesPerDay { get; set; } = 240;
    public int MonthlyCapMinutes { get; set; } = 3600;
    public string RoundingRule { get; set; } = "Nearest15";
    public bool RequiresApproval { get; set; } = true;
    public bool AllowCompOffConversion { get; set; } = true;
    public bool RamadanReducedHoursPlaceholder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public Guid? DeletedBy { get; set; }
}

public class OvertimeType : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = "Regular";
    public bool IsActive { get; set; } = true;
}

public class OvertimeMultiplier : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OvertimePolicyId { get; set; }
    public Guid? OvertimeTypeId { get; set; }
    public string DayCategory { get; set; } = "RegularDay";
    public decimal Multiplier { get; set; } = 1.25m;
    public bool IsActive { get; set; } = true;
}

public class OvertimeRule : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OvertimePolicyId { get; set; }
    public string RuleType { get; set; } = string.Empty;
    public string RuleValueJson { get; set; } = "{}";
    public bool IsActive { get; set; } = true;
}

public class OvertimeRequest : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public Guid? OvertimePolicyId { get; set; }
    public Guid? OvertimeTypeId { get; set; }
    public DateOnly WorkDate { get; set; }
    public DateTime StartTimeUtc { get; set; }
    public DateTime EndTimeUtc { get; set; }
    public int RequestedMinutes { get; set; }
    public int ApprovedMinutes { get; set; }
    public string Source { get; set; } = "Manual";
    public string Reason { get; set; } = string.Empty;
    public string Status { get; set; } = "PendingManager";
    public Guid? AttendanceDailyRecordId { get; set; }
    public Guid? ProjectId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
}

public class OvertimeApproval : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OvertimeRequestId { get; set; }
    public string ApprovalLevel { get; set; } = "Manager";
    public string Decision { get; set; } = "Pending";
    public string Notes { get; set; } = string.Empty;
    public Guid? DecidedByUserId { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
}

public class OvertimeCalculation : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OvertimeRequestId { get; set; }
    public int EmployeeId { get; set; }
    public decimal ApprovedHours { get; set; }
    public decimal HourlyRate { get; set; }
    public decimal Multiplier { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "AED";
    public string CalculationJson { get; set; } = "{}";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class OvertimePayrollImpact : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OvertimeRequestId { get; set; }
    public int EmployeeId { get; set; }
    public Guid? PayrollRunId { get; set; }
    public decimal Hours { get; set; }
    public decimal Amount { get; set; }
    public string Status { get; set; } = "PendingPayroll";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAtUtc { get; set; }
}

public class OvertimeAdjustment : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    public Guid? OvertimeRequestId { get; set; }
    public decimal HoursAdjustment { get; set; }
    public decimal AmountAdjustment { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class OvertimeBudget : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? ProjectId { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public decimal BudgetAmount { get; set; }
    public decimal ConsumedAmount { get; set; }
    public string Currency { get; set; } = "AED";
}

public class OvertimeCompOffConversion : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OvertimeRequestId { get; set; }
    public int EmployeeId { get; set; }
    public decimal OvertimeHours { get; set; }
    public decimal CompOffDays { get; set; }
    public string Status { get; set; } = "Pending";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class OvertimeAuditLog : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string EntityName { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string MetadataJson { get; set; } = "{}";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? UserId { get; set; }
}

public class SalaryStructure : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Currency { get; set; } = "AED";
    public DateOnly EffectiveDate { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public bool IsDeleted { get; set; }
}

public class SalaryComponent : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? SalaryStructureId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ComponentType { get; set; } = "Earning";
    public string CalculationType { get; set; } = "Fixed";
    public decimal Amount { get; set; }
    public decimal Percentage { get; set; }
    public bool IsTaxable { get; set; }
    public bool IsActive { get; set; } = true;
}

public class EmployeeSalaryStructure : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    public Guid SalaryStructureId { get; set; }
    public decimal BasicSalary { get; set; }
    public decimal HousingAllowance { get; set; }
    public decimal TransportAllowance { get; set; }
    public decimal FoodAllowance { get; set; }
    public decimal MobileAllowance { get; set; }
    public decimal OtherAllowance { get; set; }
    public decimal FixedDeduction { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public string Currency { get; set; } = "AED";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
}

public class PayrollGroup : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Currency { get; set; } = "AED";
    public bool IsActive { get; set; } = true;
}

public class PayrollCycle : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? PayrollGroupId { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public string Status { get; set; } = "Open";
}

public class PayrollRunEmployee : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PayrollRunId { get; set; }
    public int EmployeeId { get; set; }
    public decimal GrossEarnings { get; set; }
    public decimal TotalDeductions { get; set; }
    public decimal NetPay { get; set; }
    public string Status { get; set; } = "Draft";
}

public class PayrollEarning : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PayrollRunId { get; set; }
    public int EmployeeId { get; set; }
    public string ComponentCode { get; set; } = string.Empty;
    public string ComponentName { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Source { get; set; } = "Salary";
}

public class PayrollDeduction : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PayrollRunId { get; set; }
    public int EmployeeId { get; set; }
    public string ComponentCode { get; set; } = string.Empty;
    public string ComponentName { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Source { get; set; } = "Manual";
}

public class PayrollAllowance : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PayrollRunId { get; set; }
    public int EmployeeId { get; set; }
    public string AllowanceType { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}

public class PayrollAdjustment : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PayrollRunId { get; set; }
    public int EmployeeId { get; set; }
    public string AdjustmentType { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
}

public class PayrollApproval : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PayrollRunId { get; set; }
    public string ApprovalLevel { get; set; } = "Payroll";
    public string Decision { get; set; } = "Pending";
    public string Notes { get; set; } = string.Empty;
    public Guid? DecidedByUserId { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
}

public class PayrollValidationResult : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PayrollRunId { get; set; }
    public int? EmployeeId { get; set; }
    public string Severity { get; set; } = "Info";
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool IsResolved { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class PayrollException : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PayrollRunId { get; set; }
    public int? EmployeeId { get; set; }
    public string ExceptionType { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string Status { get; set; } = "Open";
}

public class Payslip : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PayrollRunId { get; set; }
    public int EmployeeId { get; set; }
    public string PayslipNumber { get; set; } = string.Empty;
    public string Language { get; set; } = "en";
    public bool IsPublishedToEss { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? PublishedAtUtc { get; set; }
    // Immutable reference to the PayslipTemplate version used when this payslip was generated.
    // Null for payslips generated before the template designer was introduced.
    public Guid? PayslipTemplateId { get; set; }
}

public class PayslipComponent : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PayslipId { get; set; }
    public string ComponentType { get; set; } = string.Empty;
    public string ComponentName { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}

public class PayrollPaymentBatch : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PayrollRunId { get; set; }
    public string BatchNumber { get; set; } = string.Empty;
    public string PaymentMethod { get; set; } = "WPS";
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "AED";
    public string Status { get; set; } = "Draft";

    /// <summary>WPS submission lifecycle. See <see cref="WpsStatuses"/>.</summary>
    public string WpsStatus { get; set; } = "Draft";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>WPS/SIF submission lifecycle states for a payment batch.</summary>
public static class WpsStatuses
{
    public const string Draft      = "Draft";
    public const string Generated  = "Generated";
    public const string Downloaded = "Downloaded";
    public const string Submitted  = "Submitted";
    public const string Accepted   = "Accepted";
    public const string Rejected   = "Rejected";
    public const string Reconciled = "Reconciled";

    public static readonly string[] All =
        { Draft, Generated, Downloaded, Submitted, Accepted, Rejected, Reconciled };
}

/// <summary>
/// Enforces allowed WPS lifecycle transitions.
/// Invalid transitions are rejected with 400 to prevent status corruption.
/// </summary>
public static class WpsTransitions
{
    private static readonly IReadOnlyDictionary<string, string[]> Allowed =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [WpsStatuses.Draft]      = new[] { WpsStatuses.Generated },
            [WpsStatuses.Generated]  = new[] { WpsStatuses.Downloaded, WpsStatuses.Submitted },
            [WpsStatuses.Downloaded] = new[] { WpsStatuses.Submitted },
            [WpsStatuses.Submitted]  = new[] { WpsStatuses.Accepted, WpsStatuses.Rejected },
            [WpsStatuses.Accepted]   = new[] { WpsStatuses.Reconciled },
            // Rejected allows re-export: a new WPSFileBatch is created, then status reverts to Generated.
            [WpsStatuses.Rejected]   = new[] { WpsStatuses.Generated },
            [WpsStatuses.Reconciled] = Array.Empty<string>(),
        };

    public static bool IsAllowed(string from, string to)
        => Allowed.TryGetValue(from, out var next) && next.Contains(to, StringComparer.OrdinalIgnoreCase);

    public static string[] AllowedFrom(string from)
        => Allowed.TryGetValue(from, out var next) ? next : Array.Empty<string>();
}

public class PayrollPaymentRecord : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PaymentBatchId { get; set; }
    public int EmployeeId { get; set; }
    public decimal Amount { get; set; }
    public string Iban { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public string WpsReference { get; set; } = string.Empty;
}

public class BankTransferFile : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PaymentBatchId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FileContent { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class WPSFileBatch : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PaymentBatchId { get; set; }
    public string SifFileName { get; set; } = string.Empty;
    public string Status { get; set; } = "Generated";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    // ── Export metadata (Track A PR-2) ────────────────────────────────────────
    /// <summary>User who triggered the SIF file generation.</summary>
    public Guid? GeneratedByUserId { get; set; }
    /// <summary>Number of employee records in the generated file.</summary>
    public int EmployeeCount { get; set; }
    /// <summary>Sum of NetPay across all SIF records.</summary>
    public decimal TotalSalaryAmount { get; set; }
    /// <summary>SHA-256 hex digest of the generated file content for integrity verification.</summary>
    public string FileHash { get; set; } = string.Empty;
    /// <summary>Format version tag (e.g. SIF_SA_V1). Allows future format evolution.</summary>
    public string FormatVersion { get; set; } = "SIF_SA_V1";
}

public class SIFFileRecord : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid WPSFileBatchId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeCode { get; set; } = string.Empty;
    public string Iban { get; set; } = string.Empty;
    public decimal NetPay { get; set; }
    /// <summary>Ministry of Labour / national ID — required by CBUAE WPS v2 and Saudi Mudad.</summary>
    public string MolId { get; set; } = string.Empty;
    /// <summary>Bank branch routing / sort code required in SIF E1EDL20 segment.</summary>
    public string RoutingCode { get; set; } = string.Empty;
}

public class EOSBCalculation : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    public DateOnly CalculationDate { get; set; }
    public decimal EligibleSalary { get; set; }
    public decimal CalculatedAmount { get; set; }
    public string RulesSnapshotJson { get; set; } = "{}";
    public string Status { get; set; } = "Draft";
}

public class PayrollAuditLog : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string EntityName { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string MetadataJson { get; set; } = "{}";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? UserId { get; set; }
}
