using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Localization;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Localization;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Onboarding;

/// <summary>
/// Two creates of the same person, each on its OWN context and connection, released together from a barrier.
///
/// <para><b>The lock test fails without the lock.</b> Both requests run the pre-check before either has inserted, so both
/// read "no match". With <c>EmployeeIdentityLock</c> removed, nothing orders them and both inserts succeed (the identity
/// columns are not unique): the assertion "exactly one Created and one 409" then sees two Created and the row count is 2.
/// With the lock, the second request blocks on <c>pg_advisory_xact_lock</c> until the first commits, re-reads inside its
/// transaction, finds the committed row and refuses. The rounds are repeated so a scheduling accident that serialises one
/// round cannot make the test pass in all of them.</para>
///
/// NOT RUN in the author's sandbox (Testcontainers needs Docker); the lead runs these on the train.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class HireIdentityLockPostgresTests
{
    private readonly PostgresFixture _fx;
    public HireIdentityLockPostgresTests(PostgresFixture fx) => _fx = fx;
    private const int Rounds = 5;

    [Fact]
    public async Task Create_ConcurrentSameIdentity_OneSucceeds_OtherGets409()
    {
        var tenant = await SeedAsync();
        for (var round = 0; round < Rounds; round++)
        {
            var iqama = $"21{round:00}{Random.Shared.Next(100000, 999999)}";
            var results = await RaceAsync(tenant, a: Request($"Same Person {round}", iqama: iqama), b: Request($"Same Person B {round}", iqama: iqama));
            results.Count(r => r is CreatedAtActionResult).Should().Be(1, $"round {round}: exactly one create lands");
            results.Count(r => r is ConflictObjectResult).Should().Be(1, $"round {round}: the other is refused with the existing advisory 409");
            await using var db = _fx.CreateDb();
            (await db.Employees.IgnoreQueryFilters().CountAsync(e => e.TenantId == tenant.TenantId && e.IqamaNumber == iqama))
                .Should().Be(1, $"round {round}: no second row");
        }
    }

    [Fact]
    public async Task Create_ConcurrentSameNameDob_NoIdentity_OneAdvisory409()
    {
        var tenant = await SeedAsync();
        for (var round = 0; round < Rounds; round++)
        {
            var name = $"Name Only {Guid.NewGuid():N}";
            // Never a Jan-1: that is a data-entry sentinel the duplicate detector deliberately ignores (M2).
            var dob = new DateOnly(1990, 3, 5).AddDays(round);
            var results = await RaceAsync(tenant, a: Request(name, dob: dob), b: Request(name, dob: dob));
            results.Count(r => r is CreatedAtActionResult).Should().Be(1, $"round {round}");
            results.Count(r => r is ConflictObjectResult).Should().Be(1, $"round {round}");
        }
    }

    [Fact]
    public async Task Payroll_ExcludesDraft_AndOpenCompensationGapKeepsTheHireOutOfActive()
    {
        var tenant = await SeedAsync();
        await using var db = _fx.CreateDb();
        var result = await Controller(db, tenant).CreateEmployee(Request("Pay Gate", iban: "SA0380000000608010167519"), Service(db), default);
        result.Result.Should().BeOfType<CreatedAtActionResult>();
        var employee = await db.Employees.IgnoreQueryFilters().SingleAsync(e => e.TenantId == tenant.TenantId && e.EnglishName == "Pay Gate");
        employee.Status.Should().Be("Draft", "payroll selects Status == Active only, so a Draft is excluded by construction");
        (await db.EmployeeImportGaps.IgnoreQueryFilters().AnyAsync(g => g.EmployeeId == employee.Id && g.GapType == "pay:compensationUnapproved" && g.ResolvedAtUtc == null))
            .Should().BeTrue("create opens the approval gap in the same transaction");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────
    private sealed record Seed(Guid TenantId, Guid CompanyId);

    private async Task<Seed> SeedAsync()
    {
        await using var db = _fx.CreateDb();
        var tenant = new Tenant { Name = "Lock", Slug = $"lock-{Guid.NewGuid():N}", IsActive = true };
        db.Tenants.Add(tenant);
        var company = new Company { TenantId = tenant.Id, LegalNameEn = "Lock Co", CountryCode = "SA", IsActive = true, DefaultCurrency = "SAR", RegistrationNumber = $"RC-{Guid.NewGuid():N}", Jurisdiction = "test" };
        db.Companies.Add(company);
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenant.Id, Plan = "Enterprise", Status = "Active", MaxEmployees = 10_000 });
        await db.SaveChangesAsync();
        return new Seed(tenant.Id, company.Id);
    }

    private async Task<IActionResult?[]> RaceAsync(Seed seed, EmployeeCreateRequest a, EmployeeCreateRequest b)
    {
        var barrier = new TaskCompletionSource();
        async Task<IActionResult?> One(EmployeeCreateRequest request)
        {
            await using var db = _fx.CreateDb();
            await db.Database.OpenConnectionAsync();      // warm the connection before the race
            var controller = Controller(db, seed);
            var service = Service(db);
            await barrier.Task;
            var result = await controller.CreateEmployee(request, service, default);
            return result.Result;
        }
        var t1 = Task.Run(() => One(a));
        var t2 = Task.Run(() => One(b));
        await Task.Delay(300);
        barrier.SetResult();
        return await Task.WhenAll(t1, t2);
    }

    private static EmployeeManagementService Service(ZayraDbContext db) =>
        new(db, new AuditService(db), new LockDocs(), TestNotifications.For(db));

    private static EmployeesController Controller(ZayraDbContext db, Seed seed)
    {
        var controller = new EmployeesController(db, new Pbkdf2PasswordHasher(), new AuditService(db), new LockDocs(),
            TestNotifications.For(db), new LockHijri(), new Zayra.Api.Infrastructure.Common.DataScopeService(db), new LockLetters());
        var claims = new[]
        {
            new Claim("tenant_id", seed.TenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "Admin"), new Claim("permission", "employees.read"), new Claim("permission", "employees.write"),
        };
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) } };
        return controller;
    }

    private static EmployeeCreateRequest Request(string name, string? iqama = null, DateOnly? dob = null, string? iban = null) => new(
        EmployeeCode: null, ManualEmployeeCode: false, EnglishName: name, ArabicName: null, PreferredName: null,
        Gender: "Male", DateOfBirth: dob, Nationality: "Saudi", MaritalStatus: null, PersonalEmail: null,
        WorkEmail: null, MobileNumber: null, ProfilePhotoUrl: null, CompanyId: null, BranchId: null,
        DepartmentId: null, DesignationId: null, GradeId: null, CostCenterId: null, JobTitle: null,
        ReportingManagerEmployeeId: null, SecondLevelManagerEmployeeId: null, EmploymentType: "Full-time",
        ContractType: "Unlimited", JoiningDate: DateTime.UtcNow.Date, ConfirmationDate: null, ProbationStartDate: null,
        ProbationEndDate: null, NoticePeriodDays: null, WorkLocation: null, PayrollGroup: null, ShiftPolicyCode: null,
        LeavePolicyCode: null, AttendancePolicyCode: null,
        PayrollProfile: iban is null ? null : new EmployeePayrollProfileRequest("Test Bank", iban, null, "BankTransfer", "SAR", null, null, true, true, null, null, null),
        SalaryBreakdown: null,
        ComplianceRecords: iqama is null ? null : [new EmployeeComplianceRecordRequest("SA", "iqama_number", "Iqama", iqama, null, null, true, false)],
        PositionId: null, AcknowledgeDuplicate: false);

    private sealed class LockDocs : IDocumentStorage
    {
        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) =>
            Task.FromResult(new StoredDocument(file.FileName, file.ContentType, "storage/test", "/tmp/test"));
        public string ResolvePath(string storageUrl) => "/tmp/test";
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }

    private sealed class LockHijri : IHijriDateService
    {
        public DateConversionDto FromGregorian(DateOnly date) => new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
    }

    private sealed class LockLetters : ILetterService
    {
        public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }
}
