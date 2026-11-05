using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll.ManpowerRecon;

namespace Zayra.Api.Tests.Recon;

/// <summary>Controller builders shared by the reconciliation access, gate and invalidation tests.</summary>
internal static class ReconHarness
{
    internal sealed class FixedScope : IDataScopeService
    {
        private readonly DataScope _scope;
        public FixedScope(DataScope? scope = null) =>
            _scope = scope ?? new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null };
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) => Task.FromResult(_scope);
    }

    public static HttpContext Http(Guid tenantId, string? role, IEnumerable<string> permissions, Guid? userId, string? entityScopeJson, string name = "Reviewer")
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, (userId ?? Guid.NewGuid()).ToString()),
            new(ClaimTypes.Name, name),
        };
        if (role is not null) claims.Add(new Claim(ClaimTypes.Role, role));
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        if (entityScopeJson is not null) claims.Add(new Claim("entity_scope", entityScopeJson));
        return new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
    }

    public static string CompanyScope(params Guid[] companies) =>
        JsonSerializer.Serialize(new { v = 2, m = "companies", c = companies });

    public static PayrollController Payroll(
        ZayraDbContext db, Guid tenantId, string? role, IEnumerable<string> permissions, Guid? userId = null, string? entityScopeJson = null)
    {
        var rules = PayComponentNetPayDefectTests.KsaRules();
        var http = Http(tenantId, role, permissions, userId, entityScopeJson);
        return new PayrollController(db, new FixedScope(), new HttpContextAccessor { HttpContext = http }, new F2NullNotifications(),
            new KsaTestPackResolver(rules), rules, new F2NullLetters(), new NullDocumentStorage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(8))
        { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    public static PayrollReconController Recon(
        ZayraDbContext db, Guid tenantId, string? role, IEnumerable<string> permissions, Guid? userId = null,
        string? entityScopeJson = null, DataScope? dataScope = null)
    {
        var http = Http(tenantId, role, permissions, userId, entityScopeJson);
        return new PayrollReconController(db, new FixedScope(dataScope))
        { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    public static readonly string[] ReadPermissions = { "payroll.read" };
    public static readonly string[] SignPermissions = { "payroll.read", "payroll.approve" };

    /// <summary>The body as the client would read it: MVC's camelCase web defaults.</summary>
    public static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static T Body<T>(IActionResult result) where T : class =>
        (T)(((ObjectResult)result).Value ?? throw new InvalidOperationException("The result has no body."));

    /// <summary>
    /// A sign-off request that satisfies every control, built the way the screen builds it: read the reconciliation, ask the
    /// drill-down how many employees are flagged and which ones were not paid or unexplained, explain every flagged item and each of them.
    /// </summary>
    public static async Task<ReconSignoffRequest> ValidRequestAsync(PayrollReconController ctrl, Guid runId, Guid? priorRunId = null)
    {
        var dto = Body<ReconDto>(await ctrl.Get(runId, priorRunId, CancellationToken.None));
        var flagged = Body<ReconEmployeePageDto>(await ctrl.Employees(runId, "value", null, null, null, "flagged", 1, 200, CancellationToken.None, priorRunId)).Total;
        var explanations = new List<ReconExplanationRequest>();
        foreach (var section in dto.Value.Sections)
            foreach (var item in section.Items.Where(i => i.Flagged))
                explanations.Add(new ReconExplanationRequest(ReconExplanationScopes.Item, $"{section.Kind}:{item.Code}", $"Reviewed: {item.Label} is expected this month."));
        foreach (var category in new[] { "NOT_PAID", "UNEXPLAINED" })
        {
            var page = Body<ReconEmployeePageDto>(await ctrl.Employees(runId, "volume", category, null, null, null, 1, 200, CancellationToken.None, priorRunId));
            explanations.AddRange(page.Items.Select(r => new ReconExplanationRequest(ReconExplanationScopes.Employee, r.EmployeeId.ToString(), "Reviewed: held out for a known reason.")));
        }
        return new ReconSignoffRequest(dto.ProcessVersion, dto.SnapshotHash, priorRunId, flagged, explanations, "Reviewed against last month.");
    }
}
