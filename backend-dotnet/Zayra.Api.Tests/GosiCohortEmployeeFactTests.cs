using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// F02 — the GOSI first-registration date is a person-level statutory fact, written ONLY through the
/// approval-gated sensitive-change path, applied identically by both approve paths, refused when it is not
/// a real past date, and never shown to a caller without sensitive access.
/// </summary>
public class GosiCohortEmployeeFactTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

    [Fact]
    public void ExistingEmployees_StartUnknown_AndTheDateDecidesTheCohort()
    {
        new Employee().GosiFirstRegisteredOn.Should().BeNull("an existing employee is never assumed into a cohort");
        GosiCohorts.Resolve(null).Should().Be(GosiCohorts.Unknown);
        GosiCohorts.Resolve(new DateOnly(2024, 7, 2)).Should().Be(GosiCohorts.PreJuly2024);
        GosiCohorts.Resolve(new DateOnly(2024, 7, 3)).Should().Be(GosiCohorts.NewEntrant, "on or after 3 July 2024 is a new entrant");
        GosiCohorts.Resolve(new DateOnly(2025, 2, 1)).Should().Be(GosiCohorts.NewEntrant);
    }

    [Fact]
    public async Task RecordingTheDate_IsApprovalGated_AndNothingIsWrittenBeforeApproval()
    {
        await using var db = CreateDb();
        var (tenantId, employee) = await SeedSaudiAsync(db);

        var result = await Controller(db, tenantId).UpdateEmployee(employee.Id,
            new EmployeeUpdateRequest(Today, Changes(("gosiFirstRegisteredOn", "2019-03-01"))), CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>("the first-registration date decides a contribution schedule — maker-checker");
        db.ChangeTracker.Clear();
        (await db.Employees.AsNoTracking().SingleAsync(e => e.Id == employee.Id)).GosiFirstRegisteredOn.Should().BeNull();
        (await db.EmployeeChangeRequests.AsNoTracking().SingleAsync(c => c.EmployeeId == employee.Id))
            .SensitiveFields.Should().Be("gosiFirstRegisteredOn");
        (await db.ApprovalRequests.AsNoTracking().CountAsync(a => a.EntityName == nameof(EmployeeChangeRequest) && a.Status == "Pending"))
            .Should().Be(1);
    }

    [Fact]
    public async Task ApprovalsScreenDecision_WritesTheDate_TheSameWay()
    {
        // The second approve path (ApprovalWorkflowService.DecideAsync, the Approvals screen) carries its own
        // copy of the field switch; a key missing there is approved and silently NOT applied.
        await using var db = CreateDb();
        var (tenantId, employee) = await SeedSaudiAsync(db);
        await Controller(db, tenantId).UpdateEmployee(employee.Id,
            new EmployeeUpdateRequest(Today, Changes(("gosiFirstRegisteredOn", "2016-05-10"))), CancellationToken.None);
        var approval = await db.ApprovalRequests.SingleAsync(a => a.EntityName == nameof(EmployeeChangeRequest));

        var decided = await new ApprovalWorkflowService(db, new AuditService(db)).DecideAsync(
            tenantId, approval.Id, new Zayra.Api.Application.Approvals.ApprovalDecisionRequest("Approve", "checked against GOSI record"),
            new RequestContext("127.0.0.1", "tests", Guid.NewGuid(), tenantId, ["HR Manager"], []), CancellationToken.None);

        decided!.Status.Should().Be("Approved");
        db.ChangeTracker.Clear();
        (await db.Employees.AsNoTracking().SingleAsync(e => e.Id == employee.Id))
            .GosiFirstRegisteredOn.Should().Be(new DateOnly(2016, 5, 10));
    }

    [Theory]
    [InlineData("2099-01-01", "future")]
    [InlineData("01/03/2019", "YYYY-MM-DD")]
    [InlineData("not a date", "YYYY-MM-DD")]
    public async Task ADateThatIsNotARealPastDate_IsRefusedUpFront(string value, string reasonFragment)
    {
        await using var db = CreateDb();
        var (tenantId, employee) = await SeedSaudiAsync(db);

        var result = await Controller(db, tenantId).UpdateEmployee(employee.Id,
            new EmployeeUpdateRequest(Today, Changes(("gosiFirstRegisteredOn", value))), CancellationToken.None);

        var refused = result.Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        JsonSerializer.SerializeToElement(refused.Value!).GetProperty("message").GetString().Should().Contain(reasonFragment);
        (await db.EmployeeChangeRequests.AnyAsync()).Should().BeFalse(
            "an unparseable value would otherwise be approved as 'a date' and then clear the cohort to Unknown");
    }

    [Fact]
    public async Task ClearingTheDate_IsAllowed_AndAlsoApprovalGated()
    {
        await using var db = CreateDb();
        var (tenantId, employee) = await SeedSaudiAsync(db, firstRegisteredOn: new DateOnly(2016, 5, 10));

        var result = await Controller(db, tenantId).UpdateEmployee(employee.Id,
            new EmployeeUpdateRequest(Today, new Dictionary<string, JsonElement> { ["gosiFirstRegisteredOn"] = JsonSerializer.SerializeToElement((string?)null) }),
            CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
    }

    [Fact]
    public void TheDetailView_ShowsTheCohortOnlyToSensitiveViewers()
    {
        var e = new Employee { EmployeeCode = "E1", FullName = "Saudi One", Nationality = "Saudi", GosiFirstRegisteredOn = new DateOnly(2025, 2, 1) };

        var full = EmployeeDetailDto.Project(e, includeSensitive: true);
        full.GosiFirstRegisteredOn.Should().Be(new DateOnly(2025, 2, 1));
        full.GosiCohort.Should().Be(GosiCohorts.NewEntrant);

        var masked = EmployeeDetailDto.Project(e, includeSensitive: false);
        masked.GosiFirstRegisteredOn.Should().BeNull();
        masked.GosiCohort.Should().BeNull("a masked viewer must not see 'Unknown' for a date that is recorded");

        EmployeeDetailDto.Project(new Employee { EmployeeCode = "E2", FullName = "No Date" }, includeSensitive: true)
            .GosiCohort.Should().Be(GosiCohorts.Unknown);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private static Dictionary<string, JsonElement> Changes(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value), StringComparer.Ordinal);

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(Guid TenantId, Employee Employee)> SeedSaudiAsync(ZayraDbContext db, DateOnly? firstRegisteredOn = null)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Cohort T", Slug = $"ct-{Guid.NewGuid():N}" };
        db.Tenants.Add(tenant);
        db.Roles.Add(new Role { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Employee", NormalizedName = "EMPLOYEE", Description = "Employee" });
        var employee = new Employee
        {
            TenantId = tenant.Id, EmployeeCode = $"E-{Guid.NewGuid():N}"[..12], FullName = "Saudi Cohort",
            Nationality = "Saudi", CountryCode = "SA", Status = "Active", JoiningDate = DateTime.UtcNow.Date,
            GosiFirstRegisteredOn = firstRegisteredOn,
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return (tenant.Id, employee);
    }

    private static EmployeesController Controller(ZayraDbContext db, Guid tenantId)
    {
        var audit = new AuditService(db);
        var controller = new EmployeesController(
            db, new Pbkdf2PasswordHasher(), audit, new CohortFakeDocumentStorage(), TestNotifications.For(db),
            new CohortFakeHijri(), new Zayra.Api.Infrastructure.Common.DataScopeService(db), new CohortFakeLetters(),
            new ApprovalWorkflowService(db, audit));
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim("permission", "employees.read"),
            new Claim("permission", "employees.write"),
            new Claim("permission", "employees.sensitive"),
            new Claim("permission", "employees.approve"),
            new Claim("is_group_scope", "true"),
        }, "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        return controller;
    }
}

file sealed class CohortFakeDocumentStorage : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken cancellationToken) =>
        Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/documents/test", "/tmp/test"));
    public string ResolvePath(string storageUrl) => "/tmp/test";
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class CohortFakeHijri : Zayra.Api.Infrastructure.Localization.IHijriDateService
{
    public Zayra.Api.Infrastructure.Localization.DateConversionDto FromGregorian(DateOnly date) =>
        new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
}

file sealed class CohortFakeLetters : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
}
