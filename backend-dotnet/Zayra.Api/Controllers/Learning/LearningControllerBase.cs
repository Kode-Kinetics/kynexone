using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Learning;
using Zayra.Api.Infrastructure.Reports;

namespace Zayra.Api.Controllers.Learning;

/// <summary>
/// Shared plumbing for the HR-facing learning controllers: the actor (tenant, user, and the employee scope
/// IDataScopeService resolves — HR to their companies, a manager to their reporting tree) and the CSV export through
/// the formula-injection-safe ReportTabulator.
/// </summary>
[LearningErrors]
public abstract class LearningControllerBase : ControllerBase
{
    protected async Task<LearningActor> ActorAsync(CancellationToken ct)
    {
        var tenantId = this.GetTenantId() ?? throw new LearningException(401, "unauthenticated", "Sign in again.");
        var scopes = HttpContext.RequestServices.GetRequiredService<IDataScopeService>();
        var scope = await scopes.ResolveAsync(User, tenantId, ct);
        // The acting login's own employee record (if linked): HR decisions on it are refused (segregation of duties).
        var db = HttpContext.RequestServices.GetRequiredService<Zayra.Api.Data.ZayraDbContext>();
        var self = await Zayra.Api.Infrastructure.Common.CallerEmployeeResolver.ResolveAsync(db, User, tenantId, ct);
        return new LearningActor(tenantId, this.GetUserId(), HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString(), scope, self);
    }

    protected bool Holds(string permission) => User.Claims.Any(c => c.Type == "permission" && c.Value == permission);

    protected IActionResult Rows<T>(IReadOnlyList<T> rows, string? format, string name)
    {
        if (!string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase)) return Ok(rows);
        var tables = ReportTabulator.Tabulate(JsonSerializer.SerializeToElement(rows), ReportTabulator.Humanise(name));
        return File(ReportTabulator.ToCsv(tables), "text/csv", $"learning-{name}-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv");
    }
}
