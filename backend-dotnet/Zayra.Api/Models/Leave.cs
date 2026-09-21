using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

public class LeaveType : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool IsPaid { get; set; } = true;
    public bool IsHalfDayAllowed { get; set; }
    public bool IsHourlyAllowed { get; set; }
    public bool RequiresAttachment { get; set; }
    public bool RequiresReason { get; set; }
    public int MaxConsecutiveDays { get; set; }
    public string ColorCode { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class LeavePolicy : ITenantOwned, ICompanyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid LeaveTypeId { get; set; }
    public string CountryCode { get; set; } = string.Empty;
    public Guid? CompanyId { get; set; }
    public Guid? BranchId { get; set; }
    public string DepartmentName { get; set; } = string.Empty;
    public string Grade { get; set; } = string.Empty;
    public string EmploymentType { get; set; } = string.Empty;
    public string ContractType { get; set; } = string.Empty;
    public string Gender { get; set; } = string.Empty;
    public bool AppliesOnProbation { get; set; }
    public decimal AnnualEntitlementDays { get; set; }
    public string AccrualMethod { get; set; } = "Yearly";
    public decimal CarryForwardMax { get; set; }
    public int CarryForwardExpiry { get; set; }
    public bool EncashmentAllowed { get; set; }
    public decimal EncashmentMaxDays { get; set; }
    public decimal MinimumDaysPerRequest { get; set; } = 1;
    public decimal MaximumDaysPerRequest { get; set; }
    public int NoticeRequiredDays { get; set; }
    public bool WeekendsIncluded { get; set; }
    public bool PublicHolidaysIncluded { get; set; }
    public string PayrollImpact { get; set; } = "Full";
    public Guid? ApprovalWorkflowId { get; set; }
    public string Status { get; set; } = "Draft";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class EmployeeLeaveBalance : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public Guid LeaveTypeId { get; set; }
    public string LeaveTypeName { get; set; } = string.Empty;
    public int Year { get; set; }
    public decimal Entitled { get; set; }
    public decimal Accrued { get; set; }
    public decimal Used { get; set; }
    public decimal Pending { get; set; }
    public decimal CarriedForward { get; set; }
    public decimal Encashed { get; set; }
    public decimal Expired { get; set; }
    public decimal ManualAdjustment { get; set; }
    public bool NegativeAllowed { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The days GRANTED to this employee for this leave year — the single "you have been given this
    /// much" term that <see cref="Available"/> is built on.
    ///
    /// <para><b><see cref="Entitled"/> and <see cref="Accrued"/> are two representations of the SAME
    /// grant, not two grants.</b> A front-loaded policy (<c>LeavePolicy.AccrualMethod == "Yearly"</c>)
    /// puts the whole year's allocation in <see cref="Entitled"/> and leaves <see cref="Accrued"/> at
    /// zero. A monthly-accrual policy grows <see cref="Accrued"/> month by month
    /// (<c>LeaveService.AccrueMonthlyAsync</c>, the only writer of it in production) and leaves
    /// <see cref="Entitled"/> at zero. <see cref="Available"/> used to ADD them, so any row carrying
    /// both — which is every demo/pilot tenant and every CSV-imported tenant, because
    /// <c>MigrationImportController</c>'s legacy-balance template makes both columns mandatory —
    /// counted the same entitlement twice. Evostel's annual-leave row (Entitled 30, Accrued 17.50,
    /// Used 10) showed 37.50 days available against a 30-day entitlement.</para>
    ///
    /// <para><b>Why MAX and not one field or the other.</b> Picking a single field would be a silent
    /// data migration: a tenant whose figure lives in <see cref="Entitled"/> would be zeroed by an
    /// accrued-only reading, and vice versa. MAX is a no-op for every row that populates exactly one of
    /// them — it changes nothing for the coherent shapes — and for a row carrying both it recognises the
    /// larger of the two representations, which is the employee-favourable non-double-counting answer
    /// (it is by construction &gt;= either field alone). It also needs no migration and no backfill of
    /// live data.</para>
    ///
    /// <para>[FLAG-COMPLIANCE-KSA] Art. 109 sets the QUANTUM of annual leave (21 days, 30 from five
    /// years of continuous service) and the tiering is applied by <c>KsaAnnualLeaveScale</c> into
    /// <see cref="Accrued"/> at 21/12 = 1.75 or 30/12 = 2.5 days a month. Art. 109 does NOT say whether
    /// that quantum is front-loaded at the start of the leave year or earned month by month — that is a
    /// policy choice, and it is the one <c>LeavePolicy.AccrualMethod</c> records. On a row that carries
    /// both fields MAX front-loads, so mid-year the tiering is not visible in the balance until
    /// <see cref="Accrued"/> overtakes <see cref="Entitled"/>. See the counsel question in
    /// scratchpad/leave-attendance-figures.md: whether an accrual-method tenant's balance should show
    /// the earned-to-date figure instead is a product decision, not an arithmetic one.</para>
    /// </summary>
    public decimal Granted => Math.Max(Entitled, Accrued);

    /// <summary>
    /// THE definition of a leave balance. Every screen, report, sufficiency check and settlement must
    /// read this property rather than re-spell the expression — the re-spelt copies had already drifted
    /// into four different answers for the same employee.
    /// </summary>
    public decimal Available =>
        Granted + CarriedForward + ManualAdjustment - Used - Pending - Encashed - Expired;
}

public class LeaveBalanceTransaction : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Legal-entity scope. Backfilled by CompanyScopeBackfill; required for new operational writes.</summary>
    public Guid? CompanyId { get; set; }
    public int EmployeeId { get; set; }
    public Guid LeaveTypeId { get; set; }
    public int Year { get; set; }
    public string TransactionType { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public decimal BalanceBefore { get; set; }
    public decimal BalanceAfter { get; set; }
    public string Reference { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string PerformedByName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class LeaveRequest : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Legal-entity scope. Backfilled by CompanyScopeBackfill; required for new operational writes.</summary>
    public Guid? CompanyId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string DepartmentName { get; set; } = string.Empty;
    public string DesignationTitle { get; set; } = string.Empty;
    public Guid LeaveTypeId { get; set; }
    public string LeaveTypeName { get; set; } = string.Empty;
    public Guid? PolicyId { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public decimal TotalDays { get; set; }
    public string DayType { get; set; } = "Full";
    public decimal HoursRequested { get; set; }
    public string Reason { get; set; } = string.Empty;
    public bool IsEmergency { get; set; }
    public string AttachmentPath { get; set; } = string.Empty;
    public string PayrollImpact { get; set; } = string.Empty;
    public string Status { get; set; } = "Draft";
    public string ManagerApprovalNotes { get; set; } = string.Empty;
    public string HRApprovalNotes { get; set; } = string.Empty;
    public string RejectionReason { get; set; } = string.Empty;
    public string CancellationReason { get; set; } = string.Empty;
    public DateOnly? ReturnDate { get; set; }
    public int? DelegateEmployeeId { get; set; }
    public string DelegateEmployeeName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SubmittedAtUtc { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
}

public class LeaveApproval : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid LeaveRequestId { get; set; }
    public int StepNumber { get; set; }
    public string ApproverRole { get; set; } = string.Empty;
    public Guid? ApproverId { get; set; }
    public string ApproverName { get; set; } = string.Empty;
    public string Decision { get; set; } = "Pending";
    public string Notes { get; set; } = string.Empty;
    public DateTime? ActedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class LeaveCancellationRequest : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid LeaveRequestId { get; set; }
    public int EmployeeId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public string ReviewedByName { get; set; } = string.Empty;
    public string ReviewNotes { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ReviewedAtUtc { get; set; }
}

public class LeaveModificationRequest : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid LeaveRequestId { get; set; }
    public int EmployeeId { get; set; }
    public DateOnly NewStartDate { get; set; }
    public DateOnly NewEndDate { get; set; }
    public decimal NewTotalDays { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public string ReviewedByName { get; set; } = string.Empty;
    public string ReviewNotes { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ReviewedAtUtc { get; set; }
}

public class PublicHolidayCalendar : ITenantOwned, ICompanyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string CountryCode { get; set; } = string.Empty;
    public Guid? CompanyId { get; set; }
    public Guid? BranchId { get; set; }
    public int CalendarYear { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class PublicHoliday : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid CalendarId { get; set; }
    public string NameEn { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public string HijriDate { get; set; } = string.Empty;
    public bool IsRecurring { get; set; }
    public bool IsOptional { get; set; }
    public string HolidayType { get; set; } = "National";
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class LeaveBlackoutDate : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string NameEn { get; set; } = string.Empty;
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string DepartmentName { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public bool IsCompanyWide { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class LeaveEncashmentRequest : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public Guid LeaveTypeId { get; set; }
    public string LeaveTypeName { get; set; } = string.Empty;
    public int Year { get; set; }
    public decimal DaysToEncash { get; set; }
    public decimal AmountPerDay { get; set; }
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public string HRNotes { get; set; } = string.Empty;
    public string PayrollNotes { get; set; } = string.Empty;
    /// <summary>The payroll run explicitly selected by the payroll approver.</summary>
    public Guid? PayrollRunId { get; set; }
    /// <summary>The normal payroll adjustment consumed by the selected run.</summary>
    public Guid? PayrollAdjustmentId { get; set; }
    public int DecisionVersion { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAtUtc { get; set; }
    public DateTime? VoidedAtUtc { get; set; }
    public Guid? VoidedByUserId { get; set; }
    public string VoidReason { get; set; } = string.Empty;
}

public static class LeaveEncashmentStatuses
{
    public const string Pending = "Pending";
    public const string HRApproved = "HRApproved";
    public const string PayrollApproved = "PayrollApproved";
    public const string Processed = "Processed";
    public const string Rejected = "Rejected";
    public const string Voided = "Voided";
}

public class CompOffCredit : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    /// <summary>
    /// Source conversion when this credit was generated from approved overtime. Null for
    /// manually-entered historical credits.
    /// </summary>
    public Guid? OvertimeCompOffConversionId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public DateOnly WorkedDate { get; set; }
    public string WorkType { get; set; } = "Overtime";
    public decimal HoursWorked { get; set; }
    public decimal DaysEarned { get; set; }
    public DateOnly? ExpiryDate { get; set; }
    public string Status { get; set; } = "Pending";
    /// <summary>Optimistic version protecting the remaining usable days from concurrent spends.</summary>
    public int UsageVersion { get; set; }
    public string ManagerApprovalNotes { get; set; } = string.Empty;
    public string ApprovedByName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ApprovedAtUtc { get; set; }
}

public class CompOffUsage : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    public Guid CompOffCreditId { get; set; }
    public Guid? LeaveRequestId { get; set; }
    /// <summary>Caller-supplied replay key for a use command; null on legacy/manual usages.</summary>
    public Guid? IdempotencyKey { get; set; }
    public decimal DaysUsed { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class AbsenceRecord : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string DepartmentName { get; set; } = string.Empty;
    public DateOnly AbsenceDate { get; set; }
    public string AbsenceType { get; set; } = "Unauthorized";
    public bool IsRegularized { get; set; }
    public string PayrollImpact { get; set; } = string.Empty;
    public Guid? RegularizationRequestId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class AbsenceRegularizationRequest : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public Guid AbsenceRecordId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public Guid? LeaveTypeId { get; set; }
    public string Status { get; set; } = "Pending";
    public string ManagerNotes { get; set; } = string.Empty;
    public string HRNotes { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ReviewedAtUtc { get; set; }
}

public class LeaveDelegation : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public int DelegateEmployeeId { get; set; }
    public string DelegateEmployeeName { get; set; } = string.Empty;
    public Guid? LeaveRequestId { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string DelegationType { get; set; } = "ApprovalOnly";
    public string Notes { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class LeavePayrollImpact : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid LeaveRequestId { get; set; }
    public int EmployeeId { get; set; }
    public string PayPeriod { get; set; } = string.Empty;
    public string ImpactType { get; set; } = "Deduction";
    public decimal Days { get; set; }
    public decimal Amount { get; set; }
    public string Status { get; set; } = "Pending";
    public DateTime? ProcessedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class LeaveAuditLog : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string OldValue { get; set; } = string.Empty;
    public string NewValue { get; set; } = string.Empty;
    public string PerformedByName { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class LeaveAIInsight : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string InsightType { get; set; } = string.Empty;
    public string Severity { get; set; } = "Info";
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public int? AffectedEmployeeId { get; set; }
    public string AffectedDepartment { get; set; } = string.Empty;
    public string Data { get; set; } = string.Empty;
    public bool IsAcknowledged { get; set; }
    public string AcknowledgedByName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
