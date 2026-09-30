using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests;

/// <summary>
/// THE EMPLOYEE'S GOVERNING JURISDICTION — derivation on every write path, and fail-closed enforcement
/// when it cannot be identified.
///
/// <para><b>The defect these pin.</b> A recorded evidence run made two <c>POST /api/employees</c> calls
/// that differed in one field. With a <c>complianceRecords</c> entry naming SA, activating the employee
/// was refused 422 <c>employee_not_activatable</c> on <c>IqamaNumber</c>. Without it, the identical
/// employee — same Saudi legal entity, same Indian nationality, same absent Iqama and GOSI reference —
/// was stored with <c>CountryCode ""</c> and ACTIVATED. The create path took the country solely from the
/// first compliance record and never fell back to the employing company's, and
/// <c>GccReadinessFloor.Resolve("")</c> answers an unknown jurisdiction with an EMPTY requirement list —
/// which the gate read as "nothing is required" rather than "nothing was checked".</para>
///
/// <para><b>Why the existing suite missed it.</b> <c>EmployeeActivationGateTests</c> constructs its
/// fixtures with <c>CountryCode = "SA"</c> set directly on the entity, so it exercises the gate but never
/// the derivation. These tests deliberately go through <c>CreateAsync</c> and never set the column, so the
/// derivation is the thing under test.</para>
///
/// <para>Both halves are proved, because a rule only ever seen to block is as untrustworthy as one only
/// ever seen to pass: a KNOWN non-GCC jurisdiction still activates (no GCC floor applies to a UK entity,
/// and none is invented), while a blank or unrecognised one is refused and told why.</para>
/// </summary>
public class EmployeeCountryDerivationTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed record Fx(Guid TenantId, Company Company, Department Dept, Designation Desig);

    private static async Task<Fx> Seed(ZayraDbContext db, string companyCountry)
    {
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Derivation", Slug = $"t-{tenantId:N}" });
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenantId, MaxEmployees = 1000, Plan = "Enterprise", Status = "Active" });
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "Derivation Legal Entity", TradeName = "Derivation",
            CountryCode = companyCountry, Jurisdiction = "test", RegistrationNumber = $"RC-{Guid.NewGuid():N}",
            DefaultCurrency = "SAR", IsActive = true, CreatedAtUtc = DateTime.UtcNow,
        };
        var dept = new Department { TenantId = tenantId, Code = "OPS", NameEn = "Operations", IsActive = true };
        var desig = new Designation { TenantId = tenantId, Code = "OPS-OFF", TitleEn = "Operations Officer", IsActive = true };
        db.AddRange(company, dept, desig);
        await db.SaveChangesAsync();
        return new Fx(tenantId, company, dept, desig);
    }

    private static EmployeeManagementService Svc(ZayraDbContext db) =>
        new(db, new AuditService(db), new CdDocs(), new CdNotifications(), new EstablishmentGuardService(db));

    private static RequestContext Ctx(Guid tenantId) => new(null, "test", Guid.NewGuid(), tenantId);

    /// <summary>The evidence run's request, to the letter: an Indian national under a named company, with
    /// NO compliance record and therefore no stated country, and no Iqama and no GOSI reference.</summary>
    private static EmployeeCreateRequest Req(
        Fx fx, string englishName, IReadOnlyCollection<EmployeeComplianceRecordRequest>? compliance = null) =>
        new(
            EmployeeCode: null, ManualEmployeeCode: false, EnglishName: englishName, ArabicName: null,
            PreferredName: null, Gender: "Male", DateOfBirth: null, Nationality: "Indian", MaritalStatus: null,
            PersonalEmail: null, WorkEmail: null, MobileNumber: null, ProfilePhotoUrl: null,
            CompanyId: fx.Company.Id, BranchId: null, DepartmentId: fx.Dept.Id, DesignationId: fx.Desig.Id,
            GradeId: null, CostCenterId: null, JobTitle: null, ReportingManagerEmployeeId: null,
            SecondLevelManagerEmployeeId: null, EmploymentType: "Full-time", ContractType: "Unlimited",
            JoiningDate: DateTime.UtcNow.Date, ConfirmationDate: null, ProbationStartDate: null,
            ProbationEndDate: null, NoticePeriodDays: null, WorkLocation: null, PayrollGroup: null,
            ShiftPolicyCode: null, LeavePolicyCode: null, AttendancePolicyCode: null,
            PayrollProfile: null, SalaryBreakdown: null, ComplianceRecords: compliance);

    private static EmployeeStatusChangeRequest Activate() =>
        new("Active", DateOnly.FromDateTime(DateTime.UtcNow.Date), "test: activate");

    // ── Derivation: explicit wins, else the employing company's ───────────────────────────────────

    [Fact]
    public async Task ACreateThatStatesNoCountry_InheritsTheEmployingCompanysCountry()
    {
        await using var db = CreateDb();
        var fx = await Seed(db, companyCountry: "SA");

        var created = await Svc(db).CreateAsync(fx.TenantId, Req(fx, "Derived Hire"), Ctx(fx.TenantId), default);

        db.ChangeTracker.Clear();
        var stored = await db.Employees.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Id == created.Id);
        stored.CountryCode.Should().Be("SA",
            "the employee is attached to a Saudi legal entity, and the field catalog publishes the rule: "
            + "explicit countryCode wins, else the company's country");
    }

    [Fact]
    public async Task AnExplicitCountryWins_OverTheEmployingCompanys()
    {
        await using var db = CreateDb();
        var fx = await Seed(db, companyCountry: "SA");
        var stated = new[] { new EmployeeComplianceRecordRequest("AE", "emirates_id", "Emirates ID", "784-1", null, null, false, true) };

        var created = await Svc(db).CreateAsync(fx.TenantId, Req(fx, "Stated Hire", stated), Ctx(fx.TenantId), default);

        db.ChangeTracker.Clear();
        (await db.Employees.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Id == created.Id))
            .CountryCode.Should().Be("AE", "an explicitly stated country is never overridden by the company's");
    }

    [Fact]
    public async Task AnIso3CountryOnTheCompany_IsNormalisedOntoTheEmployee()
    {
        await using var db = CreateDb();
        var fx = await Seed(db, companyCountry: "SAU");

        var created = await Svc(db).CreateAsync(fx.TenantId, Req(fx, "Iso3 Hire"), Ctx(fx.TenantId), default);

        db.ChangeTracker.Clear();
        (await db.Employees.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Id == created.Id))
            .CountryCode.Should().Be("SA", "one country list, one canonical form — the product stores ISO-2");
    }

    [Fact]
    public async Task EditingAnEmployeeWhoseCountryWasLeftBlank_HealsItFromTheCompany()
    {
        await using var db = CreateDb();
        var fx = await Seed(db, companyCountry: "SA");
        // A row as the defect left it: attached to a Saudi entity, stored with no country at all.
        var legacy = new Employee
        {
            TenantId = fx.TenantId, CompanyId = fx.Company.Id, EmployeeCode = "LEGACY-1",
            FullName = "Legacy Row", EnglishName = "Legacy Row", Nationality = "Indian",
            CountryCode = string.Empty, Status = "Draft", JoiningDate = DateTime.UtcNow.Date,
            DepartmentId = fx.Dept.Id, DesignationId = fx.Desig.Id,
        };
        db.Employees.Add(legacy);
        await db.SaveChangesAsync();

        await Svc(db).UpdateAsync(fx.TenantId, legacy.Id, Req(fx, "Legacy Row"), Ctx(fx.TenantId), default);

        db.ChangeTracker.Clear();
        (await db.Employees.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Id == legacy.Id))
            .CountryCode.Should().Be("SA", "an edit heals a country an earlier create or import left blank");
    }

    // ── The evidence run, reproduced: the KSA floor must now refuse ───────────────────────────────

    [Fact]
    public async Task AnIncompleteKsaExpatCreatedWithoutACountry_IsRefusedActivation()
    {
        await using var db = CreateDb();
        var fx = await Seed(db, companyCountry: "SA");
        // The evidence run's bypass case verbatim: no complianceRecords, no Iqama, no GOSI reference.
        var created = await Svc(db).CreateAsync(fx.TenantId, Req(fx, "Evidence Bypass Case"), Ctx(fx.TenantId), default);

        var act = () => Svc(db).ChangeStatusAsync(fx.TenantId, created.Id, Activate(), Ctx(fx.TenantId), default);

        var readiness = (await act.Should().ThrowAsync<EmployeeActivationBlockedException>(
            "the employee is attached to a Saudi legal entity, so the KSA floor applies whether or not the "
            + "request happened to carry a compliance record naming SA"))
            .Which.Readiness;
        readiness.Blocking.Should().Contain(i => i.Key == "IqamaNumber" && i.Gate == "activate",
            "this is the SAME refusal the request WITH a compliance record already produced");
        readiness.PayBlocking.Should().Contain(i => i.Key == "GosiReference" && i.Gate == "pay");

        db.ChangeTracker.Clear();
        (await db.Employees.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Id == created.Id))
            .Status.Should().Be("Draft", "a refused activation leaves the record untouched and occupies no seat");
    }

    [Fact]
    public async Task TheSameEmployeeWithItsIqama_StillActivates()
    {
        await using var db = CreateDb();
        var fx = await Seed(db, companyCountry: "SA");
        var created = await Svc(db).CreateAsync(fx.TenantId, Req(fx, "Complete Hire"), Ctx(fx.TenantId), default);
        var tracked = await db.Employees.SingleAsync(e => e.Id == created.Id);
        tracked.IqamaNumber = "2000000001";
        await db.SaveChangesAsync();

        await Svc(db).ChangeStatusAsync(fx.TenantId, created.Id, Activate(), Ctx(fx.TenantId), default);

        db.ChangeTracker.Clear();
        (await db.Employees.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Id == created.Id))
            .Status.Should().Be("Active", "the derived floor blocks only what is actually missing");
    }

    // ── Fail closed on an UNKNOWN jurisdiction — but only on an unknown one ───────────────────────

    [Fact]
    public async Task AnEmployeeWithNoIdentifiableCountry_IsRefusedActivationAndToldWhy()
    {
        await using var db = CreateDb();
        var fx = await Seed(db, companyCountry: "SA");
        // Nothing left to derive from: no company either. This is the shape a row persisted before the
        // derivation existed can still hold, and the only way an unknown jurisdiction now reaches the gate.
        var orphan = new Employee
        {
            TenantId = fx.TenantId, CompanyId = null, EmployeeCode = "ORPHAN-1",
            FullName = "No Jurisdiction", EnglishName = "No Jurisdiction", Nationality = "Indian",
            CountryCode = string.Empty, Status = "Draft", JoiningDate = DateTime.UtcNow.Date,
            DepartmentId = fx.Dept.Id, DesignationId = fx.Desig.Id,
        };
        db.Employees.Add(orphan);
        await db.SaveChangesAsync();

        var act = () => Svc(db).ChangeStatusAsync(fx.TenantId, orphan.Id, Activate(), Ctx(fx.TenantId), default);

        var readiness = (await act.Should().ThrowAsync<EmployeeActivationBlockedException>(
            "an empty requirement list means nothing was CHECKED, not that nothing is required"))
            .Which.Readiness;
        var blocker = readiness.Blocking.Should().ContainSingle(i => i.Key == "CountryCode").Subject;
        blocker.Gate.Should().Be("activate");
        blocker.Label.Should().Contain("Country not set", "the refusal must name the problem in plain words");
        blocker.FixTarget.Should().Be("CountryCode", "and point at the field that fixes it");
    }

    [Fact]
    public async Task AFreeTextCountryIsNeverSilentlyReplaced_AndIsRefusedAsUnrecognised()
    {
        await using var db = CreateDb();
        var fx = await Seed(db, companyCountry: "SA");
        // "UAE" is neither ISO-2 nor ISO-3. Substituting the company's SA would hand the operator a
        // jurisdiction they never stated, so the value stands and the gate says it is not recognised.
        var stated = new[] { new EmployeeComplianceRecordRequest("UAE", "emirates_id", "Emirates ID", "784-1", null, null, false, true) };
        var created = await Svc(db).CreateAsync(fx.TenantId, Req(fx, "Free Text Country", stated), Ctx(fx.TenantId), default);

        db.ChangeTracker.Clear();
        (await db.Employees.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Id == created.Id))
            .CountryCode.Should().Be("UAE", "a stated country is recorded as stated, never swapped for another");

        var act = () => Svc(db).ChangeStatusAsync(fx.TenantId, created.Id, Activate(), Ctx(fx.TenantId), default);
        var readiness = (await act.Should().ThrowAsync<EmployeeActivationBlockedException>()).Which.Readiness;
        readiness.Blocking.Should().ContainSingle(i => i.Key == "CountryCode")
            .Which.Label.Should().Contain("'UAE' is not recognised");
    }

    [Fact]
    public async Task AKnownNonGccJurisdictionStillActivates_BecauseNoGccFloorApplies()
    {
        await using var db = CreateDb();
        var fx = await Seed(db, companyCountry: "GB");
        // A UK legal entity is a supported employer. It has no GCC floor by design, and none is invented:
        // the requirement is that the country be KNOWN, not that it be a GCC state.
        var created = await Svc(db).CreateAsync(fx.TenantId, Req(fx, "London Hire"), Ctx(fx.TenantId), default);

        db.ChangeTracker.Clear();
        (await db.Employees.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Id == created.Id))
            .CountryCode.Should().Be("GB");

        await Svc(db).ChangeStatusAsync(fx.TenantId, created.Id, Activate(), Ctx(fx.TenantId), default);

        db.ChangeTracker.Clear();
        (await db.Employees.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Id == created.Id))
            .Status.Should().Be("Active", "no Iqama is required of a UK employee, and the gate must not invent one");
    }

    // ── The helper itself ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("AE", "SA", "AE")]      // explicit wins
    [InlineData("", "SA", "SA")]        // blank falls back to the company
    [InlineData("   ", "SAU", "SA")]    // whitespace is blank; the company's ISO-3 normalises
    [InlineData(null, "", "")]          // nothing stated anywhere stays nothing — never guessed
    [InlineData("UAE", "SA", "UAE")]    // stated free text is kept, not replaced
    [InlineData("sa", null, "SA")]      // canonical casing
    public void DeriveEmployeeCountry_IsTheOneRule(string? stated, string? company, string expected)
        => HomeJurisdiction.DeriveEmployeeCountry(stated, company).Should().Be(expected);
}

// Local doubles (file-scoped, as in EmployeeCreateCountryGuardTests): the derivation under test touches
// neither document storage nor notifications, and the service takes them as constructor dependencies.
file sealed class CdDocs : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) =>
        Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument("f", "t", "u", "p"));
    public string ResolvePath(string storageUrl) => "/tmp";
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class CdNotifications : INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
}
