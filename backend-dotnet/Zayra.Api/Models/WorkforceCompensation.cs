using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

public class LeavePolicyEligibility : ITenantOwned, ICompanyScoped
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

public class OvertimeRequest : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Legal-entity scope. Backfilled by CompanyScopeBackfill; required for new operational writes.</summary>
    public Guid? CompanyId { get; set; }
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
    /// <summary>Optimistic version for manager/HR/final decision compare-and-swap.</summary>
    public int DecisionVersion { get; set; }
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
    /// <summary>
    /// The multiplier approved at the time the overtime request was approved.
    /// Set from OvertimeMultiplier.Multiplier based on the request's DayCategory
    /// (e.g. 1.5 for regular OT, 2.0 for holiday/rest-day per KSA Art.107).
    /// 0.0 means unset — payroll will fall back to the statutory standard multiplier.
    /// </summary>
    public decimal ApprovedMultiplier { get; set; } = 0m;
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

public class SalaryStructure : ITenantOwned, ICompanyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Currency { get; set; } = "AED";
    public DateOnly EffectiveDate { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public decimal MinGrossSalary { get; set; }
    public decimal MaxGrossSalary { get; set; }
    public decimal MinBasicSalary { get; set; }
    public decimal MaxBasicSalary { get; set; }
    public string EligibleGradeIdsJson { get; set; } = "[]";
    public string EligibleDesignationIdsJson { get; set; } = "[]";
    public int VersionNumber { get; set; } = 1;
    public Guid? PreviousVersionId { get; set; }
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

public class PayrollDeduction : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Legal-entity scope. Backfilled by CompanyScopeBackfill; required for new operational writes.</summary>
    public Guid? CompanyId { get; set; }
    public Guid PayrollRunId { get; set; }
    public int EmployeeId { get; set; }
    public string ComponentCode { get; set; } = string.Empty;
    public string ComponentName { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Source { get; set; } = "Manual";
    /// <summary>
    /// True for employer-side statutory contributions (e.g. GOSI-ANN-ER, GOSI-OH-ER).
    /// These do NOT reduce employee net pay — they are employer cost lines tracked separately
    /// for GL routing and compliance reporting.
    /// </summary>
    public bool IsEmployerContribution { get; set; }
}

public class BenefitPlan : ITenantOwned, ICompanyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string PlanType { get; set; } = "Medical";
    public string Currency { get; set; } = "AED";
    public DateOnly EffectiveFrom { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public DateOnly? EffectiveTo { get; set; }
    public bool RequiresEnrollment { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public bool IsDeleted { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
}

public class BenefitEligibilityRule : ITenantOwned, ICompanyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid BenefitPlanId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid? GradeId { get; set; }
    public DateOnly EffectiveFrom { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public DateOnly? EffectiveTo { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
}

public class BenefitEnrollment : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid BenefitPlanId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string CoverageTier { get; set; } = "Employee";
    public DateOnly EffectiveFrom { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public DateOnly? EffectiveTo { get; set; }
    public string Status { get; set; } = "Active";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
}

public class BenefitContribution : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid BenefitEnrollmentId { get; set; }
    public Guid BenefitPlanId { get; set; }
    public int EmployeeId { get; set; }
    public decimal EmployeeAmount { get; set; }
    public decimal EmployerAmount { get; set; }
    public string Frequency { get; set; } = "Monthly";
    public string PayrollComponentCode { get; set; } = string.Empty;
    public DateOnly EffectiveFrom { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public DateOnly? EffectiveTo { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
}

public class BenefitPayrollDeductionLink : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid BenefitEnrollmentId { get; set; }
    public Guid BenefitContributionId { get; set; }
    public Guid PayrollDeductionId { get; set; }
    public Guid PayrollRunId { get; set; }
    public int EmployeeId { get; set; }
    public decimal LinkedAmount { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
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
    /// <summary>Stable business source type for exactly-once externally-created adjustments.</summary>
    public string SourceType { get; set; } = string.Empty;
    /// <summary>Stable source aggregate id; unique with TenantId/SourceType when populated.</summary>
    public Guid? SourceId { get; set; }
}

public static class PayrollAdjustmentSources
{
    public const string LeaveEncashment = "LeaveEncashment";
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

    // ── POD-B3: who cleared this blocking error, when, and why ────────────────────────────────────
    // Before B3 nothing in the codebase ever SET IsResolved (it was read at Approve/Lock/overview and
    // written nowhere), so any blocking Error on a Processed run was exit-only-via-Void. These columns
    // are the attribution for the override; the DURABLE record lives in PayrollValidationOverride,
    // because /validate deletes and rebuilds every result row wholesale.
    public Guid? ResolvedByUserId { get; set; }
    public string? ResolvedByName { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
    public string? ResolvedReason { get; set; }
}

/// <summary>
/// POD-B3 — a DURABLE, audited override of one blocking validation code on one run.
///
/// <para>Why this is not a flag on <see cref="PayrollValidationResult"/>: <c>POST runs/{id}/validate</c>
/// <c>ExecuteDelete</c>s every stored result for the run and re-adds the engine's fresh output, so an
/// override recorded on the result row is silently erased by the next validate and the run re-sticks.
/// Process and Validate both re-apply <c>IsResolved</c> from these rows after writing results, so an
/// override survives any number of re-validations — and is wiped by a re-Process, because a re-processed
/// run's FACTS have changed and a judgement made about the old figures no longer applies.</para>
///
/// <para>Keyed (TenantId, PayrollRunId, Code, EmployeeId). EmployeeId is nullable: a run-level code (e.g.
/// TOTALS_*) has no employee. Postgres treats NULLs as distinct in a unique index, so the uniqueness is
/// advisory for run-level codes — the upsert in the endpoint re-reads by the same predicate first.</para>
/// </summary>
public class PayrollValidationOverride : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Legal-entity scope, stamped from the run.</summary>
    public Guid? CompanyId { get; set; }
    public Guid PayrollRunId { get; set; }
    public int? EmployeeId { get; set; }
    public string Code { get; set; } = string.Empty;
    /// <summary>Mandatory, non-blank. The reason a compliance error was consciously accepted.</summary>
    public string Reason { get; set; } = string.Empty;
    public Guid? OverriddenByUserId { get; set; }
    public string OverriddenByName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// POD-B3 — the PERSISTED WITNESS of everything a payroll run CONSUMED, written by Process inside the run
/// transaction and replayed in reverse by the void / reopen unwind.
///
/// <para>WHY THIS EXISTS. The unwind cannot be recomputed from the run's outputs:</para>
/// <list type="bullet">
/// <item>Loans: Process writes ONE aggregate <c>LOAN_EMI</c> deduction per EMPLOYEE
///   (<c>empLoans.Sum(...)</c>), so an employee with two loans has no per-loan attribution; and the
///   <c>LoanInstallment</c> stamp is written only <c>if (inst is not null)</c>, so a loan with no
///   schedule row is decremented with no witness at all. Recomputing from
///   <c>InstallmentAmount</c> is not a fallback — a mid-period schedule change would corrupt the
///   reversal, restoring a different number than was taken.</item>
/// <item>Attendance and Leave impacts carry NO PayrollRunId, so "which run marked this Processed?" is
///   otherwise unanswerable and a re-run of the month would silently drop LOP, absence and OT.</item>
/// </list>
///
/// <para>Every row records the artifact's state BEFORE the run touched it, so the restore puts back what
/// was actually there rather than what the current configuration implies.</para>
/// </summary>
public class PayrollRunConsumption : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid PayrollRunId { get; set; }
    /// <summary>See <see cref="PayrollConsumptionArtifacts"/>.</summary>
    public string ArtifactType { get; set; } = string.Empty;
    public Guid ArtifactId { get; set; }
    public int EmployeeId { get; set; }
    /// <summary>The amount this run consumed (EMI taken, impact amount). Zero for non-monetary artifacts.</summary>
    public decimal Amount { get; set; }
    public string? PriorStatus { get; set; }
    public decimal? PriorOutstandingBalance { get; set; }
    public decimal? PriorTotalRepaid { get; set; }
    public decimal? PriorAmountPaid { get; set; }
    public Guid? PriorPayrollRunId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>POD-B3 — artifact vocabulary for <see cref="PayrollRunConsumption.ArtifactType"/>.</summary>
public static class PayrollConsumptionArtifacts
{
    public const string Loan               = "Loan";
    public const string LoanInstallment    = "LoanInstallment";
    public const string Advance            = "Advance";
    public const string AdvanceInstallment = "AdvanceInstallment";
    public const string AttendanceImpact   = "AttendanceImpact";
    public const string LeaveImpact        = "LeaveImpact";
    public const string OvertimeImpact     = "OvertimeImpact";
    public const string Adjustment         = "Adjustment";
    /// <summary>POD-C3 — a PayrollEmployeeReceivable this run NETTED, with its prior RecoveredAmount in
    /// PriorAmountPaid, so a void of the recovering run restores the receivable exactly.</summary>
    public const string EmployeeReceivable = "EmployeeReceivable";
    /// <summary>POD-C1 — an EmployeeFinalSettlement this run DISBURSED. PriorStatus carries the status
    /// before disbursement (always <c>Approved</c>), so a void puts the settlement back where it was and
    /// it can be re-disbursed on a fresh OffCycle run. Without it a voided settlement run would leave the
    /// settlement stranded in <c>Disbursing</c>/<c>Paid</c> with its 2320 payable re-opened by the void
    /// contra and nothing able to consume it.</summary>
    public const string FinalSettlement = "FinalSettlement";
    /// <summary>POD-C1 — an EmployeeLeaveBalance whose <c>Encashed</c> this run INCREMENTED to pay a
    /// leaver's encashment. PriorAmountPaid carries the prior Encashed value so the void restores the row
    /// exactly rather than subtracting a re-derived number. Distinct from <see cref="LeaveImpact"/>, which
    /// is an unpaid-leave DEDUCTION artifact and restores a completely different entity.</summary>
    public const string LeaveEncashment = "LeaveEncashment";
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

public class Payslip : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Legal-entity scope. Backfilled by CompanyScopeBackfill; required for new operational writes.</summary>
    public Guid? CompanyId { get; set; }
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
    public DateTime? WpsStatusChangedAtUtc { get; set; }
    public string? WpsSubmissionReference { get; set; }
    public string? WpsRejectionReason { get; set; }

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
    // POD-B1 — the bank/Mudad-accepted disbursement has been SETTLED (money left the account and the
    // net-pay GL settlement journal posted, clearing 2100 Salaries Payable against Cash/Bank).
    public const string Paid       = "Paid";
    public const string Reconciled = "Reconciled";
    // POD-B3 — TERMINAL, void-only. The run this batch belongs to was voided, so the batch and its SIF
    // filing are withdrawn: no further WPS transition, no settlement, no reconciliation.
    //
    // DELIBERATELY NOT IN `All`. `All` is the set UpdateWpsStatus accepts from a caller, and a batch must
    // never be driven to Voided by hand — it is set by PayrollVoidService alongside the ledger unwind, in
    // the same transaction, so status and GL can never disagree. WpsTransitions likewise has no edge to
    // or from it: the ONLY writer is the void.
    public const string Voided     = "Voided";

    public static readonly string[] All =
        { Draft, Generated, Downloaded, Submitted, Accepted, Rejected, Paid, Reconciled };

    /// <summary>
    /// POD-B3 — has the bank/Mudad been given a file for this batch? Drives the void's "did money actually
    /// move?" question and the Replacement run's duplicate-SIF probe against its voided parent.
    /// </summary>
    public static bool IsFiled(string? status) =>
        status is Submitted or Accepted or Paid or Reconciled;
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
            // POD-B1 — Accepted (bank/Mudad instructed) settles to Paid (net-pay GL cleared). The
            // legacy Accepted→Reconciled edge is retained (WpsTests asserts it), but at runtime
            // UpdateWpsStatus gates any →Reconciled on the net-pay settlement GL existing so a batch can
            // never reach a terminal state with 2100 still open (see PayrollController.UpdateWpsStatus).
            [WpsStatuses.Accepted]   = new[] { WpsStatuses.Reconciled, WpsStatuses.Paid },
            [WpsStatuses.Paid]       = new[] { WpsStatuses.Reconciled },
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

public class PayrollOpeningBalance : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeCode { get; set; } = string.Empty;
    public int Year { get; set; }
    public string BalanceType { get; set; } = string.Empty;
    public string ComponentCode { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "AED";
    public string SourceSystem { get; set; } = string.Empty;
    public string SourceRecordId { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
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

public class WPSFileBatch : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Legal-entity scope. Backfilled by CompanyScopeBackfill; required for new operational writes.</summary>
    public Guid? CompanyId { get; set; }
    public Guid PaymentBatchId { get; set; }
    public string SifFileName { get; set; } = string.Empty;
    public string Status { get; set; } = "Generated";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string FilingStatus { get; set; } = WpsStatuses.Generated;
    public Guid? ResubmissionOfWpsFileBatchId { get; set; }
    public int ResubmissionNumber { get; set; }
    public string? SubmissionReference { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
    public DateTime? AcknowledgedAtUtc { get; set; }
    public DateTime? RejectedAtUtc { get; set; }
    public string? RejectionReason { get; set; }

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

    // ── Tamper-evidence (POD-A3) — mirrors AuditLog's per-tenant hash chain ──────────
    // Monotonic per-tenant ordinal assigned by the SaveChanges sealer under an advisory
    // lock (see ZayraDbContext.SealPayrollAuditChain). It is the authoritative ordering key
    // (timestamps can tie to the same tick) AND is bound into EntryHash, so any reordering
    // is cryptographically detectable. Legacy rows default to 0 until the boot backfill
    // assigns real ordinals.
    public long Seq { get; set; }
    // EntryHash of the immediately-preceding chain row for this tenant ("" for genesis).
    public string PreviousHash { get; set; } = string.Empty;
    // SHA-256 over the canonical field set (incl. Seq + PreviousHash). "" == unsealed row.
    public string EntryHash { get; set; } = string.Empty;
    public string HashAlgorithm { get; set; } = "SHA-256";
}
