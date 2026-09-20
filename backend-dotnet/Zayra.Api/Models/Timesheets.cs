using Microsoft.EntityFrameworkCore;
using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

// W2-G — Timesheets and project allocation.
//
// A Project is a bucket of work an employee can log hours against; a ProjectTask is an optional
// finer grain under it. A ProjectAssignment is the permission to log: an employee can only log
// hours to projects they are assigned to. A Timesheet is one employee's hours for one period
// (week or month — a tenant setting), reconciled day by day against
// AttendanceDailyRecord.TotalWorkedMinutes and routed through the F1 approval router as
// EntityName = "Timesheet".

/// <summary>A client project or internal initiative hours are logged against. Configuration
/// entity: <c>CompanyId == null</c> means shared across the tenant's companies.</summary>
public class Project : ITenantOwned, ICompanyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>Free-text client/customer name. There is no customer master in KynexOne.</summary>
    public string ClientName { get; set; } = string.Empty;
    public Guid? CostCenterId { get; set; }
    /// <summary>Active | Closed. Hours cannot be logged to a Closed project.</summary>
    public string Status { get; set; } = ProjectStatuses.Active;
    public decimal? BudgetHours { get; set; }
    /// <summary>Default billable flag for entries; a task may override it.</summary>
    public bool IsBillable { get; set; } = true;
    public string Description { get; set; } = string.Empty;
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }

    public List<ProjectTask> Tasks { get; set; } = new();
    public List<ProjectAssignment> Assignments { get; set; } = new();
}

public class ProjectTask : ITenantOwned, ICompanyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>Optional MasterDataValue.Code under the tenant's <c>TimesheetTaskType</c> type.</summary>
    public string TaskTypeCode { get; set; } = string.Empty;
    public bool IsBillable { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }

    public Project? Project { get; set; }
}

/// <summary>The permission to log hours: employee ↔ project, with an optional allocation share.</summary>
public class ProjectAssignment : ITenantOwned, ICompanyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid ProjectId { get; set; }
    public int EmployeeId { get; set; }
    /// <summary>0–100. Informational (capacity planning); it does not cap what can be logged.</summary>
    public decimal? AllocationPercent { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }

    public Project? Project { get; set; }
}

/// <summary>
/// One employee's hours for one period. Exactly one per (tenant, employee, period start).
///
/// <para><b>Lifecycle.</b> Draft → Submitted → Approved | Rejected | SentBack → Locked.
/// Draft, SentBack and Rejected are editable and can be (re)submitted; every submit starts a fresh
/// approval request through the F1 router. Approved and Locked are read-only for the employee.
/// HR can reopen an Approved timesheet (→ SentBack) and can lock/reopen a Locked one, except that a
/// Locked timesheet whose period falls in a locked payroll run cannot be reopened.</para>
///
/// <para>The approval row is the source of truth for the decision; <see cref="Status"/> is a
/// projection of it that <c>TimesheetReconciler</c> keeps current (Approval Center decisions have
/// no hook into this module).</para>
/// </summary>
public class Timesheet : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    /// <summary>Weekly | Monthly — the tenant setting at creation time.</summary>
    public string PeriodType { get; set; } = TimesheetPeriodTypes.Weekly;
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public string Status { get; set; } = TimesheetStatuses.Draft;
    /// <summary>Denormalised sums of the entries, kept by the service on every save.</summary>
    public int TotalMinutes { get; set; }
    public int BillableMinutes { get; set; }

    public Guid? ApprovalRequestId { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
    public Guid? SubmittedByUserId { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    /// <summary>Approver comments on a rejection or send-back.</summary>
    public string? DecisionComments { get; set; }
    public DateTime? LockedAtUtc { get; set; }
    public Guid? LockedByUserId { get; set; }

    /// <summary>Optimistic concurrency token — every write advances it.</summary>
    public int Version { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }

    public List<TimesheetEntry> Entries { get; set; } = new();
}

/// <summary>Minutes on one day against one project (and optionally one task).</summary>
public class TimesheetEntry : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid TimesheetId { get; set; }
    /// <summary>Denormalised from the timesheet so the reports can group without a join.</summary>
    public int EmployeeId { get; set; }
    public DateOnly WorkDate { get; set; }
    public Guid ProjectId { get; set; }
    public Guid? ProjectTaskId { get; set; }
    public int Minutes { get; set; }
    public bool IsBillable { get; set; }
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }

    public Timesheet? Timesheet { get; set; }
}

public static class ProjectStatuses
{
    public const string Active = "Active";
    public const string Closed = "Closed";
}

public static class TimesheetPeriodTypes
{
    public const string Weekly = "Weekly";
    public const string Monthly = "Monthly";
}

public static class TimesheetStatuses
{
    public const string Draft = "Draft";
    public const string Submitted = "Submitted";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string SentBack = "SentBack";
    public const string Locked = "Locked";

    public static readonly string[] All = { Draft, Submitted, Approved, Rejected, SentBack, Locked };

    /// <summary>The employee may change entries and submit.</summary>
    public static bool IsEditable(string status) => status is Draft or SentBack or Rejected;

    /// <summary>Counts towards reports and the billing export.</summary>
    public static bool IsFinal(string status) => status is Approved or Locked;
}

public static class TimesheetConstants
{
    /// <summary>ApprovalRequest.EntityName / ApprovalWorkflow.EntityName.</summary>
    public const string ApprovalEntityName = "Timesheet";
    /// <summary>MasterDataType.Code for task types.</summary>
    public const string TaskTypeMasterType = "TimesheetTaskType";
    /// <summary>SystemSetting.Category for the tenant's timesheet settings.</summary>
    public const string SettingsCategory = "Timesheets";
    public const int MaxMinutesPerDay = 24 * 60;
}

internal static class TimesheetModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Project>(entity =>
        {
            entity.ToTable("projects");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(40);
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.ClientName).HasMaxLength(200);
            entity.Property(x => x.Status).HasMaxLength(20);
            entity.Property(x => x.Description).HasMaxLength(2000);
            entity.Property(x => x.BudgetHours).HasPrecision(10, 2);
            entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.Status });
            entity.HasIndex(x => new { x.TenantId, x.CostCenterId });
            entity.HasMany(x => x.Tasks).WithOne(x => x.Project!).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(x => x.Assignments).WithOne(x => x.Project!).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProjectTask>(entity =>
        {
            entity.ToTable("project_tasks");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.TaskTypeCode).HasMaxLength(80);
            entity.HasIndex(x => new { x.TenantId, x.ProjectId, x.IsActive });
        });

        modelBuilder.Entity<ProjectAssignment>(entity =>
        {
            entity.ToTable("project_assignments");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.AllocationPercent).HasPrecision(5, 2);
            entity.HasIndex(x => new { x.TenantId, x.ProjectId, x.EmployeeId }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.IsActive });
        });

        modelBuilder.Entity<Timesheet>(entity =>
        {
            entity.ToTable("timesheets");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EmployeeName).HasMaxLength(200);
            entity.Property(x => x.PeriodType).HasMaxLength(10);
            entity.Property(x => x.Status).HasMaxLength(20);
            entity.Property(x => x.DecisionComments).HasMaxLength(1000);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.PeriodStart }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.Status, x.PeriodStart });
            entity.HasIndex(x => new { x.TenantId, x.ApprovalRequestId });
            entity.HasMany(x => x.Entries).WithOne(x => x.Timesheet!).HasForeignKey(x => x.TimesheetId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TimesheetEntry>(entity =>
        {
            entity.ToTable("timesheet_entries");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Notes).HasMaxLength(500);
            entity.HasIndex(x => new { x.TenantId, x.TimesheetId, x.WorkDate });
            entity.HasIndex(x => new { x.TenantId, x.ProjectId, x.WorkDate });
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.WorkDate });
        });
    }
}
