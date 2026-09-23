using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Is the single authoritative record of whether something was approved, carrying the frozen workflow it runs against, the current step and the current approver the inbox filters on. @tier:T @owner:HR @retention:84m-keep
/// </summary>
public partial class ApprovalRequest
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid WorkflowId { get; set; }

    public Guid RequesterUserId { get; set; }

    public Guid? EmployeeId { get; set; }

    public string RequestType { get; set; } = null!;

    public string SubjectType { get; set; } = null!;

    public Guid SubjectId { get; set; }

    public string Status { get; set; } = null!;

    public int CurrentStep { get; set; }

    public Guid? CurrentApproverUserId { get; set; }

    public Guid? CurrentApproverEmployeeId { get; set; }

    public string? Payload { get; set; }

    public string WorkflowSnapshot { get; set; } = null!;

    public DateTime? DueAt { get; set; }

    public DateTime? SubmittedAt { get; set; }

    public DateTime? DecidedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual ICollection<ApprovalAction> ApprovalActions { get; set; } = new List<ApprovalAction>();

    public virtual ApprovalWorkflow ApprovalWorkflow { get; set; } = null!;

    public virtual Employee? Employee { get; set; }

    public virtual ICollection<EmployeeAssignment> EmployeeAssignments { get; set; } = new List<EmployeeAssignment>();

    public virtual ICollection<EmployeeSalary> EmployeeSalaries { get; set; } = new List<EmployeeSalary>();

    public virtual ICollection<FinalSettlement> FinalSettlements { get; set; } = new List<FinalSettlement>();

    public virtual ICollection<LeaveRequest> LeaveRequests { get; set; } = new List<LeaveRequest>();

    public virtual ICollection<Loan> Loans { get; set; } = new List<Loan>();

    public virtual ICollection<OvertimeRequest> OvertimeRequests { get; set; } = new List<OvertimeRequest>();

    public virtual ICollection<PayrollRun> PayrollRuns { get; set; } = new List<PayrollRun>();

    public virtual ICollection<Timesheet> Timesheets { get; set; } = new List<Timesheet>();

    public virtual User User { get; set; } = null!;
}
