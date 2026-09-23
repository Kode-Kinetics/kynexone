using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// The person: current identity, statutory identifiers, employment anchors and the PDPL lifecycle that lets an erasure anonymise the record in place while every dependent payroll row keeps its foreign key. @tier:T @owner:HR @retention:84-months-from-Separation-then-Anonymise
/// </summary>
public partial class Employee
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>
    /// Allocated from number_sequences. Retained through anonymisation so statutory payroll rows stay traceable (§12.3).
    /// </summary>
    public string EmployeeNumber { get; set; } = null!;

    /// <summary>
    /// Current-state PROJECTION maintained by EmployeeLifecycleService (§10.7). employee_assignments is authoritative for &quot;was this person employed on date D&quot; (§11.3).
    /// </summary>
    public string Status { get; set; } = null!;

    public string PrivacyStatus { get; set; } = null!;

    public string NameEn { get; set; } = null!;

    public string? NameAr { get; set; }

    /// <summary>
    /// Added to satisfy §19.4 H1, which indexes it; §2.D does not list it. See the note above this comment.
    /// </summary>
    public string? WorkEmail { get; set; }

    public string? Gender { get; set; }

    public DateOnly? Dob { get; set; }

    public string? NationalityCode { get; set; }

    public string? NationalId { get; set; }

    public string? IqamaNo { get; set; }

    public string? BorderNo { get; set; }

    public DateOnly? JoiningDate { get; set; }

    /// <summary>
    /// Drives the GOSI cohort (Legacy vs Entrant2024). NULL raises a BLOCKING payroll_issues row; the code never defaults to a cohort (§2.E).
    /// </summary>
    public DateOnly? GosiFirstRegisteredOn { get; set; }

    public bool WpsEligible { get; set; }

    public decimal? NitaqatWeightOverride { get; set; }

    public string? NitaqatWeightOverrideReason { get; set; }

    public DateOnly? EosServiceStartDate { get; set; }

    public decimal? EosPriorPaidAmount { get; set; }

    /// <summary>
    /// Projection of final_settlements.last_working_day, written when the settlement is approved; the settlement is authoritative (§11.3).
    /// </summary>
    public DateOnly? SeparationDate { get; set; }

    public DateTime? DeletedAt { get; set; }

    public DateOnly? RetentionUntil { get; set; }

    public DateTime? RedactedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual ICollection<ApprovalRequest> ApprovalRequests { get; set; } = new List<ApprovalRequest>();

    public virtual ICollection<EmployeeAssignment> EmployeeAssignmentEmployeeNavigations { get; set; } = new List<EmployeeAssignment>();

    public virtual ICollection<EmployeeAssignment> EmployeeAssignmentEmployees { get; set; } = new List<EmployeeAssignment>();

    public virtual ICollection<EmployeeBankAccount> EmployeeBankAccounts { get; set; } = new List<EmployeeBankAccount>();

    public virtual ICollection<EmployeeContract> EmployeeContracts { get; set; } = new List<EmployeeContract>();

    public virtual ICollection<EmployeeDocument> EmployeeDocuments { get; set; } = new List<EmployeeDocument>();

    public virtual ICollection<EmployeeGosiRegistration> EmployeeGosiRegistrations { get; set; } = new List<EmployeeGosiRegistration>();

    public virtual ICollection<EmployeeSalary> EmployeeSalaries { get; set; } = new List<EmployeeSalary>();

    public virtual ICollection<EosCalculation> EosCalculations { get; set; } = new List<EosCalculation>();

    public virtual ICollection<FinalSettlement> FinalSettlements { get; set; } = new List<FinalSettlement>();

    public virtual ICollection<LeaveLedger> LeaveLedgers { get; set; } = new List<LeaveLedger>();

    public virtual ICollection<LeaveRequest> LeaveRequests { get; set; } = new List<LeaveRequest>();

    public virtual ICollection<Loan> Loans { get; set; } = new List<Loan>();

    public virtual ICollection<OvertimeRequest> OvertimeRequests { get; set; } = new List<OvertimeRequest>();

    public virtual ICollection<PayrollInput> PayrollInputs { get; set; } = new List<PayrollInput>();

    public virtual ICollection<PayrollIssue> PayrollIssues { get; set; } = new List<PayrollIssue>();

    public virtual ICollection<PayrollSlip> PayrollSlips { get; set; } = new List<PayrollSlip>();

    public virtual ICollection<ShiftAssignment> ShiftAssignments { get; set; } = new List<ShiftAssignment>();

    public virtual Tenant Tenant { get; set; } = null!;

    public virtual ICollection<Timesheet> Timesheets { get; set; } = new List<Timesheet>();

    public virtual User? User { get; set; }

    public virtual ICollection<WpsLine> WpsLines { get; set; } = new List<WpsLine>();
}
