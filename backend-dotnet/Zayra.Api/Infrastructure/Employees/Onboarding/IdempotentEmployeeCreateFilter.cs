using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees.Onboarding;

/// <summary>
/// <c>[IdempotentEmployeeCreate]</c> on <c>POST /api/employees</c>: a client that retries a create (double click, lost
/// response, flaky network) gets the employee it already made, not a second record.
///
/// <para><b>Storage.</b> Two nullable columns on <c>employees</c> (<c>create_idempotency_key</c>, the client's UUID,
/// and <c>create_request_fingerprint</c>, a SHA-256 of tenant + creator + company + normalised name) with a unique
/// partial index on (tenant, key). No table, no TTL: a key is the identity of one hire for as long as that hire exists.
/// The client's key is never used as the employee's public id.</para>
///
/// <para><b>Replay.</b> Same tenant, same creator and same fingerprint returns 200 with the employee, read through
/// the CALLER's sensitive-field mask (the stored body is never replayed). A different creator or a different
/// fingerprint is 422 <c>idempotency_key_reuse_mismatch</c>. The same key in another tenant is an unrelated key.
/// A failed attempt stores nothing, so it can be retried with the same key.</para>
///
/// <para><b>Concurrency.</b> Two requests with one key race into the identity lock and the unique index. Whichever
/// loses surfaces as a duplicate 409 or a unique violation; either way this filter finds the committed row and turns
/// the outcome into the replay.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class IdempotentEmployeeCreateAttribute : Attribute, IFilterFactory
{
    public bool IsReusable => false;
    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) =>
        new IdempotentEmployeeCreateFilter(serviceProvider.GetService<ILoggerFactory>()?.CreateLogger("EmployeeCreateIdempotency"));
}

public sealed class IdempotentEmployeeCreateFilter : IAsyncActionFilter
{
    public const string HeaderName = "Idempotency-Key";
    public const string MismatchCode = "idempotency_key_reuse_mismatch";
    public const string UniqueIndexName = "ux_employees_tenant_create_idempotency_key";
    private const string ItemKey = "hire.idempotency";

    private readonly ILogger? _log;
    public IdempotentEmployeeCreateFilter(ILogger? log) => _log = log;

    private sealed record Claim(Guid Key, string Fingerprint);

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        var raw = http.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            // Optional for the legacy form; the new wizard always sends one.
            _log?.LogWarning("Employee create without an {Header}; a retry cannot be recognised.", HeaderName);
            await next();
            return;
        }
        if (!Guid.TryParse(raw.Trim(), out var key))
        {
            context.Result = Problem(http, StatusCodes.Status400BadRequest, "validation_failed", "Idempotency-Key must be a UUID.",
                new Dictionary<string, string[]> { ["idempotencyKey"] = ["invalid_format"] });
            return;
        }
        var tenantClaim = http.User.FindFirstValue("tenant_id");
        var userClaim = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.FindFirstValue("sub");
        if (!Guid.TryParse(tenantClaim, out var tenantId) || !Guid.TryParse(userClaim, out var userId)
            || context.ActionArguments.Values.OfType<EmployeeCreateRequest>().FirstOrDefault() is not { } request)
        {
            await next();
            return;
        }

        var claim = new Claim(key, FingerprintOf(tenantId, userId, request.CompanyId, request.EnglishName));
        var db = http.RequestServices.GetRequiredService<ZayraDbContext>();
        var ct = http.RequestAborted;

        var existing = await FindAsync(db, tenantId, key, ct);
        if (existing is not null)
        {
            context.Result = await ReplayOrMismatchAsync(context, db, tenantId, userId, claim, existing, ct);
            return;
        }

        http.Items[ItemKey] = claim;
        var executed = await next();

        if (executed.Result is CreatedAtActionResult or CreatedResult or CreatedAtRouteResult) return;
        // Not created. If a row with our key now exists, a concurrent request with the same key won: replay it.
        var lostRace = executed.Exception is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg }
                       && string.Equals(pg.ConstraintName, UniqueIndexName, StringComparison.Ordinal);
        if (executed.Exception is not null && !lostRace) return;
        db.ChangeTracker.Clear();
        var winner = await FindAsync(db, tenantId, key, ct);
        if (winner is null) return;
        executed.Exception = null;
        executed.ExceptionHandled = true;
        executed.Result = await ReplayOrMismatchAsync(context, db, tenantId, userId, claim, winner, ct);
    }

    /// <summary>Stamps the claimed key onto the new employee inside the create (service hook).</summary>
    public static void Stamp(IHttpContextAccessor? accessor, Employee employee)
    {
        if (accessor?.HttpContext?.Items[ItemKey] is Claim claim)
        {
            employee.CreateIdempotencyKey = claim.Key;
            employee.CreateRequestFingerprint = claim.Fingerprint;
        }
    }

    /// <summary>SHA-256 of tenant + creator + company + normalised name (letters and digits, upper case).</summary>
    public static string FingerprintOf(Guid tenantId, Guid userId, Guid? companyId, string? englishName)
    {
        var name = new StringBuilder();
        foreach (var ch in (englishName ?? string.Empty).Trim())
            if (char.IsLetterOrDigit(ch)) name.Append(char.ToUpperInvariant(ch));
        var text = $"{tenantId:N}|{userId:N}|{companyId?.ToString("N") ?? "-"}|{name}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private sealed record Existing(int Id, Guid? CreatedBy, string? Fingerprint);

    private static async Task<Existing?> FindAsync(ZayraDbContext db, Guid tenantId, Guid key, CancellationToken ct)
    {
        var row = await ScopedBypass.NullableTenantWide(db.Employees, tenantId,
                "Idempotent create: the key's employee may sit in a company the caller cannot see; the tenant is re-applied and only the id, creator and fingerprint are read.")
            .AsNoTracking()
            .Where(e => e.CreateIdempotencyKey == key)
            .Select(e => new { e.Id, e.CreatedBy, e.CreateRequestFingerprint })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : new Existing(row.Id, row.CreatedBy, row.CreateRequestFingerprint);
    }

    private static async Task<IActionResult> ReplayOrMismatchAsync(
        ActionExecutingContext context, ZayraDbContext db, Guid tenantId, Guid userId, Claim claim,
        Existing row, CancellationToken ct)
    {
        if (row.CreatedBy != userId || !string.Equals(row.Fingerprint, claim.Fingerprint, StringComparison.Ordinal))
            return Problem(context.HttpContext, StatusCodes.Status422UnprocessableEntity, MismatchCode,
                "This Idempotency-Key was already used for a different request.", null);

        var management = context.HttpContext.RequestServices.GetRequiredService<IEmployeeManagementService>();
        var http = context.HttpContext;
        var ctx = new RequestContext(http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString(), userId, tenantId);
        var sensitive = http.User.HasPermission("employees.sensitive");
        var body = await management.GetAsync(tenantId, row.Id, sensitive, ctx, ct);
        return body is null ? new NotFoundResult() : new OkObjectResult(body);
    }

    internal static IActionResult Problem(HttpContext http, int status, string code, string detail, IDictionary<string, string[]>? errors)
    {
        var problem = new ProblemDetails { Status = status, Title = code, Detail = detail };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = http.TraceIdentifier;
        if (errors is not null) problem.Extensions["errors"] = errors;
        return new ObjectResult(problem)
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" },
        };
    }
}
