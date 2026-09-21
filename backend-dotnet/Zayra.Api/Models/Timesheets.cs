using Microsoft.EntityFrameworkCore;
using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

// ── Timesheets ───────────────────────────────────────────────────────────────────────────────
//
// An employee records minutes against a day and (optionally) a cost centre, submits the week,
// and a manager approves or rejects it through the ONE approval engine (ApprovalRequest /
// ApprovalWorkflowService, EntityName = "Timesheet"). There is no second approval mechanism here.
//
// The hours are not a filing cabinet. They are consumed by attendance reconciliation:
//   • at SUBMIT, a day whose logged minutes exceed the attendance record for that day by more
//     than the tolerance REFUSES the submission (TimesheetService.SubmitAsync);
//   • at APPROVE, the per-day reconciliation is persisted as TimesheetDayReconciliation rows,
//     which are what the HR attendance-variance report reads.
//
// The draft this module was harvested from (feat/w2g-timesheets) invented a Project /
// ProjectTask / ProjectAssignment module. KynexOne has no projects master, and inventing one was
// out of scope, so the dimension here is CostCenter — which already exists, already has a
// controller, and is already what Employee.CostCenterId points at.

/// <summary>
/// One employee's time for one weekly period. Exactly one per (tenant, employee, period start).
///
/// <para><b>Lifecycle.</b> Draft → Submitted → Approved | Rejected. A Rejected timesheet is
/// editable again and can be resubmitted; each submit starts a fresh
/// <see cref="ApprovalRequest"/>. Approved is terminal and read-only.</para>
///
/// <para><b>The approval row is the source of truth for the decision.</b> <see cref="Status"/> is
/// projected from it by <c>TimesheetApprovalSync</c>, which runs inside
/// <c>ApprovalWorkflowService.DecideAsync</c>'s single SaveChanges — so a decision taken in the
/// Approval Center and a decision taken on the timesheet screen are the same write.</para>
/// </summary>
public class Timesheet : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Legal-entity scope, stamped from the employee. Operational rows are never null-company.</summary>
    public Guid? CompanyId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public string Status { get; set; } = TimesheetStatuses.Draft;
    /// <summary>Denormalised sum of the entries; the service rewrites it on every save.</summary>
    public int TotalMinutes { get; set; }

    public Guid? ApprovalRequestId { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
    public Guid? SubmittedByUserId { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    /// <summary>The approver's comments, copied from the approval decision on reject or approve.</summary>
    public string? DecisionComments { get; set; }

    /// <summary>Optimistic concurrency token — EF advances it on every UPDATE.</summary>
    public int Version { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }

    public List<TimesheetEntry> Entries { get; set; } = new();
}

/// <summary>Minutes on one day, optionally attributed to a cost centre.</summary>
public class TimesheetEntry : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid TimesheetId { get; set; }
    /// <summary>Denormalised from the timesheet so the reports group without a join.</summary>
    public int EmployeeId { get; set; }
    public DateOnly WorkDate { get; set; }
    /// <summary>Optional <see cref="CostCenter"/>. Null means "not attributed".</summary>
    public Guid? CostCenterId { get; set; }
    public int Minutes { get; set; }
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public Timesheet? Timesheet { get; set; }
}

/// <summary>
/// The consumer's output: what an APPROVED timesheet's hours mean against the attendance the
/// devices recorded, one row per day of the period.
///
/// <para>These rows exist only for approved timesheets. They are written by
/// <c>TimesheetApprovalSync</c> in the same transaction as the approval decision, and they are
/// the sole backing store of the HR attendance-variance report. Nothing recomputes them on read —
/// a recomputed "report" is the read step this codebase keeps skipping, and a persisted row is
/// something a later payroll or audit question can be asked of.</para>
/// </summary>
public class TimesheetDayReconciliation : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid TimesheetId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public DateOnly WorkDate { get; set; }
    public int LoggedMinutes { get; set; }
    /// <summary>
    /// <see cref="AttendanceDailyRecord.TotalWorkedMinutes"/> for the day, or null when the
    /// employee has no attendance record at all (remote/field staff off device coverage).
    /// </summary>
    public int? AttendanceMinutes { get; set; }
    /// <summary>logged − attendance. Null exactly when <see cref="AttendanceMinutes"/> is null.</summary>
    public int? VarianceMinutes { get; set; }
    /// <summary>AttendanceDailyRecord.Status, or "NoRecord".</summary>
    public string AttendanceStatus { get; set; } = TimesheetReconciliationStatuses.NoRecord;
    /// <summary>logged &gt; attendance + tolerance. Always false when there is no record.</summary>
    public bool IsOverAllocated { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public static class TimesheetStatuses
{
    public const string Draft = "Draft";
    public const string Submitted = "Submitted";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";

    public static readonly string[] All = { Draft, Submitted, Approved, Rejected };

    /// <summary>The employee may change entries and submit.</summary>
    public static bool IsEditable(string status) => status is Draft or Rejected;
}

public static class TimesheetReconciliationStatuses
{
    /// <summary>The employee has no AttendanceDailyRecord for that day.</summary>
    public const string NoRecord = "NoRecord";
}

public static class TimesheetConstants
{
    /// <summary><c>ApprovalRequest.EntityName</c> / <c>ApprovalWorkflow.EntityName</c>.</summary>
    public const string ApprovalEntityName = "Timesheet";

    /// <summary>
    /// Periods are calendar weeks starting Sunday — the GCC working week this product is sold
    /// into. Deliberately a constant rather than a tenant setting: a per-tenant week-start is a
    /// whole settings surface, and getting it wrong silently re-buckets historical periods.
    /// </summary>
    public const DayOfWeek WeekStartDay = DayOfWeek.Sunday;

    /// <summary>
    /// How far a day's logged minutes may exceed the attendance record before submission is
    /// refused. Sixty minutes absorbs the ordinary break/rounding gap between a punch pair and a
    /// person's own recollection; beyond that the two records genuinely disagree.
    /// </summary>
    public const int OverAllocationToleranceMinutes = 60;

    public const int MaxMinutesPerDay = 24 * 60;
}

/// <summary>Weekly period arithmetic. Pure; no database, no clock beyond what is passed in.</summary>
public static class TimesheetPeriod
{
    /// <summary>The Sunday on or before <paramref name="date"/>.</summary>
    public static DateOnly StartFor(DateOnly date)
    {
        var delta = ((int)date.DayOfWeek - (int)TimesheetConstants.WeekStartDay + 7) % 7;
        return date.AddDays(-delta);
    }

    public static DateOnly EndFor(DateOnly date) => StartFor(date).AddDays(6);

    public static bool Contains(DateOnly periodStart, DateOnly date)
        => date >= periodStart && date <= periodStart.AddDays(6);

    public static IEnumerable<DateOnly> Days(DateOnly periodStart)
    {
        for (var i = 0; i < 7; i++) yield return periodStart.AddDays(i);
    }
}

/// <summary>
/// Fluent configuration for the timesheet tables. Lives here rather than inline in
/// <c>OnModelCreating</c> so that 4,000-line file stays append-only for this module.
/// Tenant isolation and the ICompanyScopedOperational filter come from the marker interfaces via
/// <c>ApplyTenantQueryFilters</c>; the <c>(tenant_id, company_id)</c> index comes from
/// <c>ApplyCompanyScopeIndexes</c>. Nothing about either is repeated here.
/// </summary>
internal static class TimesheetModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Timesheet>(entity =>
        {
            entity.ToTable("timesheets");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EmployeeName).HasMaxLength(200);
            entity.Property(x => x.Status).HasMaxLength(20).IsRequired();
            entity.Property(x => x.DecisionComments).HasMaxLength(1000);
            // Optimistic concurrency: two tabs editing the same week cannot both win silently.
            entity.Property(x => x.Version).IsConcurrencyToken();

            // One timesheet per person per week. This is the invariant the whole module rests on;
            // enforcing it only in code would make a double-submit race produce two live weeks.
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.PeriodStart })
                .IsUnique()
                .HasDatabaseName("ux_timesheets_tenant_employee_period");
            // The approver worklist: "everything Submitted, oldest period first".
            entity.HasIndex(x => new { x.TenantId, x.Status, x.PeriodStart })
                .HasDatabaseName("ix_timesheets_tenant_status_period");
            // Resolving a timesheet from an Approval Center decision.
            entity.HasIndex(x => new { x.TenantId, x.ApprovalRequestId })
                .HasDatabaseName("ix_timesheets_tenant_approval_request");

            entity.HasMany(x => x.Entries)
                .WithOne(x => x.Timesheet!)
                .HasForeignKey(x => x.TimesheetId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TimesheetEntry>(entity =>
        {
            entity.ToTable("timesheet_entries");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Notes).HasMaxLength(500);
            entity.HasIndex(x => new { x.TenantId, x.TimesheetId, x.WorkDate })
                .HasDatabaseName("ix_timesheet_entries_tenant_sheet_date");
            // Cost-centre hours over a date range, without touching the header table.
            entity.HasIndex(x => new { x.TenantId, x.CostCenterId, x.WorkDate })
                .HasDatabaseName("ix_timesheet_entries_tenant_cost_centre_date");
        });

        modelBuilder.Entity<TimesheetDayReconciliation>(entity =>
        {
            entity.ToTable("timesheet_day_reconciliations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EmployeeName).HasMaxLength(200);
            entity.Property(x => x.AttendanceStatus).HasMaxLength(40).IsRequired();
            // One row per approved timesheet-day. Unique so a replayed approval cannot double the
            // variance HR is looking at.
            entity.HasIndex(x => new { x.TenantId, x.TimesheetId, x.WorkDate })
                .IsUnique()
                .HasDatabaseName("ux_timesheet_day_reconciliations_sheet_date");
            // Backs the variance report: a date window, optionally narrowed to one person.
            entity.HasIndex(x => new { x.TenantId, x.WorkDate, x.EmployeeId })
                .HasDatabaseName("ix_timesheet_day_reconciliations_tenant_date_employee");
        });
    }
}
