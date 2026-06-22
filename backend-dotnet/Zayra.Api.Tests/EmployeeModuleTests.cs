using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class EmployeeModuleTests
{
    [Fact]
    public async Task ApproveDraft_ActivatesEmployeeCreatesUserAndHistory()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAndEmployeeRole(db);
        var controller = CreateController(db, tenantId);
        var draftResult = await controller.CreateDraft(new EmployeeDraftRequest("Review", "Sara Ahmed", "سارة أحمد", "sara.personal@example.com", "sara@zayra.local", "+9715000000", "Female", DateOnly.FromDateTime(DateTime.UtcNow.Date.AddYears(-30)), "Married", "Ali Ahmed", "+9715111111", "UAE", "UAE", "People", "HR Officer", "Dubai", "Dubai HQ", null, DateTime.UtcNow.Date, "Unlimited", "G5", "HR-001", DateOnly.FromDateTime(DateTime.UtcNow.Date), DateOnly.FromDateTime(DateTime.UtcNow.Date.AddYears(2)), DateOnly.FromDateTime(DateTime.UtcNow.Date.AddMonths(6)), "MONTHLY", 12000m, "Emirates NBD", "AE000000", "WPS-1", "DAY", "UAE-ANNUAL", "Zayra", DateOnly.FromDateTime(DateTime.UtcNow.Date.AddYears(-1)), "P123", DateOnly.FromDateTime(DateTime.UtcNow.Date.AddYears(5)), DateOnly.FromDateTime(DateTime.UtcNow.Date), "V123", DateOnly.FromDateTime(DateTime.UtcNow.Date.AddYears(2)), null, null, null, null, "784-0000", "LC-1", "VF-1", null, null, null, null, null, null), CancellationToken.None);
        var draft = Assert.IsType<EmployeeDraftDto>(Assert.IsType<CreatedResult>(draftResult.Result).Value);
        await controller.SubmitDraft(draft.Id, CancellationToken.None);

        var approval = await controller.ApproveDraft(draft.Id, CancellationToken.None);

        var profile = Assert.IsType<EmployeeDetailDto>(Assert.IsType<OkObjectResult>(approval.Result).Value);
        Assert.Equal("Active", profile.Status);
        Assert.StartsWith("EMP-", profile.EmployeeCode);
        Assert.NotNull(profile.UserAccountId);
        Assert.True(await db.EmployeeHistories.AnyAsync(x => x.EmployeeId == profile.Id && x.EventType == "Activated"));
    }

    [Fact]
    public async Task SensitiveUpdate_CreatesApprovalRequestWithoutChangingEmployee()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAndEmployeeRole(db);
        var employee = new Employee { TenantId = tenantId, EmployeeCode = "EMP-00001", FullName = "Sara Ahmed", Department = "People", Designation = "HR Officer", Status = "Active", JoiningDate = DateTime.UtcNow.Date, Salary = 10000m };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var controller = CreateController(db, tenantId);

        var result = await controller.UpdateEmployee(employee.Id, new EmployeeUpdateRequest(DateOnly.FromDateTime(DateTime.UtcNow.Date), new() { ["salary"] = System.Text.Json.JsonDocument.Parse("15000").RootElement }), CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
        var unchanged = await db.Employees.FindAsync(employee.Id);
        Assert.Equal(10000m, unchanged!.Salary);
        Assert.True(await db.EmployeeChangeRequests.AnyAsync(x => x.EmployeeId == employee.Id && x.SensitiveFields == "salary"));
    }

    private static ZayraDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new ZayraDbContext(options);
    }

    private static async Task<Guid> SeedTenantAndEmployeeRole(ZayraDbContext db)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Zayra HQ", Slug = "zayra" };
        var role = new Role { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Employee", NormalizedName = "EMPLOYEE", Description = "Employee" };
        db.Tenants.Add(tenant);
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    // ── Test: Employee CSV import with payroll columns creates PayrollProfile + SalaryStructure ──

    [Fact]
    public async Task EmployeeImport_WithPayrollColumns_CreatesPayrollProfileAndSalaryStructure()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAndEmployeeRole(db);
        var ctrl = CreateController(db, tenantId);

        const string csv =
            "EmployeeCode,FullName,WorkEmail,JoiningDate,BasicSalary,HousingAllowance,TransportAllowance,OtherAllowance,Currency,IBAN,BankName,MolId\n" +
            "EMP-PAY-001,Ahmad Al-Rashidi,ahmad@test.com,2024-01-15,8000,2000,1000,500,SAR,SA0380000000608010167519,Al Rajhi Bank,MOL-001\n";

        var result = await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);

        var employee = await db.Employees.FirstOrDefaultAsync(e => e.TenantId == tenantId && e.EmployeeCode == "EMP-PAY-001");
        Assert.NotNull(employee);
        Assert.Equal(11500m, employee.Salary);
        Assert.Equal("Al Rajhi Bank", employee.BankName);
        Assert.Equal("SA0380000000608010167519", employee.BankIban);

        var profile = await db.EmployeePayrollProfiles.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.EmployeeId == employee.Id);
        Assert.NotNull(profile);
        Assert.Equal("SA0380000000608010167519", profile.Iban);
        Assert.Equal("SAR", profile.SalaryCurrency);
        Assert.Equal("MOL-001", profile.MolId);
        Assert.True(profile.WpsEligible);
        Assert.True(profile.EosbEligible);

        var salary = await db.EmployeeSalaryStructures.FirstOrDefaultAsync(s => s.TenantId == tenantId && s.EmployeeId == employee.Id);
        Assert.NotNull(salary);
        Assert.Equal(8000m, salary.BasicSalary);
        Assert.Equal(2000m, salary.HousingAllowance);
        Assert.Equal(1000m, salary.TransportAllowance);
        Assert.Equal(500m, salary.OtherAllowance);
        Assert.Equal("SAR", salary.Currency);
        Assert.True(salary.IsActive);
    }

    // ── Test: Currency defaults to SAR when blank ─────────────────────────────

    [Fact]
    public async Task EmployeeImport_BlankCurrency_DefaultsToSar()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAndEmployeeRole(db);
        var ctrl = CreateController(db, tenantId);

        const string csv =
            "EmployeeCode,FullName,JoiningDate,BasicSalary,Currency\n" +
            "EMP-CUR-001,Test Employee,2024-01-15,5000,\n";

        await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);

        var employee = await db.Employees.FirstOrDefaultAsync(e => e.TenantId == tenantId && e.EmployeeCode == "EMP-CUR-001");
        Assert.NotNull(employee);
        var profile = await db.EmployeePayrollProfiles.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.EmployeeId == employee.Id);
        Assert.NotNull(profile);
        Assert.Equal("SAR", profile.SalaryCurrency);
        var salaryRec = await db.EmployeeSalaryStructures.FirstOrDefaultAsync(s => s.TenantId == tenantId && s.EmployeeId == employee.Id);
        Assert.NotNull(salaryRec);
        Assert.Equal("SAR", salaryRec.Currency);
    }

    // ── Test: Import preview with invalid IBAN produces warning (not error) ───

    [Fact]
    public async Task EmployeeImportPreview_InvalidIban_ProducesWarning()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAndEmployeeRole(db);
        var ctrl = CreateController(db, tenantId);

        const string csv =
            "EmployeeCode,FullName,JoiningDate,IBAN\n" +
            "EMP-IBAN-001,Test Employee,2024-01-15,INVALID-IBAN\n";

        var result = await ctrl.ImportPreview(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        // Row is WillCreate (not Error) because IBAN issue is a warning only
        Assert.Contains("WillCreate", json);
        Assert.Contains("IBAN", json);
        Assert.Contains("invalid", json);
        // No DB records created (preview is dry-run)
        Assert.Equal(0, await db.EmployeePayrollProfiles.CountAsync());
    }

    // ── Test: 15-row import on a fresh empty tenant succeeds without pre-setup ─

    [Fact]
    public async Task EmployeeImport_FreshTenant_15Rows_CreatesAll()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAndEmployeeRole(db);
        var ctrl = CreateController(db, tenantId);

        var headerLine = "EmployeeCode,FullName,Department,JoiningDate,BasicSalary,Currency";
        var dataLines = Enumerable.Range(1, 15).Select(i =>
            $"EMP-{i:D3},Employee {i},Engineering,2024-01-01,{5000 + i * 100},SAR");
        var csv = string.Join("\n", new[] { headerLine }.Concat(dataLines)) + "\n";

        var result = await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"created\":15", json);
        Assert.Contains("\"skipped\":0", json);

        var empCount = await db.Employees.CountAsync(e => e.TenantId == tenantId && !e.IsDeleted);
        Assert.Equal(15, empCount);
        // All 15 should have payroll profiles (salary provided)
        var profileCount = await db.EmployeePayrollProfiles.CountAsync(p => p.TenantId == tenantId);
        Assert.Equal(15, profileCount);
        // All 15 should have salary structures
        var salaryCount = await db.EmployeeSalaryStructures.CountAsync(s => s.TenantId == tenantId);
        Assert.Equal(15, salaryCount);
    }

    private static EmployeesController CreateController(ZayraDbContext db, Guid tenantId)
    {
        var controller = new EmployeesController(db, new Pbkdf2PasswordHasher(), new AuditService(db), new FakeDocumentStorage(), new NotificationService(db, new FakeEmailService(), NullLogger<NotificationService>.Instance), new FakeHijriDateService(), new Zayra.Api.Infrastructure.Common.DataScopeService(db), new FakeLetterService());
        var userId = Guid.NewGuid();
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Role, "Admin")
        }, "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        return controller;
    }
}

file sealed class FakeDocumentStorage : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken cancellationToken) => Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/documents/test", "/tmp/test"));
    public string ResolvePath(string storageUrl) => "/tmp/test";
}

file sealed class FakeHijriDateService : Zayra.Api.Infrastructure.Localization.IHijriDateService
{
    public Zayra.Api.Infrastructure.Localization.DateConversionDto FromGregorian(DateOnly date) => new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
}

file sealed class FakeEmailService : IEmailService
{
    public Task SendAsync(string toAddress, string toName, string subject, string htmlBody,
        IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}

file sealed class FakeLetterService : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<byte>());

    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<byte>());

    public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<byte>());

    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<byte>());
}
