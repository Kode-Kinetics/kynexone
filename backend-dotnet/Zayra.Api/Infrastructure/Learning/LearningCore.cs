using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Learning;

/// <summary>
/// A refusal with a stable machine code. Controllers carry <see cref="LearningErrorsAttribute"/>, which turns it into
/// <c>{ error, message, details }</c> with the status — so every learning endpoint fails the same way.
/// </summary>
public sealed class LearningException(int status, string code, string message, object? details = null, int? retryAfterSeconds = null) : Exception(message)
{
    public int Status { get; } = status;
    /// <summary>For 503 responses: seconds the caller should wait before retrying.</summary>
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
    public string Code { get; } = code;
    public object? Details { get; } = details;

    public static LearningException NotFound(string what) => new(404, "not_found", $"{what} was not found.");
    public static LearningException Invalid(string message, object? details = null) => new(400, "invalid_request", message, details);
    public static LearningException Conflict(string code, string message, object? details = null) => new(409, code, message, details);
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class LearningErrorsAttribute : Attribute, IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        switch (context.Exception)
        {
            case LearningException e:
                context.Result = new ObjectResult(new { error = e.Code, message = e.Message, details = e.Details }) { StatusCode = e.Status };
                if (e.RetryAfterSeconds is int retry) context.HttpContext.Response.Headers.RetryAfter = retry.ToString();
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
/// Who is acting, in which tenant, which employees they may reach (IDataScopeService), and — for segregation of
/// duties — the employee record the acting login is linked to (<see cref="SelfEmployeeId"/>): nobody decides on their
/// own training record.
/// </summary>
public sealed record LearningActor(Guid TenantId, Guid? UserId, string? IpAddress, string? UserAgent, DataScope Scope, int? SelfEmployeeId = null)
{
    /// <summary>Refuses an HR decision (reset, revoke, correct, verify) on the actor's own record.</summary>
    public void RefuseSelf(int subjectEmployeeId)
    {
        if (SelfEmployeeId is int self && self == subjectEmployeeId)
            throw new LearningException(403, "self_decision_forbidden", "You cannot make this decision on your own training record. Another HR user must.");
    }

    /// <summary>The background sweep: tenant-wide scope, no user.</summary>
    public static LearningActor System(Guid tenantId) =>
        new(tenantId, null, null, "learning-sweep", new DataScope { Level = DataScopeLevel.Organization });

    public RequestContext Request => new(IpAddress, UserAgent, UserId, TenantId);
}

public static class LearningTokens
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I: read aloud and typed from paper

    /// <summary>A URL-safe random secret of <paramref name="bytes"/> random bytes.</summary>
    public static string NewSecret(int bytes = 32) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Sha256Hex(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>Constant-time comparison of two hex hashes.</summary>
    public static bool HashEquals(string? a, string? b) =>
        a is not null && b is not null && a.Length == b.Length
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    public static string Code(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    public static string NewCertificateNumber(DateTime issuedUtc) => $"LRN-{issuedUtc:yyyy}-{Code(8)}";
}

/// <summary>Path eligibility: company / department / designation / grade lists; an empty list means "any".</summary>
public sealed record LearningEligibilityCriteria(
    IReadOnlyList<Guid>? CompanyIds = null,
    IReadOnlyList<Guid>? DepartmentIds = null,
    IReadOnlyList<Guid>? DesignationIds = null,
    IReadOnlyList<Guid>? GradeIds = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static LearningEligibilityCriteria Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<LearningEligibilityCriteria>(json, Json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    public string Serialize() => JsonSerializer.Serialize(new LearningEligibilityCriteria(
        CompanyIds?.Distinct().ToList() ?? [], DepartmentIds?.Distinct().ToList() ?? [],
        DesignationIds?.Distinct().ToList() ?? [], GradeIds?.Distinct().ToList() ?? []), Json);

    public bool IsOpen => (CompanyIds?.Count ?? 0) + (DepartmentIds?.Count ?? 0) + (DesignationIds?.Count ?? 0) + (GradeIds?.Count ?? 0) == 0;

    /// <summary>Why <paramref name="employee"/> is not eligible (machine codes), or empty when eligible.</summary>
    public IReadOnlyList<string> Reasons(Employee employee)
    {
        var reasons = new List<string>();
        if (CompanyIds is { Count: > 0 } c && (employee.CompanyId is not Guid company || !c.Contains(company))) reasons.Add("company_not_eligible");
        if (DepartmentIds is { Count: > 0 } d && (employee.DepartmentId is not Guid department || !d.Contains(department))) reasons.Add("department_not_eligible");
        if (DesignationIds is { Count: > 0 } g && (employee.DesignationId is not Guid designation || !g.Contains(designation))) reasons.Add("designation_not_eligible");
        if (GradeIds is { Count: > 0 } r && (employee.GradeId is not Guid grade || !r.Contains(grade))) reasons.Add("grade_not_eligible");
        return reasons;
    }
}

internal static class LearningDb
{
    /// <summary>
    /// Rows of ONE tenant without the company filter, for the two places that must not depend on the caller's company
    /// scope: (1) the cmi5 endpoints, called anonymously by the AU — in strict scope mode an anonymous principal has an
    /// EMPTY company scope, so a filtered read would silently find nothing (and e.g. complete an assignment because it
    /// "saw" no unfinished siblings); (2) facts that are tenant-wide by definition (certificate-number uniqueness, the
    /// siblings of an enrollment that was already authorised). The tenant is always re-applied (ScopedBypass.TenantWide).
    /// </summary>
    public static IQueryable<T> TenantRows<T>(Microsoft.EntityFrameworkCore.DbSet<T> set, Guid tenantId) where T : class, Zayra.Api.Domain.Entities.ITenantOwned =>
        Zayra.Api.Infrastructure.Data.ScopedBypass.TenantWide(set, tenantId,
            "LRN: reached through an already-authorised enrollment or an authenticated cmi5 session token; anonymous AU calls have no company scope.");

    /// <summary>A unique-index violation (Postgres 23505, SQLite 19) — the loser of a race on a "one live row" index.</summary>
    public static bool IsUniqueViolation(DbUpdateException ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is Npgsql.PostgresException pg && pg.SqlState == "23505") return true;
            if (e.GetType().Name == "SqliteException" && e.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Runs <paramref name="work"/> in one transaction under the context's execution strategy (production retries
    /// transient failures, which refuses a bare BeginTransaction — see ExecutionStrategyLintTests). The delegate must
    /// load what it changes, because a retry runs it again from the start.
    /// </summary>
    public static async Task InTransactionAsync(Microsoft.EntityFrameworkCore.DbContext db, Func<Task> work, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await work();
            await transaction.CommitAsync(ct);
        });
    }

    /// <summary>Detaches the entities a failed SaveChanges left Added, so the context can be reused for the next row.</summary>
    public static void DetachAdded(Microsoft.EntityFrameworkCore.DbContext db)
    {
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList())
            entry.State = EntityState.Detached;
    }
}

/// <summary>
/// The one definition of "overdue", shared by the reports, the KPI, the assignment views and the sweep's notices:
/// an open assignment past its due date, of a person whose training is chased, with something left for THEM to do.
/// <list type="bullet">
/// <item>Chased: Active; Suspended (still employed — the obligation stands; reports label the status); Invited once their
/// start date has come. Leavers are closed by the sweep and never counted.</item>
/// <item>Something left to do: an assignment whose only open required items are completions waiting for HR verification
/// is "Waiting for HR verification", not overdue (the learner has done their part).</item>
/// </list>
/// No AsNoTracking here: these are subqueries, and EF applies a subquery's AsNoTracking to the whole outer query.
/// </summary>
public static class LearningOverdue
{
    public static IQueryable<int> ChasedEmployees(Zayra.Api.Data.ZayraDbContext db, Guid tenantId, DateOnly today)
    {
        var started = DateTime.SpecifyKind(today.ToDateTime(TimeOnly.MaxValue), DateTimeKind.Utc);
        return db.Employees.Where(e => e.TenantId == tenantId && !e.IsDeleted
                && (e.Status == EmployeeStatuses.Active || e.Status == EmployeeStatuses.Suspended
                    || (e.Status == EmployeeStatuses.Invited && e.JoiningDate <= started)))
            .Select(e => e.Id);
    }

    public static bool IsChased(string? status, DateTime joiningDate, DateOnly today) =>
        status == EmployeeStatuses.Active || status == EmployeeStatuses.Suspended
        || (status == EmployeeStatuses.Invited && DateOnly.FromDateTime(joiningDate) <= today);

    /// <summary>Assignments whose open required items are all completions waiting for HR verification.</summary>
    public static IQueryable<Guid> AwaitingVerification(Zayra.Api.Data.ZayraDbContext db, Guid tenantId) =>
        db.LearningAssignments.Where(a => a.TenantId == tenantId
                && db.LearningEnrollments.Any(e => e.AssignmentId == a.Id && e.IsRequired && e.Status == LearningEnrollmentStatuses.PendingVerification)
                && !db.LearningEnrollments.Any(e => e.AssignmentId == a.Id && e.IsRequired
                    && e.Status != LearningEnrollmentStatuses.Completed && e.Status != LearningEnrollmentStatuses.PendingVerification))
            .Select(a => a.Id);

    public static bool IsAwaitingVerification(IEnumerable<(bool IsRequired, string Status)> items)
    {
        var required = items.Where(i => i.IsRequired).ToList();
        return required.Any(i => i.Status == LearningEnrollmentStatuses.PendingVerification)
            && required.All(i => i.Status is LearningEnrollmentStatuses.Completed or LearningEnrollmentStatuses.PendingVerification);
    }

    public static bool IsOverdue(string assignmentStatus, DateOnly? due, DateOnly today, bool chased, bool awaitingVerification) =>
        LearningAssignmentStatuses.Active.Contains(assignmentStatus) && due is { } d && d < today && chased && !awaitingVerification;
}
