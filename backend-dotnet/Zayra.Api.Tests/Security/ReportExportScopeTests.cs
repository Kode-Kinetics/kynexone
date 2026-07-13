using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Reports;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Localization;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

[Trait("Category", "Integration")]
[Collection("Integration")]
public class ReportExportScopeTests
{
    private readonly PostgresFixture _fx;
    public ReportExportScopeTests(PostgresFixture fx) => _fx = fx;

    private sealed record World(Guid TenantId, Guid CompanyA, Guid CompanyB, int EmpA, int EmpB, Guid RunId);

    [Fact]
    public async Task ReportsRun_PayrollRegister_ExcludesSiblingCompanyRows()
    {
        var w = await SeedWorld();
        await using var db = _fx.CreateDbWithAccessor(Accessor(ScopedHr(w.TenantId, w.CompanyA)));
        var controller = new ReportsController(db, new DataScopeService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = ScopedHr(w.TenantId, w.CompanyA) } }
        };

        var result = await controller.RunReport(new RunReportRequest("payroll.register", null), CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("Alpha Employee");
        json.Should().NotContain("Beta Employee");
        json.Should().NotContain("9500");
    }

    [Fact]
    public async Task EmployeeExport_ExcludesSiblingCompanyPayrollAndBankRows()
    {
        var w = await SeedWorld();
        await using var db = _fx.CreateDbWithAccessor(Accessor(ScopedHr(w.TenantId, w.CompanyA)));
        var controller = CreateEmployeesController(db, ScopedHr(w.TenantId, w.CompanyA));

        var result = await controller.Export(CancellationToken.None);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        var csv = Encoding.UTF8.GetString(file.FileContents);
        csv.Should().Contain("EA-001");
        csv.Should().Contain("Alpha Employee");
        csv.Should().NotContain("EB-001");
        csv.Should().NotContain("Beta Employee");
        csv.Should().NotContain("SA-BETA-IBAN");
    }

    [Fact]
    public async Task EmployeeUpdate_RejectsSiblingCompanyEmployee()
    {
        var w = await SeedWorld();
        await using var db = _fx.CreateDbWithAccessor(Accessor(ScopedHr(w.TenantId, w.CompanyA)));
        var controller = CreateEmployeesController(db, ScopedHr(w.TenantId, w.CompanyA));
        var changes = new Dictionary<string, JsonElement>
        {
            ["phone"] = JsonSerializer.Deserialize<JsonElement>("\"+971555000\"")
        };

        var result = await controller.UpdateEmployee(w.EmpB, new EmployeeUpdateRequest(DateOnly.FromDateTime(DateTime.UtcNow), changes), CancellationToken.None);

        (result is ForbidResult or NotFoundResult).Should().BeTrue(
            "sibling-company updates must fail closed as either explicit forbid or hidden-row 404");
    }

    private async Task<World> SeedWorld()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var companyA = new Company { TenantId = tenantId, LegalNameEn = "Report A", RegistrationNumber = $"RA-{Guid.NewGuid():N}", IsActive = true };
        var companyB = new Company { TenantId = tenantId, LegalNameEn = "Report B", RegistrationNumber = $"RB-{Guid.NewGuid():N}", IsActive = true };
        db.Companies.AddRange(companyA, companyB);
        await db.SaveChangesAsync();

        var empA = new Employee { TenantId = tenantId, CompanyId = companyA.Id, EmployeeCode = "EA-001", FullName = "Alpha Employee", Status = "Active", JoiningDate = DateTime.UtcNow, Department = "HR", WorkEmail = "alpha@example.test", BankIban = "SA-ALPHA-IBAN" };
        var empB = new Employee { TenantId = tenantId, CompanyId = companyB.Id, EmployeeCode = "EB-001", FullName = "Beta Employee", Status = "Active", JoiningDate = DateTime.UtcNow, Department = "HR", WorkEmail = "beta@example.test", BankIban = "SA-BETA-IBAN" };
        db.Employees.AddRange(empA, empB);
        await db.SaveChangesAsync();

        var run = new PayrollRun { TenantId = tenantId, CompanyId = companyA.Id, Year = 2026, Month = 7, Status = "Approved" };
        db.PayrollRuns.Add(run);
        db.PayrollSlips.AddRange(
            new PayrollSlip { TenantId = tenantId, CompanyId = companyA.Id, RunId = run.Id, EmployeeId = empA.Id, EmployeeCode = empA.EmployeeCode, EmployeeName = empA.FullName, Department = "HR", BasicSalary = 5000, GrossSalary = 6000, NetSalary = 5800, Status = "Approved" },
            new PayrollSlip { TenantId = tenantId, CompanyId = companyB.Id, RunId = run.Id, EmployeeId = empB.Id, EmployeeCode = empB.EmployeeCode, EmployeeName = empB.FullName, Department = "HR", BasicSalary = 9500, GrossSalary = 10000, NetSalary = 9700, Status = "Approved" });
        db.EmployeePayrollProfiles.AddRange(
            new EmployeePayrollProfile { TenantId = tenantId, EmployeeId = empA.Id, Iban = "SA-ALPHA-IBAN", BankName = "Alpha Bank" },
            new EmployeePayrollProfile { TenantId = tenantId, EmployeeId = empB.Id, Iban = "SA-BETA-IBAN", BankName = "Beta Bank" });
        await db.SaveChangesAsync();

        return new World(tenantId, companyA.Id, companyB.Id, empA.Id, empB.Id, run.Id);
    }

    private static ClaimsPrincipal ScopedHr(Guid tenantId, Guid companyId) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "HR Manager"),
            new Claim("permission", "employees.read"),
            new Claim("permission", "employees.write"),
            new Claim("permission", "reports.read"),
            new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { companyId } })),
        }, "Test"));

    private static IHttpContextAccessor Accessor(ClaimsPrincipal principal) =>
        new FixedAccessor { HttpContext = new DefaultHttpContext { User = principal } };

    private static EmployeesController CreateEmployeesController(ZayraDbContext db, ClaimsPrincipal principal)
    {
        var controller = new EmployeesController(
            db,
            new Pbkdf2PasswordHasher(),
            new AuditService(db),
            new FakeDocumentStorage(),
            new FakeNotificationService(),
            new FakeHijriDateService(),
            new DataScopeService(db),
            new FakeLetterService());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };
        return controller;
    }

    private sealed class FixedAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class FakeDocumentStorage : IDocumentStorage
    {
        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) =>
            Task.FromResult(new StoredDocument(file.FileName, file.ContentType, "storage/test", "/tmp/test"));
        public string ResolvePath(string storageUrl) => "/tmp/test";
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) =>
            Task.FromResult(Array.Empty<byte>());
    }

    private sealed class FakeNotificationService : INotificationService
    {
        public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string entity, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeHijriDateService : IHijriDateService
    {
        public DateConversionDto FromGregorian(DateOnly date) =>
            new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
    }

    private sealed class FakeLetterService : ILetterService
    {
        public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }
}
