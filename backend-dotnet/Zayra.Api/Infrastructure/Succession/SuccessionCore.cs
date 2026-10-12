using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Succession;

/// <summary>The 12 succession permission keys (SUC_DESIGN §4). Granted by explicit lists only (AuthSeeder).</summary>
public static class SuccessionPermissions
{
    public const string Read = "succession.read";
    public const string PositionsManage = "succession.positions.manage";
    public const string PoolsManage = "succession.pools.manage";
    public const string Nominate = "succession.nominate";
    public const string Assess = "succession.assess";
    public const string Calibrate = "succession.calibrate";
    public const string SlatesRead = "succession.slates.read";
    public const string SlatesManage = "succession.slates.manage";
    public const string DevPlansRead = "succession.devplans.read";
    public const string DevPlansManage = "succession.devplans.manage";
    public const string ReportsRead = "succession.reports.read";
    public const string Self = "succession.self";

    public static readonly string[] All =
        [Read, PositionsManage, PoolsManage, Nominate, Assess, Calibrate, SlatesRead, SlatesManage, DevPlansRead, DevPlansManage, ReportsRead, Self];
}

/// <summary>Stable notification event codes. None of them names a slate's candidates to a manager or an employee.</summary>
public static class SuccessionEventCodes
{
    /// <summary>To the HR Director role: a slate version is waiting for a decision. Ids only.</summary>
    public const string SlateSubmitted = "SUCCESSION_SLATE_SUBMITTED";
    /// <summary>To the person who submitted it: approved or returned.</summary>
    public const string SlateDecided = "SUCCESSION_SLATE_DECIDED";
    /// <summary>To the plan's owner and the employee: the plan's review date has arrived.</summary>
    public const string DevPlanReviewDue = "DEVPLAN_REVIEW_DUE";
    /// <summary>To the action's owner: an open action is past its due date.</summary>
    public const string DevPlanActionOverdue = "DEVPLAN_ACTION_OVERDUE";
}

/// <summary>
/// A refusal with a stable machine code; <see cref="SuccessionErrorsAttribute"/> turns it into
/// <c>{ error, message, details }</c> with the status, so every succession endpoint fails the same way.
/// </summary>
public sealed class SuccessionException(int status, string code, string message, object? details = null) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public object? Details { get; } = details;

    /// <summary>Not found and not allowed look the same: a record outside the caller's reach is never confirmed to exist.</summary>
    public static SuccessionException NotFound(string what) => new(404, "not_found", $"{what} was not found.");
    public static SuccessionException Invalid(string message, object? details = null) => new(400, "invalid_request", message, details);
    public static SuccessionException Conflict(string code, string message, object? details = null) => new(409, code, message, details);
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class SuccessionErrorsAttribute : Attribute, IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        switch (context.Exception)
        {
            case SuccessionException e:
                context.Result = new ObjectResult(new { error = e.Code, message = e.Message, details = e.Details }) { StatusCode = e.Status };
                context.ExceptionHandled = true;
                break;
            case DbUpdateConcurrencyException:
                context.Result = new ObjectResult(new
                {
                    error = "concurrent_change",
                    message = "Someone else changed this record at the same time. Reload it and try again.",
                }) { StatusCode = 409 };
                context.ExceptionHandled = true;
                break;
        }
    }
}

/// <summary>
/// Who is acting: tenant, user, their own employee record (if any), the permissions they hold and the employees their
/// data scope reaches (IDataScopeService — HR to their companies, a manager to their reporting tree).
/// </summary>
public sealed record SuccessionActor(Guid TenantId, Guid? UserId, int? EmployeeId, IReadOnlySet<string> Permissions,
    IReadOnlyCollection<string> Roles, DataScope Scope, string? IpAddress = null, string? UserAgent = null)
{
    public bool Holds(string permission) => Permissions.Contains(permission);

    public RequestContext Request => new(IpAddress, UserAgent, UserId, TenantId, Roles.ToList(), Permissions.ToList());

    /// <summary>The background sweep and retention job: tenant-wide scope, no user, every key.</summary>
    public static SuccessionActor System(Guid tenantId) => new(tenantId, null, null,
        new HashSet<string>(SuccessionPermissions.All, StringComparer.OrdinalIgnoreCase), [],
        new DataScope { Level = DataScopeLevel.Organization }, null, "succession-sweep");
}

/// <summary>
/// The ONLY projection of a person that succession reads (SUC-08). It deliberately carries no gender, date of birth,
/// nationality or marital status: nothing in this module can order, filter or display people by a protected
/// characteristic, because the data never arrives. SuccessionArchitectureTests pins the shape and scans the module's
/// source for the forbidden fields.
/// </summary>
public sealed record SuccessionCandidateFacts(
    int EmployeeId, string EmployeeCode, string NameEn, string NameAr, Guid? CompanyId, Guid? DepartmentId,
    Guid? DesignationId, Guid? GradeId, Guid? PositionId, int? ManagerEmployeeId, string Status, string JobTitle, bool IsDeleted)
{
    public bool IsActive => !IsDeleted && Status == EmployeeStatuses.Active;

    /// <summary>Left employment (or the record was removed): a nomination is auto-withdrawn, a position counts as vacant.</summary>
    public bool HasLeft => IsDeleted || Status is EmployeeStatuses.Offboarded or EmployeeStatuses.Archived or EmployeeStatuses.Terminated
        or EmployeeStatuses.Exited or EmployeeStatuses.Inactive;

    public bool IsSuspended => Status == EmployeeStatuses.Suspended;
}

public static class SuccessionFacts
{
    /// <summary>Projects employees in SQL — only these columns are ever selected.</summary>
    public static IQueryable<SuccessionCandidateFacts> Project(IQueryable<Employee> employees) =>
        employees.Select(e => new SuccessionCandidateFacts(e.Id, e.EmployeeCode,
            e.EnglishName != "" ? e.EnglishName : e.FullName, e.ArabicName, e.CompanyId, e.DepartmentId, e.DesignationId, e.GradeId,
            e.PositionId, e.ManagerEmployeeId, e.Status, e.JobTitle, e.IsDeleted));

    /// <summary>
    /// The people a record names, read tenant-wide (a candidate may sit in another company of the tenant than the
    /// caller selected; authorisation is decided before this is called, by target and data scope).
    /// </summary>
    public static async Task<Dictionary<int, SuccessionCandidateFacts>> LoadAsync(ZayraDbContext db, Guid tenantId, IEnumerable<int> ids,
        CancellationToken ct)
    {
        var set = ids.Distinct().ToList();
        if (set.Count == 0) return new();
        var rows = await Project(SuccessionDb.TenantRows(db.Employees, tenantId).AsNoTracking().Where(e => set.Contains(e.Id))).ToListAsync(ct);
        return rows.ToDictionary(r => r.EmployeeId);
    }
}

/// <summary>The 9-box: the cell code the database generates, and what it means in both languages (SUC_DESIGN §2).</summary>
public static class NineBox
{
    public const string RuleVersion = "suc-9box-v1";

    public sealed record Cell(string Code, int Potential, string Band, string LabelEn, string LabelAr);

    public static readonly IReadOnlyList<Cell> Cells =
    [
        new("P3-Low", 3, PerformanceBandValues.Low, "Untapped potential", "إمكانات غير مستغلة"),
        new("P3-Moderate", 3, PerformanceBandValues.Moderate, "Emerging leader", "قائد ناشئ"),
        new("P3-High", 3, PerformanceBandValues.High, "Future leader", "قائد المستقبل"),
        new("P2-Low", 2, PerformanceBandValues.Low, "Inconsistent performer", "أداء غير مستقر"),
        new("P2-Moderate", 2, PerformanceBandValues.Moderate, "Core contributor", "مساهم أساسي"),
        new("P2-High", 2, PerformanceBandValues.High, "High performer", "أداء عالٍ"),
        new("P1-Low", 1, PerformanceBandValues.Low, "Below expectations", "دون التوقعات"),
        new("P1-Moderate", 1, PerformanceBandValues.Moderate, "Effective in role", "فعّال في الدور"),
        new("P1-High", 1, PerformanceBandValues.High, "Trusted professional", "محترف موثوق"),
    ];

    public static readonly IReadOnlyDictionary<int, (string En, string Ar)> PotentialLabels = new Dictionary<int, (string, string)>
    {
        [1] = ("Limited", "محدود"),
        [2] = ("Growth", "متوسط"),
        [3] = ("High", "عالٍ"),
    };

    /// <summary>The same rule as the generated column, for code that must place a row before it is saved.</summary>
    public static string? CellFor(int potential, string band) =>
        band is PerformanceBandValues.Low or PerformanceBandValues.Moderate or PerformanceBandValues.High ? $"P{Math.Clamp(potential, 1, 3)}-{band}" : null;
}

internal static class SuccessionDb
{
    /// <summary>
    /// Rows of ONE tenant without the company filter, for facts that are tenant-wide by definition: the people a
    /// succession record names (a candidate may sit in a sibling company), and the background sweep. The tenant is always
    /// re-applied (ScopedBypass.TenantWide).
    /// </summary>
    public static IQueryable<T> TenantRows<T>(DbSet<T> set, Guid tenantId) where T : class, Zayra.Api.Domain.Entities.ITenantOwned =>
        Zayra.Api.Infrastructure.Data.ScopedBypass.TenantWide(set, tenantId,
            "SUC: the people and records a succession row names, read after the row itself was authorised by target company and data scope.");

    public static IQueryable<Employee> TenantRows(DbSet<Employee> set, Guid tenantId) =>
        Zayra.Api.Infrastructure.Data.ScopedBypass.NullableTenantWide(set, tenantId,
            "SUC: the people a succession row names (candidate, incumbent, assessor, accountable executive), read after the row itself was authorised.");

    public static string Sha256Hex(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static string Reason(string? reason, string what = "a reason", int min = 1, int max = 1000)
    {
        var trimmed = reason?.Trim() ?? string.Empty;
        if (trimmed.Length < min || trimmed.Length > max)
            throw SuccessionException.Invalid(min > 1
                ? $"Give {what} of at least {min} characters (up to {max})."
                : $"Give {what} (up to {max} characters).");
        return trimmed;
    }

    /// <summary>A trigger refusal (immutability guard), surfaced as a 409 rather than a 500.</summary>
    public static bool IsGuardViolation(DbUpdateException ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is Npgsql.PostgresException pg && pg.SqlState == "P0001" && pg.MessageText.StartsWith("succession:", StringComparison.Ordinal))
                return true;
        return false;
    }
}
