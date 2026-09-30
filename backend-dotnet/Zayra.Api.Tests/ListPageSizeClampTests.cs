using System.Reflection;
using System.Security.Claims;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Finance;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Every paged list answers an oversized <c>pageSize</c> with at most 100 rows, the way the employee and
/// organisation lists always have.
///
/// <para>THE DEFECT. Most list endpoints clamp <c>pageSize</c> to 1..100 (<c>EmployeeManagementService</c>,
/// <c>OrganizationSetupService</c>, <c>AccessController</c>). The payroll slips, payslips and overtime
/// request lists, and about forty other lists, applied no cap at all: the frontend asked for 200 and got
/// 200, and a caller could ask for a million. A <c>page</c> below 1 became a negative OFFSET, which
/// Postgres refuses with a 500.</para>
///
/// <para>The frontend pages through at 100 (<c>frontend/src/lib/paging.ts</c>, <c>API_MAX_PAGE_SIZE</c>),
/// so the clamp changes no screen: it only stops a single request from pulling an unbounded page.</para>
/// </summary>
public class ListPageSizeClampTests
{
    private const int Rows = 150;
    private static readonly CancellationToken Ct = CancellationToken.None;

    // ── The three lists the register named ─────────────────────────────────────────────────────

    [Fact]
    public async Task PayrollSlips_AnswerAnOversizedPageWith100Rows_AndPageTwoHasTheRest()
    {
        var (db, tenantId, runId) = await SeedRunAsync();
        var ctrl = PayComponentNetPayDefectTests.Build(db, tenantId, "employees.write", "payroll.read");

        var first = Paged<PayrollSlipDto>(await ctrl.Slips(runId, 1, 500, Ct));
        first.Items.Should().HaveCount(100);
        first.PageSize.Should().Be(100, "the response states the page size it actually applied");
        first.Total.Should().Be(Rows);

        var second = Paged<PayrollSlipDto>(await ctrl.Slips(runId, 2, 100, Ct));
        second.Items.Should().HaveCount(Rows - 100);
        first.Items.Concat(second.Items).Select(s => s.EmployeeId).Should().OnlyHaveUniqueItems()
            .And.HaveCount(Rows, "paging at 100, the way the frontend does, still reads every slip exactly once");
    }

    [Fact]
    public async Task Payslips_AnswerAnOversizedPageWith100Rows()
    {
        var (db, tenantId, runId) = await SeedRunAsync(withPayslips: true);
        var ctrl = PayComponentNetPayDefectTests.Build(db, tenantId, "employees.write", "payroll.read");

        var first = Paged<PayslipListItemDto>(await ctrl.ListPayslips(runId, 1, 200, Ct));
        first.Items.Should().HaveCount(100);
        first.PageSize.Should().Be(100);
        first.Total.Should().Be(Rows);
    }

    [Fact]
    public async Task OvertimeRequests_AnswerAnOversizedPageWith100Rows()
    {
        var db = NewDb();
        var tenantId = Guid.NewGuid();
        for (var i = 0; i < Rows; i++)
            db.OvertimeRequests.Add(new OvertimeRequest
            {
                TenantId = tenantId, EmployeeId = i + 1, EmployeeName = $"Employee {i + 1}",
                WorkDate = new DateOnly(2026, 9, 1).AddDays(i % 28), RequestedMinutes = 60, Status = "Approved",
            });
        await db.SaveChangesAsync();

        var ctrl = new OvertimeController(db, new DataScopeService(db), new HrmHierarchyService(db, new AuditService(db)));
        Bind(ctrl, tenantId, "employees.write");

        var result = (await ctrl.Requests(null, null, 1, 200, Ct)).Result;
        var page = (PagedResult<OvertimeRequest>)((OkObjectResult)result!).Value!;
        page.Items.Should().HaveCount(100);
        page.PageSize.Should().Be(100);
        page.Total.Should().Be(Rows);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task APageBelowOne_IsTheFirstPage_NotANegativeOffset(int page)
    {
        var (db, tenantId, runId) = await SeedRunAsync();
        var ctrl = PayComponentNetPayDefectTests.Build(db, tenantId, "employees.write", "payroll.read");

        var result = Paged<PayrollSlipDto>(await ctrl.Slips(runId, page, 0, Ct));
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(1, "a page size below 1 is the smallest page, not an empty or negative one");
        result.Items.Should().ContainSingle();
    }

    // ── The sweep: no list endpoint is left without the clamp ──────────────────────────────────

    /// <summary>
    /// Actions that hand <c>page</c>/<c>pageSize</c> to a service which clamps them itself, named with that
    /// service so a reader can check it. Everything else must clamp in the action.
    /// </summary>
    private static readonly Dictionary<string, string> ClampedByTheirService = new()
    {
        ["ApprovalRequests.Search"] = "ApprovalWorkflowService.GetRequestsAsync",
        ["ApprovalWorkflows.List"] = "ApprovalWorkflowService.GetWorkflowsAsync",
        ["ApprovalWorkflows.Requests"] = "ApprovalWorkflowService.GetRequestsAsync",
        ["Branches.Search"] = "OrganizationSetupService.GetBranchesAsync",
        ["CostCenters.Search"] = "OrganizationSetupService.GetCostCentersAsync",
        ["Departments.Search"] = "OrganizationSetupService.GetDepartmentsAsync",
        ["Designations.Search"] = "OrganizationSetupService.GetDesignationsAsync",
        ["Grades.Search"] = "OrganizationSetupService.GetGradesAsync",
        ["Organization.Companies"] = "OrganizationSetupService.GetCompaniesAsync",
        ["Organization.Branches"] = "OrganizationSetupService.GetBranchesAsync",
        ["Organization.Departments"] = "OrganizationSetupService.GetDepartmentsAsync",
        ["Organization.Designations"] = "OrganizationSetupService.GetDesignationsAsync",
        ["Organization.Grades"] = "OrganizationSetupService.GetGradesAsync",
        ["Organization.CostCenters"] = "OrganizationSetupService.GetCostCentersAsync",
        ["Timesheets.List"] = "TimesheetService.ListAsync (1..200)",
    };

    /// <summary>Lists that already capped at 200 before this change; left as they were.</summary>
    private static readonly HashSet<string> CappedAt200 = new()
    {
        "HrLetters.Register", "HrLetters.Requests", "Platform.ListLoginActivity", "Platform.AllInvoices", "Timesheets.Inbox",
    };

    [Fact]
    public void EveryListEndpoint_ClampsPageSizeAndPage()
    {
        var apiRoot = ResolveApiRoot();
        var wrong = new List<string>();
        var actions = PagedActions().ToList();
        actions.Should().HaveCountGreaterThan(50, "the sweep must actually find the paged actions it guards");

        foreach (var (controller, method) in actions)
        {
            var key = $"{controller.Name[..^"Controller".Length]}.{method.Name}";
            if (ClampedByTheirService.ContainsKey(key)) continue;

            var body = ActionBody(apiRoot, controller, method);
            if (body is null) { wrong.Add($"{key}: source not found"); continue; }

            var clamp = Regex.Match(body, @"Math\.Clamp\(pageSize,\s*1,\s*(\d+)\)");
            var cap = CappedAt200.Contains(key) ? 200 : 100;
            if (!clamp.Success) wrong.Add($"{key}: pageSize is not clamped");
            else if (int.Parse(clamp.Groups[1].Value) != cap) wrong.Add($"{key}: pageSize clamped to {clamp.Groups[1].Value}, expected {cap}");

            var hasPage = method.GetParameters().Any(p => p.Name == "page");
            if (hasPage && !Regex.IsMatch(body, @"Math\.Max\(1,\s*page\)"))
                wrong.Add($"{key}: page is not floored at 1");
        }

        wrong.Should().BeEmpty(
            "every paged list clamps pageSize to 1..100 and floors page at 1, as EmployeesController does. " +
            "Unclamped:\n" + string.Join('\n', wrong));
    }

    // ── Harness ────────────────────────────────────────────────────────────────────────────────

    private static ZayraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase($"page-clamp-{Guid.NewGuid():N}").Options);

    private static async Task<(ZayraDbContext Db, Guid TenantId, Guid RunId)> SeedRunAsync(bool withPayslips = false)
    {
        var db = NewDb();
        var tenantId = Guid.NewGuid();
        var run = new PayrollRun { TenantId = tenantId, Year = 2026, Month = 9, Status = "Locked", RunType = "Regular" };
        db.PayrollRuns.Add(run);
        for (var i = 1; i <= Rows; i++)
        {
            db.Employees.Add(new Employee { Id = i, TenantId = tenantId, EmployeeCode = $"E{i:D4}", FullName = $"Employee {i}", Status = "Active" });
            db.PayrollSlips.Add(new PayrollSlip
            {
                TenantId = tenantId, RunId = run.Id, EmployeeId = i, EmployeeCode = $"E{i:D4}", EmployeeName = $"Employee {i}",
                BasicSalary = 5_000m, GrossSalary = 5_000m, NetSalary = 4_500m, Status = "Final",
            });
            if (withPayslips)
                db.Payslips.Add(new Payslip { TenantId = tenantId, PayrollRunId = run.Id, EmployeeId = i, PayslipNumber = $"PS-{i:D4}" });
        }
        await db.SaveChangesAsync();
        return (db, tenantId, run.Id);
    }

    private static PagedResult<T> Paged<T>(IActionResult result) => (PagedResult<T>)((OkObjectResult)result).Value!;

    private static void Bind(ControllerBase ctrl, Guid tenantId, params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
        };
    }

    /// <summary>Every HTTP action in the API that takes an integer <c>pageSize</c>.</summary>
    private static IEnumerable<(Type Controller, MethodInfo Method)> PagedActions() =>
        typeof(PayrollController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract && t.Name.EndsWith("Controller"))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes<HttpMethodAttribute>(true).Any())
                .Where(m => m.GetParameters().Any(p => p.Name == "pageSize" && (p.ParameterType == typeof(int) || p.ParameterType == typeof(int?))))
                .Select(m => (t, m)));

    /// <summary>
    /// The source of the action's body: the controller file is found by type name, the method by name with a
    /// <c>pageSize</c> parameter, and the body by brace matching from its opening brace.
    /// </summary>
    private static string? ActionBody(string apiRoot, Type controller, MethodInfo method)
    {
        var file = Directory.EnumerateFiles(Path.Combine(apiRoot, "Controllers"), controller.Name + ".cs", SearchOption.AllDirectories)
            .FirstOrDefault();
        if (file is null) return null;
        var src = File.ReadAllText(file);
        foreach (Match m in Regex.Matches(src, @"public [^\n(]*\b" + Regex.Escape(method.Name) + @"\s*\("))
        {
            var i = m.Index + m.Length;
            var depth = 1;
            while (depth > 0 && i < src.Length)
            {
                if (src[i] == '(') depth++;
                else if (src[i] == ')') depth--;
                i++;
            }
            var parameters = src[(m.Index + m.Length)..(i - 1)];
            if (!Regex.IsMatch(parameters, @"\bint\??\s+pageSize\b")) continue;

            var open = src.IndexOfAny(new[] { '{', ';' }, i);
            if (open < 0) return null;
            if (src[open] == ';' || src.Substring(i, open - i).Contains("=>"))
                return src.Substring(i, Math.Max(0, src.IndexOf(';', i) - i)); // expression-bodied
            depth = 0;
            for (var j = open; j < src.Length; j++)
            {
                if (src[j] == '{') depth++;
                else if (src[j] == '}' && --depth == 0) return src[open..(j + 1)];
            }
        }
        return null;
    }

    private static string ResolveApiRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir?.Parent is not null; i++)
        {
            dir = dir.Parent;
            var candidate = Path.Combine(dir.FullName, "Zayra.Api");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException(
            $"Could not locate the Zayra.Api source root from {AppContext.BaseDirectory}; this guard cannot pass without scanning it.");
    }
}
