using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Common.Import;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;
using Zayra.Api.Infrastructure.Operations;
using Zayra.Api.Infrastructure.Filters;

namespace Zayra.Api.Tests;

/// <summary>
/// P3-1 / P3-2 / P3-3: a free-text reason is capped at 500 characters (422, English and Arabic) on the cost-accounting endpoints, a reason in a query
/// string never reaches request logs or traces, the import-message regexes cannot be turned against the CPU, and a cost-centre CSV row cannot
/// deactivate a cost centre without saying why.
/// </summary>
public class CostAccountingInputHardeningTests : IClassFixture<CostCentreReadAccessHttpFixture>
{
    private readonly CostCentreReadAccessHttpFixture _f;
    public CostAccountingInputHardeningTests(CostCentreReadAccessHttpFixture f) => _f = f;

    private static readonly string Long = new('x', 501);
    private static readonly string AtLimit = new('x', 500);

    // ── reason cap: the filter ────────────────────────────────────────────────────────────────

    private sealed record Body(string? Reason, string? Other = null);
    private sealed record EmployeeBody(string? CostCenterReason);

    private static async Task<ActionExecutingContext> Run(ReasonLimitAttribute filter, Dictionary<string, object?> args)
    {
        var http = new DefaultHttpContext();
        var context = new ActionExecutingContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), new List<IFilterMetadata>(), args, new object());
        await filter.OnActionExecutionAsync(context, () => Task.FromResult<ActionExecutedContext>(null!));
        return context;
    }

    private static void ShouldBeTooLong(ActionExecutingContext c)
    {
        var result = c.Result.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(422);
        var json = JsonSerializer.Serialize(result.Value);
        json.Should().Contain("reason_too_long").And.Contain("500").And.Contain("messageAr");
    }

    [Fact]
    public async Task ABodyReasonOver500_Is422WithEnglishAndArabic_AndExactly500Passes()
    {
        ShouldBeTooLong(await Run(new ReasonLimitAttribute(), new() { ["request"] = new Body(Long) }));
        (await Run(new ReasonLimitAttribute(), new() { ["request"] = new Body(AtLimit) })).Result.Should().BeNull();
    }

    [Fact]
    public async Task AQueryStringReasonOver500_IsRefused_AndReasonsOfTrailingSpacesAreMeasuredTrimmed()
    {
        ShouldBeTooLong(await Run(new ReasonLimitAttribute(), new() { ["id"] = Guid.NewGuid(), ["reason"] = Long }));
        (await Run(new ReasonLimitAttribute(), new() { ["reason"] = AtLimit + "   " })).Result.Should().BeNull();
    }

    [Fact]
    public async Task OnlyTheNamedProperties_AreMeasured()
    {
        ShouldBeTooLong(await Run(new ReasonLimitAttribute("CostCenterReason"), new() { ["request"] = new EmployeeBody(Long) }));
        (await Run(new ReasonLimitAttribute("CostCenterReason"), new() { ["request"] = new Body(Long) })).Result.Should().BeNull();
    }

    [Fact]
    public void TheControllersThatTakeReasons_CarryTheFilter()
    {
        foreach (var type in new[] { typeof(CostCentersController), typeof(OrganizationController), typeof(DepartmentsController), typeof(FinanceGlController), typeof(EmployeesController) })
            type.GetCustomAttributes<ReasonLimitAttribute>(true).Should().NotBeEmpty(type.Name);
    }

    [Fact]
    public async Task ThroughThePipeline_ADeleteWithAReasonOver500_Is422_BeforeAnyWork()
    {
        using var r = await _f.Send(HttpMethod.Delete, $"/api/cost-centers/{_f.CostCentreId}?reason={Long}", _f.Token("Admin"));
        r.StatusCode.Should().Be((HttpStatusCode)422);
        var body = await r.Content.ReadAsStringAsync();
        body.Should().Contain("reason_too_long");
        JsonDocument.Parse(body).RootElement.GetProperty("messageAr").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ThroughThePipeline_APutWithAReasonOver500_Is422_NotTheFrameworks400()
    {
        using var r = await _f.Send(HttpMethod.Put, $"/api/cost-centers/{_f.CostCentreId}", _f.Token("Admin"),
            new { companyId = _f.CompanyId, code = "OPS", name = "Ops", isActive = true, reason = Long });
        r.StatusCode.Should().Be((HttpStatusCode)422);
    }

    // ── query-string redaction ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("?reason=because%20we%20said&companyId=abc", "?reason=[redacted]&companyId=abc")]
    [InlineData("?a=1&Reason=x&costCenterReason=y", "?a=1&Reason=[redacted]&costCenterReason=[redacted]")]
    [InlineData("?page=2", "?page=2")]
    [InlineData("", "")]
    public void TheQueryRedactor_BlanksReasons(string query, string expected) =>
        QueryRedactor.Redact(query).Should().Be(expected);

    [Fact]
    public void RequestStartLogging_IsNotEmitted_BecauseItPrintsTheFullQueryString()
    {
        var file = Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location)!, "appsettings.json");
        var level = JsonDocument.Parse(File.ReadAllText(file)).RootElement
            .GetProperty("Logging").GetProperty("LogLevel").GetProperty("Microsoft.AspNetCore.Hosting.Diagnostics").GetString();
        level.Should().BeOneOf("Warning", "Error", "Critical", "None");
    }

    // ── regex timeouts ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryImportMessageRegex_HasAShortMatchTimeout()
    {
        var fields = typeof(ImportMessagesAr).GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        var regexes = new List<Regex>();
        foreach (var f in fields)
        {
            var value = f.GetValue(null);
            if (value is Regex single) regexes.Add(single);
            else if (value is System.Collections.IEnumerable many)
                foreach (var item in many)
                    if (item is ITuple t && t[0] is Regex r) regexes.Add(r);
        }
        regexes.Should().HaveCountGreaterThan(10);
        regexes.Should().OnlyContain(r => r.MatchTimeout == TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public void ATimedOutMessage_FallsBackToEnglishOnly_InsteadOfThrowing()
    {
        var hostile = "Cost center code '" + new string('(', 4000) + "': nope";
        var act = () => ImportMessagesAr.Translate(hostile);
        act.Should().NotThrow();
    }

    // ── CSV deactivation needs a reason ───────────────────────────────────────────────────────

    private static async Task<(CostCentersController C, ZayraDbContext Db, Guid Company)> Controller()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid();
        var company = new Company { TenantId = tenant, LegalNameEn = "Alpha", RegistrationNumber = "A-1", IsActive = true, CountryCode = "SA", DefaultCurrency = "SAR" };
        db.AddRange(company, new CostCenter { TenantId = tenant, CompanyId = company.Id, Code = "SPARE", Name = "Spare" });
        await db.SaveChangesAsync();
        var c = new CostCentersController(new OrganizationSetupService(db, new AuditService(db)), db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                        new Claim("permission", "organization.costcenter.manage"), new Claim("is_group_scope", "true"),
                    }, "Test"))
                }
            }
        };
        return (c, db, company.Id);
    }

    private static ImportPreviewResult Preview(IActionResult r) => (ImportPreviewResult)((OkObjectResult)r).Value!;

    [Fact]
    public async Task DeactivatingByCsvWithoutAReason_IsARowError_AndNothingChanges()
    {
        var (c, db, company) = await Controller();
        var csv = "Code,Name,IsActive\nSPARE,Spare,false\n";
        var preview = Preview(await c.ImportPreview(new CostCenterImportRequest(csv, company), default));
        var row = preview.Rows.Single();
        row.Status.Should().Be(ImportRowStatus.Error);
        row.Errors.Should().Contain(e => e.Contains("Reason"));
        row.ErrorsAr.Should().NotBeNull().And.Contain(e => e != null && e.Contains("السبب"));

        await c.Import(new CostCenterImportRequest(csv, company), default);
        (await db.CostCenters.AsNoTracking().SingleAsync(x => x.Code == "SPARE")).IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task DeactivatingByCsvWithAReasonColumn_Works_AndTheReasonIsAudited()
    {
        var (c, db, company) = await Controller();
        var csv = "Code,Name,IsActive,Reason\nSPARE,Spare,false,Closed after the merger\n";
        Preview(await c.ImportPreview(new CostCenterImportRequest(csv, company), default)).Rows.Single().Status.Should().NotBe(ImportRowStatus.Error);
        await c.Import(new CostCenterImportRequest(csv, company), default);
        (await db.CostCenters.AsNoTracking().SingleAsync(x => x.Code == "SPARE")).IsActive.Should().BeFalse();
        (await db.AuditLogs.AsNoTracking().Where(a => a.Action == "organization.cost_center_updated").ToListAsync())
            .Should().Contain(a => a.Metadata!.Contains("Closed after the merger"));
    }

    [Fact]
    public async Task ARowThatKeepsTheCostCentreActive_NeedsNoReason()
    {
        var (c, _, company) = await Controller();
        Preview(await c.ImportPreview(new CostCenterImportRequest("Code,Name,IsActive\nSPARE,Spare renamed,true\n", company), default))
            .Rows.Single().Status.Should().NotBe(ImportRowStatus.Error);
    }

    [Fact]
    public async Task ARowReasonOver500_IsARowError()
    {
        var (c, _, company) = await Controller();
        var preview = Preview(await c.ImportPreview(new CostCenterImportRequest($"Code,Name,IsActive,Reason\nSPARE,Spare,false,{Long}\n", company), default));
        preview.Rows.Single().Status.Should().Be(ImportRowStatus.Error);
    }

    [Fact]
    public async Task TheTemplateAndTheExport_CarryTheReasonColumn()
    {
        var (c, _, _) = await Controller();
        var template = System.Text.Encoding.UTF8.GetString(((FileContentResult)c.ImportTemplate()).FileContents);
        template.Split('\n')[0].Trim().Should().EndWith("Reason");
        var export = System.Text.Encoding.UTF8.GetString(((FileContentResult)await c.Export(default)).FileContents);
        export.Split('\n')[0].Trim().Should().Be(template.Split('\n')[0].Trim());
    }
}
