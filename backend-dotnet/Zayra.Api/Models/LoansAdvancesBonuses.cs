using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

// ── Loan Module ───────────────────────────────────────────────────────────────

public class LoanType : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Code { get; set; } = string.Empty;        // Personal, Emergency, Housing, Vehicle, Education
    public string NameEn { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public decimal MaxAmount { get; set; }
    public int MaxInstallments { get; set; } = 12;
    public string RepaymentFrequency { get; set; } = "Monthly";
    public bool IsInterestFree { get; set; } = true;
    public decimal InterestRate { get; set; }               // 0 if interest-free
    public int MinServiceMonths { get; set; }               // eligibility: min months of service
    public bool RequiresApproval { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public bool IsDeleted { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
}

public class LoanPolicy : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid LoanTypeId { get; set; }
    public string PolicyName { get; set; } = string.Empty;
    public int MaxConcurrentLoans { get; set; } = 1;
    public decimal MaxMultiplierOfSalary { get; set; }      // e.g. 3 = max 3x monthly salary
    public int CooldownMonthsAfterRepayment { get; set; }
    public bool AllowEarlySettlement { get; set; } = true;
    public bool AllowRescheduling { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
}

public class EmployeeLoan : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Legal-entity scope. Backfilled by CompanyScopeBackfill; required for new operational writes.</summary>
    public Guid? CompanyId { get; set; }
    public Guid EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    /// <summary>Int FK to Employee.Id — enables payroll run to deduct EMI without Guid/int mismatch.</summary>
    public int? EmployeeIntId { get; set; }
    public Guid LoanTypeId { get; set; }
    public string LoanTypeName { get; set; } = string.Empty;
    public string LoanNumber { get; set; } = string.Empty;  // auto-generated e.g. LN-2026-00001
    public decimal RequestedAmount { get; set; }
    public decimal ApprovedAmount { get; set; }
    public int RequestedInstallments { get; set; }
    public int ApprovedInstallments { get; set; }
    public decimal InstallmentAmount { get; set; }
    public string RepaymentFrequency { get; set; } = "Monthly";
    public DateOnly? DisbursementDate { get; set; }
    public DateOnly? RepaymentStartDate { get; set; }
    public decimal TotalRepaid { get; set; }
    public decimal OutstandingBalance { get; set; }
    public string Status { get; set; } = "Pending";         // Pending, Approved, Rejected, Active, Settled, Cancelled, Overdue
    public string? RejectionReason { get; set; }
    public string Notes { get; set; } = string.Empty;
    public bool IsLockedByPayroll { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
}

public class LoanApproval : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid LoanId { get; set; }
    public int StepOrder { get; set; }
    public string ApproverRole { get; set; } = string.Empty; // Manager, HR, Finance, Admin
    public Guid? ApprovedBy { get; set; }
    public string ApprovedByName { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";         // Pending, Approved, Rejected
    public string Comments { get; set; } = string.Empty;
    public DateTime? DecidedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class LoanInstallment : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid LoanId { get; set; }
    public int InstallmentNumber { get; set; }
    public DateOnly DueDate { get; set; }
    public decimal AmountDue { get; set; }
    public decimal AmountPaid { get; set; }
    public string Status { get; set; } = "Pending";         // Pending, Paid, Overdue, Waived
    public Guid? PayrollRunId { get; set; }
    public DateOnly? PaidDate { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class LoanSettlement : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid LoanId { get; set; }
    public string SettlementType { get; set; } = "Early";   // Early, Normal, Waiver
    public decimal SettlementAmount { get; set; }
    public DateOnly SettlementDate { get; set; }
    public string Notes { get; set; } = string.Empty;
    public Guid? ApprovedBy { get; set; }
    public string ApprovedByName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
}

public class LoanAuditLog : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid LoanId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string OldValuesJson { get; set; } = string.Empty;
    public string NewValuesJson { get; set; } = string.Empty;
    public Guid? PerformedBy { get; set; }
    public string PerformedByName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

// ── Salary Advance Module ─────────────────────────────────────────────────────

public class AdvancePolicy : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string PolicyName { get; set; } = string.Empty;
    public decimal MaxPercentageOfSalary { get; set; } = 50; // max 50% of monthly salary
    public int MaxAdvancesPerYear { get; set; } = 2;
    public int MinServiceMonths { get; set; } = 6;
    public bool AllowInstallments { get; set; } = true;
    public int MaxInstallments { get; set; } = 3;
    public int CooldownMonths { get; set; } = 3;
    public bool RequiresApproval { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
}

public class SalaryAdvance : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Legal-entity scope. Backfilled by CompanyScopeBackfill; required for new operational writes.</summary>
    public Guid? CompanyId { get; set; }
    public Guid EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    /// <summary>Int FK to Employee.Id — enables payroll run to deduct advance repayment.</summary>
    public int? EmployeeIntId { get; set; }
    public string AdvanceNumber { get; set; } = string.Empty; // e.g. ADV-2026-00001
    public decimal RequestedAmount { get; set; }
    public decimal ApprovedAmount { get; set; }
    public string RepaymentType { get; set; } = "OneTime";  // OneTime, Installments
    public int Installments { get; set; } = 1;
    public decimal InstallmentAmount { get; set; }
    public DateOnly? RepaymentStartDate { get; set; }
    public decimal TotalRepaid { get; set; }
    public decimal OutstandingBalance { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";         // Pending, Approved, Rejected, Active, Settled
    public string? RejectionReason { get; set; }
    public bool IsLockedByPayroll { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
}

public class AdvanceApproval : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid AdvanceId { get; set; }
    public int StepOrder { get; set; }
    public string ApproverRole { get; set; } = string.Empty;
    public Guid? ApprovedBy { get; set; }
    public string ApprovedByName { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public string Comments { get; set; } = string.Empty;
    public DateTime? DecidedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class AdvanceInstallment : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid AdvanceId { get; set; }
    public int InstallmentNumber { get; set; }
    public DateOnly DueDate { get; set; }
    public decimal AmountDue { get; set; }
    public decimal AmountPaid { get; set; }
    public string Status { get; set; } = "Pending";         // Pending, Paid, Overdue
    public Guid? PayrollRunId { get; set; }
    public DateOnly? PaidDate { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class AdvanceAuditLog : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid AdvanceId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string OldValuesJson { get; set; } = string.Empty;
    public string NewValuesJson { get; set; } = string.Empty;
    public Guid? PerformedBy { get; set; }
    public string PerformedByName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

// ── Bonus Module ──────────────────────────────────────────────────────────────

public class BonusType : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Code { get; set; } = string.Empty;        // Performance, Festival, Annual, Joining, Retention, Project
    public string NameEn { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public string CalculationMethod { get; set; } = "Fixed"; // Fixed, PercentageSalary, PerformanceBased, Custom
    public decimal DefaultCalculationValue { get; set; } = 0; // Fixed: AED amount; PercentageSalary: e.g. 10 = 10%; 0 = ad-hoc per batch
    // Eligibility
    public string Frequency { get; set; } = "OneTime"; // Annual, Quarterly, Monthly, OneTime, ProjectBased
    public int MinServiceMonths { get; set; } = 0;
    public bool ProRataEligibility { get; set; } = false; // pro-rate if joining date is mid-period
    public bool RequiresApproval { get; set; } = true;
    // Compliance flags
    public bool IsIncludedInEosb { get; set; } = false; // add bonus to EOSB/gratuity base salary
    public bool IsIncludedInGosiBase { get; set; } = false; // add bonus to GOSI/social insurance base (KSA/GCC)
    public bool IsIncludedInWps { get; set; } = true; // include in WPS SIF payment file
    // Tax treatment — region-aware
    public bool IsTaxable { get; set; }
    public string TaxRegion { get; set; } = "GCC"; // GCC, US, UK, EU, Custom
    public decimal TaxRate { get; set; } = 0; // used when TaxRegion = Custom or UK (override)
    public string Notes { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public bool IsDeleted { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
}

public class BonusBatch : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid BonusTypeId { get; set; }
    public string BonusTypeName { get; set; } = string.Empty;
    public string BatchNumber { get; set; } = string.Empty; // e.g. BON-2026-001
    public string BatchName { get; set; } = string.Empty;
    public string PaymentPeriod { get; set; } = string.Empty; // e.g. "2026-05"
    public DateOnly PaymentDate { get; set; }
    public decimal TotalAmount { get; set; }
    public int EmployeeCount { get; set; }
    public string Status { get; set; } = "Draft";           // Draft, PendingApproval, Approved, Paid, Cancelled
    public string Notes { get; set; } = string.Empty;
    public bool IsLockedByPayroll { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
}

public class EmployeeBonus : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Legal-entity scope. Backfilled by CompanyScopeBackfill; required for new operational writes.</summary>
    public Guid? CompanyId { get; set; }
    public Guid BonusBatchId { get; set; }
    public Guid EmployeeId { get; set; }       // Employee.PublicId (stable public/domain identifier)
    public int? EmployeeIntId { get; set; }    // Employee.Id (int PK) for payroll-run matching
    public string EmployeeName { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public Guid BonusTypeId { get; set; }
    public string BonusTypeName { get; set; } = string.Empty;
    public decimal BasicSalary { get; set; }
    public string CalculationMethod { get; set; } = "Fixed";
    public decimal CalculationValue { get; set; }           // fixed amount or percentage
    public decimal GrossBonusAmount { get; set; }
    public decimal TaxWithheld { get; set; }
    public decimal BonusAmount { get; set; }                 // net (after tax)
    public string TaxRegion { get; set; } = "GCC";
    public string PaymentPeriod { get; set; } = string.Empty;
    public string Status { get; set; } = "Draft";           // Draft, Approved, PaidInPayroll, Cancelled
    public string Notes { get; set; } = string.Empty;
    public Guid? PayrollRunId { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
}

public class BonusApproval : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid BonusBatchId { get; set; }
    public int StepOrder { get; set; }
    public string ApproverRole { get; set; } = string.Empty;
    public Guid? ApprovedBy { get; set; }
    public string ApprovedByName { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public string Comments { get; set; } = string.Empty;
    public DateTime? DecidedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class BonusAuditLog : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? BonusBatchId { get; set; }
    public Guid? EmployeeBonusId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string OldValuesJson { get; set; } = string.Empty;
    public string NewValuesJson { get; set; } = string.Empty;
    public Guid? PerformedBy { get; set; }
    public string PerformedByName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

// ── Finance GL Entry (audit-ready journal entries) ────────────────────────────

public class FinanceGlEntry : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>
    /// POD-B1b — legal-entity attribution for this journal line (per-company trial balance, per-company
    /// period close, and the exact accrual→clearing match the bonus payable needs when a batch spans
    /// companies). Deliberately a PLAIN DIMENSION and NOT <c>ICompanyScoped</c>: adding a global query
    /// filter here would hide every pre-B1b row (they are all NULL) from the GL reports the whole
    /// finance module reads. Null = unattributed / group-level (all historical rows).
    /// </summary>
    public Guid? CompanyId { get; set; }

    // Source reference
    public string SourceModule { get; set; } = string.Empty;  // Loan, Advance, Bonus
    public Guid SourceEntityId { get; set; }
    public string SourceEntityRef { get; set; } = string.Empty; // LN-2026-00001, ADV-…, BON-…
    public string EventType { get; set; } = string.Empty;       // Disbursement, Repayment, Settlement, BonusPayment, Reversal

    // Accounting fields (double-entry)
    public string DebitAccount { get; set; } = string.Empty;    // e.g. "1400 - Employee Loans Receivable"
    public string CreditAccount { get; set; } = string.Empty;   // e.g. "1000 - Cash/Bank"
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";

    // Period
    public DateOnly EntryDate { get; set; }
    public string Period { get; set; } = string.Empty;          // YYYY-MM

    // Audit
    public string Description { get; set; } = string.Empty;
    public string PostedByName { get; set; } = string.Empty;
    public Guid? PostedBy { get; set; }
    public bool IsReversed { get; set; }
    public Guid? ReversalOfEntryId { get; set; }
    public string ErpPostingStatus { get; set; } = ErpPostingStatuses.NotReady;
    public string? ErpDocumentNumber { get; set; }
    public DateTime? ErpStatusChangedAtUtc { get; set; }
    public string? ErpRejectionReason { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
