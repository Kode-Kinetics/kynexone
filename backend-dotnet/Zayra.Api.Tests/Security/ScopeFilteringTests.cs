using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// P4.4: Scope-filtering tests.
///
/// Verifies that endpoints which aggregate cross-employee data are correctly restricted:
/// - AttendanceController.Insights must require HR-level roles (not open to all employees)
/// - ESS endpoints must be scoped to the requesting employee's ID from the JWT claim
///
/// Role-gate tests reflect on the [Authorize(Roles="...")] attribute rather than going
/// through middleware (which is not active in unit tests). This is the correct approach:
/// we assert the declaration is present, which is what the ASP.NET runtime enforces.
/// </summary>
public class ScopeFilteringTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static EmployeeSelfServiceController EssController(ZayraDbContext db, Guid tenantId, int employeeId)
    {
        var controller = new EmployeeSelfServiceController(db, new StubLetterService(), new Zayra.Api.Infrastructure.Documents.PdfRenderGate(1));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("tenant_id", tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim("employee_id", employeeId.ToString()),
                    new Claim("permission", "ess.read"),
                ], "Test"))
            }
        };
        return controller;
    }

    private static Employee SeedEmployee(ZayraDbContext db, Guid tenantId, string code)
    {
        var emp = new Employee
        {
            TenantId = tenantId, EmployeeCode = code, FullName = $"Employee {code}",
            Department = "HR", Designation = "Officer", Status = "Active",
            JoiningDate = DateTime.UtcNow.Date, Salary = 50_000m,
        };
        db.Employees.Add(emp);
        db.SaveChanges();
        return emp;
    }

    // ── P4.4 Scope: Insights endpoint RBAC gate (attribute inspection) ────────────

    [Fact]
    public void Insights_EndpointHasRoleRestrictedAuthorizeAttribute()
    {
        var method = typeof(AttendanceController)
            .GetMethod("Insights", BindingFlags.Public | BindingFlags.Instance);
        method.Should().NotBeNull("AttendanceController.Insights must exist");

        var attr = method!.GetCustomAttribute<AuthorizeAttribute>();
        attr.Should().NotBeNull(
            "Insights aggregates all-employee attendance anomalies — it must be " +
            "restricted with [Authorize(Roles=...)] so employees cannot see other employees' insights");

        attr!.Roles.Should().NotBeNullOrEmpty(
            "Authorize attribute must specify roles, not just require authentication");
    }

    [Fact]
    public void Insights_EndpointRoles_MustIncludeHrManagerButNotEmployee()
    {
        var method = typeof(AttendanceController)
            .GetMethod("Insights", BindingFlags.Public | BindingFlags.Instance);
        var attr = method!.GetCustomAttribute<AuthorizeAttribute>();
        attr.Should().NotBeNull();

        var roles = attr!.Roles!.Split(',').Select(r => r.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        roles.Should().Contain("HR Manager",
            "HR Manager must be able to review cross-employee attendance insights");
        roles.Should().Contain("Admin",
            "Admin must have access to all insights");
        roles.Should().NotContain("Employee",
            "Employee role must NOT be in the Insights allow-list — insights expose data about other employees");
    }

    // ── P4.4 Scope: Cross-endpoint role gate inventory ────────────────────────────

    [Theory]
    [InlineData("Devices", "Admin,HR Manager,HR Officer,Auditor")]
    [InlineData("CreateDevice", "Admin,HR Manager,HR Officer")]
    public void AttendanceController_RoleSensitiveMethods_HaveExpectedRoleGates(string methodName, string expectedRoles)
    {
        var method = typeof(AttendanceController)
            .GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
        method.Should().NotBeNull($"method {methodName} must exist on AttendanceController");

        var attr = method!.GetCustomAttribute<AuthorizeAttribute>();
        attr.Should().NotBeNull($"{methodName} must have an [Authorize(Roles=...)] attribute");

        var declared = attr!.Roles!.Split(',').Select(r => r.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expected = expectedRoles.Split(',').Select(r => r.Trim());

        foreach (var role in expected)
        {
            declared.Should().Contain(role, $"{methodName} must list '{role}' in its Roles");
        }
    }

    // ── P4.4 Scope: ESS attendance scoped to requesting employee ─────────────────

    [Fact]
    public async Task EssAttendance_EmployeeA_CannotSeeEmployeeBAttendanceDays()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var empA = SeedEmployee(db, tenantId, "A-ATT");
        var empB = SeedEmployee(db, tenantId, "B-ATT");

        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        db.AttendanceDailyRecords.AddRange(
            new AttendanceDailyRecord
            {
                TenantId = tenantId, EmployeeId = empA.Id,
                WorkDate = today, Status = "Present", TotalWorkedMinutes = 480,
            },
            new AttendanceDailyRecord
            {
                TenantId = tenantId, EmployeeId = empB.Id,
                WorkDate = today, Status = "Present", TotalWorkedMinutes = 480,
            });
        await db.SaveChangesAsync();

        var controller = EssController(db, tenantId, empA.Id);
        var result = await controller.Attendance(
            from: today.AddDays(-30), to: today, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var records = (ok.Value as IEnumerable<object>)?.ToList();
        records.Should().NotBeNull();

        var employeeIds = records!
            .Select(r => (int?)r.GetType().GetProperty("EmployeeId")?.GetValue(r))
            .ToList();

        employeeIds.Should().NotContain(empB.Id,
            "ESS attendance must be scoped to the JWT employee_id claim — Employee B's records must not appear");
        employeeIds.Should().AllSatisfy(id => id.Should().Be(empA.Id,
            "every attendance record in the ESS response must belong to Employee A"));
    }

    // ── P4.4 Scope: ESS payslips scoped to requesting employee ───────────────────

    [Fact]
    public async Task EssPayslips_ScopeFilter_EmployeeA_CannotSeeEmployeeBPayslips()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var empA = SeedEmployee(db, tenantId, "A-PAY");
        var empB = SeedEmployee(db, tenantId, "B-PAY");

        db.PayrollSlips.AddRange(
            new PayrollSlip
            {
                TenantId = tenantId, EmployeeId = empA.Id, RunId = Guid.NewGuid(),
                EmployeeCode = "A-PAY", EmployeeName = "Employee A",
                BasicSalary = 5_000m, NetSalary = 5_000m, Status = "Final"
            },
            new PayrollSlip
            {
                TenantId = tenantId, EmployeeId = empB.Id, RunId = Guid.NewGuid(),
                EmployeeCode = "B-PAY", EmployeeName = "Employee B",
                BasicSalary = 200_000m, NetSalary = 200_000m, Status = "Final"
            });
        await db.SaveChangesAsync();

        var controller = EssController(db, tenantId, empA.Id);
        var result = await controller.Payslips(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var slips = (ok.Value as IEnumerable<object>)?.ToList();
        slips.Should().NotBeNull();

        var netSalaries = slips!
            .Select(s => (decimal?)s.GetType().GetProperty("NetSalary")?.GetValue(s))
            .ToList();

        netSalaries.Should().NotContain(200_000m,
            "Employee B's 200,000 NetSalary payslip must not appear in Employee A's ESS payslip list");
    }

    // ── P4.4 Scope: ESS HR requests scoped to requesting employee ────────────────

    [Fact]
    public async Task EssHrRequests_ScopeFilter_EmployeeA_CannotSeeEmployeeBRequests()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var empA = SeedEmployee(db, tenantId, "A-HR2");
        var empB = SeedEmployee(db, tenantId, "B-HR2");

        db.HRRequests.AddRange(
            new HRRequest
            {
                TenantId = tenantId, EmployeeId = empA.Id,
                Subject = "A scope test request", Status = "Open"
            },
            new HRRequest
            {
                TenantId = tenantId, EmployeeId = empB.Id,
                Subject = "B scope test confidential", Status = "Open"
            });
        await db.SaveChangesAsync();

        var controller = EssController(db, tenantId, empA.Id);
        var result = await controller.MyHrRequests(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var requests = (ok.Value as IEnumerable<object>)?.ToList();
        requests.Should().NotBeNull();

        var subjects = requests!
            .Select(r => r.GetType().GetProperty("Subject")?.GetValue(r) as string)
            .ToList();

        subjects.Should().NotContain("B scope test confidential",
            "ESS MyHrRequests must only return the requesting employee's requests — Employee B's must not appear");
    }

    // ── Stubs ─────────────────────────────────────────────────────────────────────

    private sealed class StubLetterService : ILetterService
    {
        public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }
}
