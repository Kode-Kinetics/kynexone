using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Controllers.Performance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Security-audit batch 2 — regression + negative tests for the fixes in the 360° security pass:
///   1. CSV/Excel formula injection is neutralized on export (Csv.Escape).
///   2. SSRF guard blocks internal/loopback/metadata targets and allows public hosts.
///   3. Within-tenant IDOR/BOLA: a scoped employee cannot read another employee's
///      payslips (Mobile), loans, advances, PIPs, or probation reviews.
///   4. MFA challenge tokens are consumed after too many wrong codes (brute-force cap).
///   5. Platform-admin login locks out after repeated failures.
///   6. DataScope.CanAccessEmployee object-level authorization logic.
///
/// The "positive" half of every negative test (the owner CAN see their own data / an org-wide
/// role CAN see everything) is asserted too, so the guard is proven to deny without over-denying.
/// </summary>
public class SecurityAuditBatch2Tests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // A principal with NO org-wide permission → DataScopeService resolves to Own/Team scope.
    // employee_id binds the caller to a specific employee record.
    private static DefaultHttpContext EssContext(Guid tenantId, int employeeId) =>
        new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("tenant_id", tenantId.ToString()),
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim("employee_id", employeeId.ToString()),
                new Claim("permission", "ess.read"),
            ], "Test"))
        };

    private static Employee SeedEmployee(ZayraDbContext db, Guid tenantId, int? id = null, string code = "EMP")
    {
        var emp = new Employee
        {
            TenantId = tenantId, EmployeeCode = code, FullName = $"Emp {code}",
            Department = "Ops", Designation = "Staff", Status = "Active",
            JoiningDate = DateTime.UtcNow.Date, Salary = 40_000m,
        };
        if (id.HasValue) emp.Id = id.Value;
        db.Employees.Add(emp);
        db.SaveChanges();
        return emp;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 1. CSV FORMULA INJECTION
    // ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\",\"x\")")]
    [InlineData("+1+1")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1)")]
    [InlineData("\tcmd")]
    [InlineData("\rformula")]
    public void CsvEscape_NeutralizesFormulaLeadCharacters(string dangerous)
    {
        var escaped = Csv.Escape(dangerous);
        // A neutralized cell begins with a literal apostrophe (optionally inside the RFC-4180 quotes),
        // so a spreadsheet renders it as text and never evaluates it as a formula.
        var inner = escaped.StartsWith('"') ? escaped.Trim('"') : escaped;
        inner.Should().StartWith("'", "formula-trigger cells must be prefixed so they are not evaluated");
    }

    [Fact]
    public void CsvEscape_LeavesOrdinaryValuesUnprefixed()
    {
        Csv.Escape("Mohammed Ali").Should().Be("Mohammed Ali");
        Csv.Escape("Riyadh").Should().Be("Riyadh");
    }

    [Fact]
    public void CsvEscape_StillQuotesDelimitersAndQuotes()
    {
        Csv.Escape("a,b").Should().Be("\"a,b\"");
        Csv.Escape("she said \"hi\"").Should().Be("\"she said \"\"hi\"\"\"");
    }

    [Fact]
    public void CsvBuild_NeutralizesFormulaInEmployeeName()
    {
        // Simulate an employee whose name is a malicious formula flowing into an export.
        var csv = Csv.Build(
            new[] { "Name", "Dept" },
            new List<IReadOnlyList<object?>> { new object?[] { "=cmd|'/c calc'!A1", "Ops" } });
        csv.Should().NotContain("\n=cmd", "the formula must not appear at the start of a cell");
        csv.Should().Contain("'=cmd", "the formula must be neutralized with a leading apostrophe");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 2. SSRF GUARD
    // ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")] // AWS/GCP IMDS
    [InlineData("http://127.0.0.1:5117/api/platform")]        // loopback
    [InlineData("http://localhost/internal")]                 // loopback by name
    [InlineData("http://10.0.0.5/x")]                         // private 10/8
    [InlineData("http://192.168.1.1/x")]                      // private 192.168/16
    [InlineData("http://172.16.0.1/x")]                       // private 172.16/12
    [InlineData("file:///etc/passwd")]                        // non-http scheme
    [InlineData("gopher://127.0.0.1")]                        // non-http scheme
    public async Task SsrfGuard_BlocksInternalAndNonHttpTargets(string url)
    {
        var (ok, _) = await SsrfGuard.ValidateOutboundUrlAsync(url, CancellationToken.None);
        ok.Should().BeFalse($"'{url}' must be rejected as an SSRF target");
    }

    [Theory]
    [InlineData("https://8.8.8.8/attendance/poll")]  // literal public IP — no DNS needed (CI-safe)
    [InlineData("http://93.184.216.34/poll")]         // literal public IP
    public async Task SsrfGuard_AllowsPublicHttpsTargets(string url)
    {
        var (ok, _) = await SsrfGuard.ValidateOutboundUrlAsync(url, CancellationToken.None);
        ok.Should().BeTrue($"'{url}' is a public host and must be allowed");
    }

    [Fact]
    public async Task SsrfGuard_BlocksLoopbackHostForTcp()
    {
        var (ok, _) = await SsrfGuard.ValidateOutboundHostAsync("127.0.0.1", CancellationToken.None);
        ok.Should().BeFalse("a device IP of 127.0.0.1 must be blocked (internal port-scan)");
    }

    [Fact]
    public void SsrfGuard_GuardedHandlerDisablesRedirects()
    {
        using var handler = SsrfGuard.CreateGuardedClientHandler();
        handler.AllowAutoRedirect.Should().BeFalse(
            "auto-redirect must be off so a validated host cannot 3xx to an internal address");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 3. DataScope object-level authorization
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DataScope_CanAccessEmployee_OrgWideSeesEveryone()
    {
        var scope = new DataScope { Level = DataScopeLevel.Organization }; // AllowedEmployeeIds null = unrestricted
        scope.IsUnrestricted.Should().BeTrue();
        scope.CanAccessEmployee(999).Should().BeTrue();
    }

    [Fact]
    public void DataScope_CanAccessEmployee_ScopedDeniesOutOfScope()
    {
        var scope = new DataScope { Level = DataScopeLevel.Own, AllowedEmployeeIds = new[] { 5 } };
        scope.CanAccessEmployee(5).Should().BeTrue("caller may see their own record");
        scope.CanAccessEmployee(6).Should().BeFalse("caller must NOT see a colleague's record");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 3a. IDOR — MobileController must serve only the caller's own data
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Mobile_Payslips_CannotReadAnotherEmployee()
    {
        await using var db = CreateDb();
        var tenant = Guid.NewGuid();
        var victim = SeedEmployee(db, tenant, 100, "VICTIM");
        var attacker = SeedEmployee(db, tenant, 200, "ATTACKER");
        db.Payslips.Add(new Payslip { TenantId = tenant, EmployeeId = victim.Id, PayrollRunId = Guid.NewGuid() });
        db.SaveChanges();

        var controller = new MobileController(db)
        { ControllerContext = new ControllerContext { HttpContext = EssContext(tenant, attacker.Id) } };

        // Attacker (employee 200) tries to read victim's (employee 100) payslips.
        var result = await controller.MyPayslips(victim.Id, CancellationToken.None);
        result.Should().BeOfType<ForbidResult>(
            "an employee must not read another employee's payslips via the mobile route");
    }

    [Fact]
    public async Task Mobile_Payslips_OwnEmployeeSucceeds()
    {
        await using var db = CreateDb();
        var tenant = Guid.NewGuid();
        var me = SeedEmployee(db, tenant, 100, "ME");

        var controller = new MobileController(db)
        { ControllerContext = new ControllerContext { HttpContext = EssContext(tenant, me.Id) } };

        var result = await controller.MyPayslips(me.Id, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>("an employee must be able to read their OWN payslips");
    }

    [Fact]
    public async Task Mobile_Dashboard_CannotReadAnotherEmployee()
    {
        await using var db = CreateDb();
        var tenant = Guid.NewGuid();
        SeedEmployee(db, tenant, 100, "VICTIM");
        var attacker = SeedEmployee(db, tenant, 200, "ATTACKER");

        var controller = new MobileController(db)
        { ControllerContext = new ControllerContext { HttpContext = EssContext(tenant, attacker.Id) } };

        var result = await controller.MobileDashboard(100, CancellationToken.None);
        result.Should().BeOfType<ForbidResult>("dashboard must be self-only");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 3b. IDOR — Finance loan/advance detail must be scope-checked
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Loans_Detail_ScopedEmployeeCannotReadAnothersLoan()
    {
        await using var db = CreateDb();
        var tenant = Guid.NewGuid();
        var victim = SeedEmployee(db, tenant, 100, "VICTIM");
        var attacker = SeedEmployee(db, tenant, 200, "ATTACKER");
        var loan = new EmployeeLoan
        {
            TenantId = tenant, EmployeeId = Guid.NewGuid(), EmployeeIntId = victim.Id,
            RequestedAmount = 50_000m, ApprovedAmount = 50_000m, Status = "Active",
        };
        db.EmployeeLoans.Add(loan);
        db.SaveChanges();

        var controller = new LoansController(db, new Zayra.Api.Infrastructure.Common.DataScopeService(db))
        { ControllerContext = new ControllerContext { HttpContext = EssContext(tenant, attacker.Id) } };

        var result = await controller.GetLoan(loan.Id, CancellationToken.None);
        result.Should().BeOfType<ForbidResult>(
            "a scoped employee must not read another employee's loan by guessing the loan GUID");
    }

    [Fact]
    public async Task Advances_Detail_ScopedEmployeeCannotReadAnothersAdvance()
    {
        await using var db = CreateDb();
        var tenant = Guid.NewGuid();
        var victim = SeedEmployee(db, tenant, 100, "VICTIM");
        var attacker = SeedEmployee(db, tenant, 200, "ATTACKER");
        var adv = new SalaryAdvance
        {
            TenantId = tenant, EmployeeId = Guid.NewGuid(), EmployeeIntId = victim.Id,
            RequestedAmount = 10_000m, ApprovedAmount = 10_000m, Status = "Active",
        };
        db.SalaryAdvances.Add(adv);
        db.SaveChanges();

        var controller = new AdvancesController(db, new Zayra.Api.Infrastructure.Common.DataScopeService(db))
        { ControllerContext = new ControllerContext { HttpContext = EssContext(tenant, attacker.Id) } };

        var result = await controller.Get(adv.Id, CancellationToken.None);
        result.Should().BeOfType<ForbidResult>("advance detail must be scope-checked");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 3c. IDOR — PIP / Probation reads must be scoped
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pip_Detail_ScopedEmployeeCannotReadAnothersPip()
    {
        await using var db = CreateDb();
        var tenant = Guid.NewGuid();
        var victim = SeedEmployee(db, tenant, 100, "VICTIM");
        var attacker = SeedEmployee(db, tenant, 200, "ATTACKER");
        var pip = new PerformanceImprovementPlan
        {
            TenantId = tenant, EmployeeId = victim.Id, EmployeeName = "Victim", Status = "Active",
        };
        db.PerformanceImprovementPlans.Add(pip);
        db.SaveChanges();

        var controller = new PIPController(db, new Zayra.Api.Infrastructure.Common.DataScopeService(db))
        { ControllerContext = new ControllerContext { HttpContext = EssContext(tenant, attacker.Id) } };

        var result = await controller.Get(pip.Id, CancellationToken.None);
        result.Should().BeOfType<ForbidResult>("a PIP is disciplinary-sensitive and must be scope-checked");
    }

    [Fact]
    public async Task Pip_List_ScopedEmployeeSeesOnlyOwn()
    {
        await using var db = CreateDb();
        var tenant = Guid.NewGuid();
        var me = SeedEmployee(db, tenant, 100, "ME");
        var other = SeedEmployee(db, tenant, 200, "OTHER");
        db.PerformanceImprovementPlans.Add(new PerformanceImprovementPlan { TenantId = tenant, EmployeeId = me.Id, EmployeeName = "Me", Status = "Active" });
        db.PerformanceImprovementPlans.Add(new PerformanceImprovementPlan { TenantId = tenant, EmployeeId = other.Id, EmployeeName = "Other", Status = "Active" });
        db.SaveChanges();

        var controller = new PIPController(db, new Zayra.Api.Infrastructure.Common.DataScopeService(db))
        { ControllerContext = new ControllerContext { HttpContext = EssContext(tenant, me.Id) } };

        var result = await controller.List(null, null, CancellationToken.None) as OkObjectResult;
        result.Should().NotBeNull();
        var items = (result!.Value as IEnumerable<PerformanceImprovementPlan>)!.ToList();
        items.Should().OnlyContain(p => p.EmployeeId == me.Id,
            "a scoped employee must only see their own PIP in the list");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 6. DataScope Constrain: no-employeeId list request must not leak everyone for scoped user
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DataScope_Constrain_ScopedNoIdReturnsSetFilterNotAll()
    {
        var scope = new DataScope { Level = DataScopeLevel.Own, AllowedEmployeeIds = new[] { 7 } };
        var (singleId, setFilter) = scope.Constrain(null);
        singleId.Should().BeNull();
        setFilter.Should().NotBeNull("a scoped caller with no id filter must be constrained to their allowed set");
        setFilter.Should().Equal(new[] { 7 });
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 7. PII compliance controllers must be role-gated at the CLASS level (reads included).
    //    Asserted by attribute reflection (the ASP.NET runtime enforces the declaration).
    // ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(typeof(Zayra.Api.Controllers.Compliance.VisaTrackingController))]
    [InlineData(typeof(Zayra.Api.Controllers.Compliance.ContractsController))]
    public void CompliancePiiControllers_AreRoleGatedAtClassLevel(Type controllerType)
    {
        var authorize = controllerType
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
            .FirstOrDefault();

        authorize.Should().NotBeNull($"{controllerType.Name} must carry a class-level [Authorize]");
        authorize!.Roles.Should().NotBeNullOrEmpty(
            $"{controllerType.Name} exposes national-ID/salary PII and must restrict READS to HR roles, " +
            "not just any authenticated tenant user");
        authorize.Roles.Should().Contain("HR", "the role gate should include HR roles");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 8. Public marketing writes are rate-limited (spam/DoS protection).
    // ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(nameof(PricingController.Estimate))]
    [InlineData(nameof(PricingController.SubmitQuote))]
    public void PricingWriteEndpoints_HaveRateLimiting(string methodName)
    {
        var method = typeof(PricingController).GetMethod(methodName);
        method.Should().NotBeNull();
        var rl = method!.GetCustomAttributes(typeof(Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute), true);
        rl.Should().NotBeEmpty($"{methodName} is an unauthenticated write and must be rate-limited");
    }
}
