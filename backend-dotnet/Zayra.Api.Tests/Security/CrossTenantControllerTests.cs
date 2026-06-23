using System.Net;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Localization;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// P4.1 + P4.2: Controller-level cross-tenant access and IDOR suite.
///
/// CROSS-TENANT: Authenticate as Tenant A and attempt to read or mutate a resource
/// owned by Tenant B. The DbContext is created WITHOUT IHttpContextAccessor so the
/// EF global query filter is inactive (worst-case: the explicit WHERE predicate in
/// each service method is the only line of defence). All attempts must return 404.
///
/// IDOR: Within the same tenant, one employee cannot access another employee's
/// self-service data. The ESS controller's GetEssContextAsync reads employee_id from
/// the JWT claim — the test verifies that a second employee's data is unreachable
/// even when the attacker knows the victim's resource IDs.
/// </summary>
public class CrossTenantControllerTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static EmployeesController EmployeeController(ZayraDbContext db, Guid tenantId, string role = "Admin")
    {
        var controller = new EmployeesController(
            db,
            new Pbkdf2PasswordHasher(),
            new AuditService(db),
            new StubDocumentStorage(),
            new StubNotificationService(),
            new StubHijriDateService(),
            new DataScopeService(db),
            new StubLetterService());
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContext(tenantId, role) };
        return controller;
    }

    private static EmployeeManagementService EmployeeMgmtSvc(ZayraDbContext db) =>
        new(db, new AuditService(db), new StubDocumentStorage(), new StubNotificationService());

    private static EmployeeSelfServiceController EssController(ZayraDbContext db, Guid tenantId, int employeeId)
    {
        var controller = new EmployeeSelfServiceController(db, new StubLetterService(), new Zayra.Api.Infrastructure.Documents.PdfRenderGate(8));
        controller.ControllerContext = new ControllerContext { HttpContext = BuildEssHttpContext(tenantId, employeeId) };
        return controller;
    }

    private static DefaultHttpContext BuildHttpContext(Guid tenantId, string role) =>
        new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("tenant_id", tenantId.ToString()),
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Role, role),
                // DataScopeService.ResolveAsync grants Organization scope when employees.write is present.
                // Without this permission the service returns a restricted scope and Get() returns Forbid.
                new Claim("permission", "employees.write"),
            ], "Test"))
        };

    private static DefaultHttpContext BuildEssHttpContext(Guid tenantId, int employeeId) =>
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

    private static Employee SeedEmployee(ZayraDbContext db, Guid tenantId, string code = "EMP-001")
    {
        var emp = new Employee
        {
            TenantId       = tenantId,
            EmployeeCode   = code,
            FullName       = $"Employee {code}",
            Department     = "HR",
            Designation    = "Officer",
            Status         = "Active",
            JoiningDate    = DateTime.UtcNow.Date,
            Salary         = 50_000m,
            BankIban       = "SA0000000000000000001234",
            PassportNumber = "P99999999",
            IqamaNumber    = "2000000001",
        };
        db.Employees.Add(emp);
        db.SaveChanges();
        return emp;
    }

    // ── P4.1 Cross-tenant: EmployeesController.Get ───────────────────────────────

    [Fact]
    public async Task GetEmployee_AuthenticatedAsTenantA_Returns404ForTenantBEmployee()
    {
        await using var db = CreateDb();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        // Seed TenantB employee — global filter inactive (no accessor) = worst case.
        // The isolation must come from the explicit WHERE tenantId predicate.
        var tenantBEmployee = SeedEmployee(db, tenantB, "B-001");

        var controller = EmployeeController(db, tenantA);
        var svc = EmployeeMgmtSvc(db);
        var result = await controller.Get(tenantBEmployee.Id, svc, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>(
            "TenantA cannot read TenantB's employee record even when global filter is inactive — " +
            "EmployeeManagementService.GetAsync includes explicit WHERE x.TenantId == tenantId");
    }

    [Fact]
    public async Task GetEmployee_AuthenticatedAsTenantA_FindsOwnEmployee()
    {
        await using var db = CreateDb();
        var tenantA = Guid.NewGuid();
        var tenantAEmployee = SeedEmployee(db, tenantA, "A-001");

        var controller = EmployeeController(db, tenantA);
        var svc = EmployeeMgmtSvc(db);
        var result = await controller.Get(tenantAEmployee.Id, svc, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>(
            "TenantA must be able to read its own employee records");
    }

    [Fact]
    public async Task GetEmployee_SequentialIdEnumeration_CannotReadAcrossTenants()
    {
        await using var db = CreateDb();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        SeedEmployee(db, tenantA, "A-001");
        var tenantBSalaryEmployee = SeedEmployee(db, tenantB, "B-SALARY");

        var svc = EmployeeMgmtSvc(db);
        var controller = EmployeeController(db, tenantA);

        // Attacker exploits sequential int IDs to access TenantB's 50,000 salary record
        var result = await controller.Get(tenantBSalaryEmployee.Id, svc, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>(
            "Sequential employee IDs must not be IDOR-exploitable across tenants");
        result.Value.Should().BeNull(
            "response body must be empty — no salary/IBAN/passport data must leak");
    }

    // ── P4.1 Cross-tenant: EmployeeManagementService (service-layer defence) ─────

    [Fact]
    public async Task EmployeeService_GetAsync_TenantA_ReturnsNullForTenantBEmployee()
    {
        await using var db = CreateDb();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var tenantBEmployee = SeedEmployee(db, tenantB, "B-DIRECT");
        var svc = EmployeeMgmtSvc(db);

        var result = await svc.GetAsync(
            tenantId: tenantA,
            id: tenantBEmployee.Id,
            includeSensitive: true,
            context: new RequestContext("127.0.0.1", "test", Guid.NewGuid(), tenantA),
            cancellationToken: CancellationToken.None);

        result.Should().BeNull(
            "GetAsync(tenantA, tenantBEmployee.Id) must return null — not leak salary/IBAN/passport/Iqama");
    }

    private static LeaveController LeaveCtrl(ZayraDbContext db, Guid tenantId)
    {
        var c = new LeaveController(db, new DataScopeService(db));
        c.ControllerContext = new ControllerContext { HttpContext = BuildHttpContext(tenantId, "Admin") };
        return c;
    }

    private static OvertimeController OvertimeCtrl(ZayraDbContext db, Guid tenantId)
    {
        var c = new OvertimeController(db, new DataScopeService(db));
        c.ControllerContext = new ControllerContext { HttpContext = BuildHttpContext(tenantId, "Admin") };
        return c;
    }

    private static LoansController LoanCtrl(ZayraDbContext db, Guid tenantId)
    {
        var c = new LoansController(db, new DataScopeService(db));
        c.ControllerContext = new ControllerContext { HttpContext = BuildHttpContext(tenantId, "Admin") };
        return c;
    }

    private static AdvancesController AdvanceCtrl(ZayraDbContext db, Guid tenantId)
    {
        var c = new AdvancesController(db, new DataScopeService(db));
        c.ControllerContext = new ControllerContext { HttpContext = BuildHttpContext(tenantId, "Admin") };
        return c;
    }

    private static PayrollController PayrollCtrl(ZayraDbContext db, Guid tenantId)
    {
        // IHttpContextAccessor used only for IP address logging; null HttpContext is safe.
        // NullPackResolver (zero statutory deductions) is correct for cross-tenant isolation tests —
        // we're checking data ownership, not deduction accuracy.
        var c = new PayrollController(db, new DataScopeService(db), new HttpContextAccessor(), new StubNotificationService(), new StubPackResolver(), new StubRuleReader(), new StubLetterService(), new StubDocumentStorage(), new Zayra.Api.Infrastructure.Documents.PdfRenderGate(8));
        c.ControllerContext = new ControllerContext { HttpContext = BuildHttpContext(tenantId, "Admin") };
        return c;
    }

    // ── P4.1 Extended: data-driven coverage for all remaining tenant-scoped controllers ─

    /// <summary>
    /// Registry of all tenant-scoped controller/service endpoints under cross-tenant isolation test.
    /// To cover a new controller: add an entry here and a corresponding case block in
    /// <see cref="CrossTenant_ControllerOrService_BlocksCrossTenantAccess"/>.
    /// </summary>
    public static TheoryData<string> AllTenantIsolationCases { get; } = new()
    {
        "LeaveController.Approve",
        "OvertimeController.Approve",
        "LoansController.GetLoan",
        "AdvancesController.Get",
        "AttendanceService.GetDevice",
        "PayrollController.Slips",
    };

    [Theory]
    [MemberData(nameof(AllTenantIsolationCases))]
    public async Task CrossTenant_ControllerOrService_BlocksCrossTenantAccess(string scenario)
    {
        await using var db = CreateDb();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        switch (scenario)
        {
            case "LeaveController.Approve":
            {
                var leave = new LeaveRequest
                {
                    TenantId = tenantB, EmployeeId = 99,
                    StartDate = DateOnly.FromDateTime(DateTime.UtcNow),
                    EndDate = DateOnly.FromDateTime(DateTime.UtcNow),
                    TotalDays = 1, Status = "Submitted",
                };
                db.LeaveRequests.Add(leave);
                await db.SaveChangesAsync();

                var result = await LeaveCtrl(db, tenantA).Approve(leave.Id, CancellationToken.None);
                result.Should().BeOfType<NotFoundResult>(
                    "TenantA cannot approve TenantB's leave — WHERE l.TenantId == tenantId returns null");
                break;
            }

            case "OvertimeController.Approve":
            {
                var ot = new OvertimeRequest
                {
                    TenantId = tenantB, EmployeeId = 99,
                    WorkDate = DateOnly.FromDateTime(DateTime.UtcNow),
                    StartTimeUtc = DateTime.UtcNow.AddHours(-2),
                    EndTimeUtc = DateTime.UtcNow,
                    RequestedMinutes = 120, Status = "PendingManager",
                };
                db.OvertimeRequests.Add(ot);
                await db.SaveChangesAsync();

                var result = await OvertimeCtrl(db, tenantA).Approve(ot.Id, new OvertimeDecisionRequest(120, null), CancellationToken.None);
                result.Should().BeOfType<NotFoundResult>(
                    "TenantA cannot approve TenantB's overtime — WHERE x.TenantId == tenantId returns null");
                break;
            }

            case "LoansController.GetLoan":
            {
                var loan = new EmployeeLoan { TenantId = tenantB };
                db.EmployeeLoans.Add(loan);
                await db.SaveChangesAsync();

                var result = await LoanCtrl(db, tenantA).GetLoan(loan.Id, CancellationToken.None);
                result.Should().BeOfType<NotFoundResult>(
                    "TenantA cannot read TenantB's employee loan — WHERE x.TenantId == tid returns null");
                break;
            }

            case "AdvancesController.Get":
            {
                var adv = new SalaryAdvance { TenantId = tenantB };
                db.SalaryAdvances.Add(adv);
                await db.SaveChangesAsync();

                var result = await AdvanceCtrl(db, tenantA).Get(adv.Id, CancellationToken.None);
                result.Should().BeOfType<NotFoundResult>(
                    "TenantA cannot read TenantB's salary advance — WHERE x.TenantId == tid returns null");
                break;
            }

            case "AttendanceService.GetDevice":
            {
                var device = new AttendanceDevice { TenantId = tenantB };
                db.AttendanceDevices.Add(device);
                await db.SaveChangesAsync();

                var svc = new AttendanceService(db, new StubNotificationService(), new StubHttpClientFactory());
                var result = await svc.GetDeviceAsync(tenantA, device.Id, CancellationToken.None);
                result.Should().BeNull(
                    "GetDeviceAsync(tenantA, tenantBDeviceId) must return null — WHERE TenantId == tenantA excludes TenantB records");
                break;
            }

            case "PayrollController.Slips":
            {
                var run = new PayrollRun { TenantId = tenantB, Year = 2026, Month = 6 };
                db.PayrollRuns.Add(run);
                db.PayrollSlips.Add(new PayrollSlip
                {
                    TenantId = tenantB, RunId = run.Id, EmployeeId = 99,
                    EmployeeCode = "B-001", EmployeeName = "TenantB Employee",
                    BasicSalary = 15_000m, NetSalary = 15_000m, Status = "Draft",
                });
                await db.SaveChangesAsync();

                var result = await PayrollCtrl(db, tenantA).Slips(run.Id, 1, 50, CancellationToken.None);
                var ok = result.Should().BeOfType<OkObjectResult>().Subject;
                var total = (int?)ok.Value!.GetType().GetProperty("Total")?.GetValue(ok.Value);
                total.Should().Be(0,
                    "TenantA must see zero payslips for a TenantB run — WHERE s.TenantId == tenantId filters them out");
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), $"Unknown scenario '{scenario}'. Add a case block.");
        }
    }

    // ── P4.2 IDOR: ESS — Employee A cannot read Employee B's payslips ────────────

    [Fact]
    public async Task EssPayslips_EmployeeA_CannotReadEmployeeBPayslips()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();

        var empA = SeedEmployee(db, tenantId, "A-001");
        var empB = SeedEmployee(db, tenantId, "B-001");

        // Seed finalised payslips for both employees
        db.PayrollSlips.AddRange(
            new PayrollSlip
            {
                TenantId = tenantId, EmployeeId = empA.Id, RunId = Guid.NewGuid(),
                EmployeeCode = "A-001", EmployeeName = "Employee A",
                BasicSalary = 10_000m, NetSalary = 10_000m, Status = "Final"
            },
            new PayrollSlip
            {
                TenantId = tenantId, EmployeeId = empB.Id, RunId = Guid.NewGuid(),
                EmployeeCode = "B-001", EmployeeName = "Employee B",
                BasicSalary = 99_000m, NetSalary = 99_000m, Status = "Final"
            });
        await db.SaveChangesAsync();

        // Employee A's ESS controller — jwt contains employee_id = empA.Id
        var controller = EssController(db, tenantId, empA.Id);
        var result = await controller.Payslips(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payslips = (ok.Value as IEnumerable<object>)?.ToList();
        payslips.Should().NotBeNull();

        var employeeIds = payslips!
            .Select(p => (int?)p.GetType().GetProperty("EmployeeId")?.GetValue(p))
            .ToList();

        employeeIds.Should().AllSatisfy(id =>
            id.Should().Be(empA.Id, "ESS payslip list must contain only the requesting employee's payslips"));
        employeeIds.Should().NotContain(empB.Id,
            "Employee A must not see Employee B's payslips — EmployeeB has salary 99,000 which must remain private");
    }

    [Fact]
    public async Task EssPayslips_EmployeeA_DoesNotLeakHighSalaryOfEmployeeB()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();

        var empA = SeedEmployee(db, tenantId, "A-LOW");
        var empB = SeedEmployee(db, tenantId, "B-HIGH");

        // Employee B has a 250,000 payslip; Employee A has none
        db.PayrollSlips.Add(new PayrollSlip
        {
            TenantId = tenantId, EmployeeId = empB.Id, RunId = Guid.NewGuid(),
            EmployeeCode = "B-HIGH", EmployeeName = "Employee B",
            BasicSalary = 250_000m, NetSalary = 250_000m, Status = "Final"
        });
        await db.SaveChangesAsync();

        var controller = EssController(db, tenantId, empA.Id);
        var result = await controller.Payslips(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payslips = (ok.Value as IEnumerable<object>)?.ToList();

        // empA has no payslips — list must be empty, not contain empB's 250k payslip
        payslips.Should().NotBeNull().And.BeEmpty(
            "Employee A has no payslips; the 250,000 payslip belongs to Employee B and must not appear");
    }

    // ── P4.2 IDOR: ESS — Employee A cannot read Employee B's HR requests ─────────

    [Fact]
    public async Task EssHrRequests_EmployeeA_CannotReadEmployeeBRequests()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var empA = SeedEmployee(db, tenantId, "A-HR");
        var empB = SeedEmployee(db, tenantId, "B-HR");

        db.HRRequests.AddRange(
            new HRRequest
            {
                TenantId = tenantId, EmployeeId = empA.Id,
                Subject = "A's leave extension request", Status = "Open"
            },
            new HRRequest
            {
                TenantId = tenantId, EmployeeId = empB.Id,
                Subject = "B's confidential grievance", Status = "Open"
            });
        await db.SaveChangesAsync();

        var controller = EssController(db, tenantId, empA.Id);
        var result = await controller.MyHrRequests(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var requests = (ok.Value as IEnumerable<object>)?.ToList();
        requests.Should().NotBeNull();

        var subjects = requests!
            .Select(r => r.GetType().GetProperty("Subject")?.GetValue(r) as string)
            .ToList();

        subjects.Should().NotContain("B's confidential grievance",
            "Employee A must never see Employee B's HR requests via the ESS endpoint");
        subjects.Should().Contain("A's leave extension request",
            "Employee A must see their own HR requests");
    }

    // ── Stubs ─────────────────────────────────────────────────────────────────────

    private sealed class StubDocumentStorage : IDocumentStorage
    {
        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) =>
            Task.FromResult(new StoredDocument(file.FileName, file.ContentType, "storage/test", "/tmp/test"));
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public string ResolvePath(string storageUrl) => "/tmp/test";
    }

    private sealed class StubNotificationService : INotificationService
    {
        public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string entity, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class StubHijriDateService : IHijriDateService
    {
        public DateConversionDto FromGregorian(DateOnly date) =>
            new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
    }

    private sealed class StubLetterService : ILetterService
    {
        public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }

    // Zero-deduction resolver — correct for cross-tenant isolation tests (data ownership,
    // not deduction accuracy).
    private sealed class StubPackResolver : ICountryPackResolver
    {
        public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) => new DefaultStatutoryDeductionCalculator();
        public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
        public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
        public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
        public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
        public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
    }
}
