using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Owner decisions 2026-11-12 on the hire form.
///
/// <para><b>GOSI first-registration date.</b> A Saudi hire may state it at hire (create and the draft autosave). The cohort is keyed on that
/// date and never inferred; blank stays Unknown with the payroll warning; an expatriate's value is ignored. Changing it on an existing employee
/// stays on the approval-gated change path (<c>GosiCohortEmployeeFactTests</c>), which this does not touch.</para>
///
/// <para><b>Notice period.</b> A new hire with no stated notice period gets the company's default: the existing seeded country rule
/// <c>notice_period_days</c> (SA = 60) for the employing company's country. HR can change it per person. Existing employees are never
/// back-filled, and once stored a defaulted value is indistinguishable from a typed one, so exit and final settlement read it the same.</para>
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class EmployeeHireGosiAndNoticeTests
{
    private readonly PostgresFixture _fx;
    public EmployeeHireGosiAndNoticeTests(PostgresFixture fx) => _fx = fx;

    private static EmployeeManagementService Service(ZayraDbContext db) =>
        new(db, new AuditService(db), new NullDocumentStorage(), TestNotifications.For(db));

    private static RequestContext Ctx(Guid tenantId) => new("127.0.0.1", "tests", Guid.NewGuid(), tenantId);

    private static readonly DateTime Joining = new(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);

    private static EmployeeCreateRequest Hire(string name, string nationalityCode, Guid? companyId = null) =>
        new(
            EmployeeCode: $"GN-{Guid.NewGuid():N}"[..14], ManualEmployeeCode: true, EnglishName: name,
            ArabicName: null, PreferredName: null, Gender: "Male", DateOfBirth: null, Nationality: nationalityCode == "SA" ? "Saudi Arabia" : "Pakistan", MaritalStatus: null,   // the input guard normalises the text from the code in production
            PersonalEmail: null, WorkEmail: null, MobileNumber: null, ProfilePhotoUrl: null,
            CompanyId: companyId, BranchId: null, DepartmentId: null, DesignationId: null, GradeId: null, CostCenterId: null, JobTitle: null,
            ReportingManagerEmployeeId: null, SecondLevelManagerEmployeeId: null,
            EmploymentType: "Full-time", ContractType: "Permanent", JoiningDate: Joining,
            ConfirmationDate: null, ProbationStartDate: null, ProbationEndDate: null,
            NoticePeriodDays: null, WorkLocation: null, PayrollGroup: null,
            ShiftPolicyCode: null, LeavePolicyCode: null, AttendancePolicyCode: null,
            PayrollProfile: null, SalaryBreakdown: null, ComplianceRecords: null,
            NationalityCode: nationalityCode);

    private static async Task<(Guid TenantId, Company Company)> SeedAsync(ZayraDbContext db, string country = "SA")
    {
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company { TenantId = tenantId, LegalNameEn = "Notice Co", TradeName = "Notice Co", CountryCode = country, Jurisdiction = country, EmailDomain = "notice.test" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return (tenantId, company);
    }

    private static void SeedNoticeRule(ZayraDbContext db, Guid tenantId, string country, string value, DateTime? from = null, DateTime? to = null) =>
        db.CountryPayrollRules.Add(new CountryPayrollRule
        {
            TenantId = tenantId, CountryCode = country, RuleKey = "notice_period_days", RuleValue = value, DataType = "int",
            Description = "Configurable notice period default (days)", EffectiveFrom = from ?? new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), EffectiveTo = to,
        });

    // ── GOSI first registration date ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("2024-07-02", "PreJuly2024")]
    [InlineData("2019-03-01", "PreJuly2024")]
    [InlineData("2024-07-03", "NewEntrant")]
    [InlineData("2025-02-01", "NewEntrant")]
    public async Task SaudiHire_WithAFirstRegistrationDate_IsOnTheRightCohort(string date, string cohort)
    {
        await using var db = _fx.CreateDb();
        var (tenantId, company) = await SeedAsync(db);

        var created = await Service(db).CreateAsync(tenantId, Hire("Saudi Hire", "SA", company.Id) with { GosiFirstRegisteredOn = DateOnly.Parse(date) }, Ctx(tenantId), default);

        var stored = await db.Employees.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        stored.GosiFirstRegisteredOn.Should().Be(DateOnly.Parse(date));
        GosiCohorts.Resolve(stored.GosiFirstRegisteredOn).Should().Be(cohort);
    }

    [Fact]
    public async Task SaudiHire_WithNoDate_StaysUnknown_NothingIsInferred()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, company) = await SeedAsync(db);

        var created = await Service(db).CreateAsync(tenantId, Hire("Blank Date", "SA", company.Id), Ctx(tenantId), default);

        var stored = await db.Employees.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        stored.GosiFirstRegisteredOn.Should().BeNull();
        GosiCohorts.Resolve(stored.GosiFirstRegisteredOn).Should().Be(GosiCohorts.Unknown);
    }

    [Fact]
    public async Task Expat_WithADate_HasItIgnored()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, company) = await SeedAsync(db);

        var created = await Service(db).CreateAsync(tenantId, Hire("Expat Hire", "PK", company.Id) with { GosiFirstRegisteredOn = new DateOnly(2025, 1, 1) }, Ctx(tenantId), default);

        (await db.Employees.AsNoTracking().SingleAsync(x => x.Id == created.Id)).GosiFirstRegisteredOn.Should().BeNull("the form shows the field to Saudi nationals only");
    }

    [Fact]
    public async Task DraftAutosave_KeepsTheDate_AndClearsItWhenTheNationalityStopsBeingSaudi()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, company) = await SeedAsync(db);
        var created = await Service(db).CreateAsync(tenantId, Hire("Draft Saudi", "SA", company.Id) with { GosiFirstRegisteredOn = new DateOnly(2025, 3, 1) }, Ctx(tenantId), default);

        await Service(db).UpdateAsync(tenantId, created.Id, Hire("Draft Saudi", "SA", company.Id) with { EmployeeCode = created.EmployeeCode, GosiFirstRegisteredOn = new DateOnly(2025, 4, 1) }, Ctx(tenantId), default);
        (await db.Employees.AsNoTracking().SingleAsync(x => x.Id == created.Id)).GosiFirstRegisteredOn.Should().Be(new DateOnly(2025, 4, 1));

        await Service(db).UpdateAsync(tenantId, created.Id, Hire("Draft Saudi", "PK", company.Id) with { EmployeeCode = created.EmployeeCode, GosiFirstRegisteredOn = new DateOnly(2025, 4, 1) }, Ctx(tenantId), default);
        (await db.Employees.AsNoTracking().SingleAsync(x => x.Id == created.Id)).GosiFirstRegisteredOn.Should().BeNull();
    }

    [Fact]
    public async Task AnEmployeeWhoHasStartedWork_IsNotChangedByThisDoor()
    {
        // The edit path stays approval-gated. Even if the service is handed a date for an employee who is no longer a draft, it is not written.
        await using var db = _fx.CreateDb();
        var (tenantId, company) = await SeedAsync(db);
        var created = await Service(db).CreateAsync(tenantId, Hire("Started", "SA", company.Id) with { GosiFirstRegisteredOn = new DateOnly(2019, 1, 1) }, Ctx(tenantId), default);
        var tracked = await db.Employees.SingleAsync(x => x.Id == created.Id);
        tracked.Status = "Active"; tracked.ActivatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        await Service(db).UpdateAsync(tenantId, created.Id, Hire("Started", "SA", company.Id) with { EmployeeCode = created.EmployeeCode, GosiFirstRegisteredOn = new DateOnly(2025, 9, 1) }, Ctx(tenantId), default);

        (await db.Employees.AsNoTracking().SingleAsync(x => x.Id == created.Id)).GosiFirstRegisteredOn.Should().Be(new DateOnly(2019, 1, 1));
    }

    [Fact]
    public async Task NewEntrantHire_IsPaidOnTheNewEntrantSchedule_AndAnExistingSubscriberOnNinePercent()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, company) = await SeedAsync(db);
        var entrant = await Service(db).CreateAsync(tenantId, Hire("Entrant", "SA", company.Id) with { GosiFirstRegisteredOn = new DateOnly(2025, 1, 1) }, Ctx(tenantId), default);
        var existing = await Service(db).CreateAsync(tenantId, Hire("Existing", "SA", company.Id) with { GosiFirstRegisteredOn = new DateOnly(2016, 5, 10) }, Ctx(tenantId), default);
        var unknown = await Service(db).CreateAsync(tenantId, Hire("Unknown", "SA", company.Id), Ctx(tenantId), default);

        // Payroll reads exactly these two stored facts (PayrollRunProcessor: e.Nationality, e.GosiFirstRegisteredOn).
        async Task<StatutoryDeductionResult> PayAsync(int id)
        {
            var e = await db.Employees.AsNoTracking().SingleAsync(x => x.Id == id);
            return await new KsaDeductionCalculator(GosiNewEntrantScheduleTests.SeededRules.Instance).CalculateAsync(
                new StatutoryDeductionInput(Guid.Empty, Guid.Empty, new SalaryBreakdown(10_000m, 3_000m, 0m, 0m), e.Nationality, "Indefinite", 2026, 7)
                { SocialInsuranceFirstRegisteredOn = e.GosiFirstRegisteredOn });
        }
        var entrantPay = await PayAsync(entrant.Id);
        var existingPay = await PayAsync(existing.Id);
        var unknownPay = await PayAsync(unknown.Id);

        entrantPay.SocialInsuranceCohort.Should().Be(GosiCohorts.NewEntrant);
        entrantPay.Lines.Single(l => l.Code == "GOSI-ANN-EE").Rate.Should().Be(0.10m, "a new entrant is on 10% from July 2026");
        existingPay.SocialInsuranceCohort.Should().Be(GosiCohorts.PreJuly2024);
        existingPay.Lines.Single(l => l.Code == "GOSI-ANN-EE").Rate.Should().Be(0.09m);
        unknownPay.SocialInsuranceCohort.Should().Be(GosiCohorts.Unknown, "no date is never read as a new entrant");
    }

    // ── Notice period ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NewHire_WithNoNoticePeriod_GetsTheCompanysDefault_60ForKsa()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, company) = await SeedAsync(db);
        SeedNoticeRule(db, tenantId, "SA", "60");
        await db.SaveChangesAsync();

        var created = await Service(db).CreateAsync(tenantId, Hire("Defaulted", "SA", company.Id), Ctx(tenantId), default);

        (await db.Employees.AsNoTracking().SingleAsync(x => x.Id == created.Id)).NoticePeriodDays.Should().Be(60);
    }

    [Theory]
    [InlineData(45)]
    [InlineData(0)]
    public async Task AStatedNoticePeriod_IsKept_ZeroIncluded(int typed)
    {
        await using var db = _fx.CreateDb();
        var (tenantId, company) = await SeedAsync(db);
        SeedNoticeRule(db, tenantId, "SA", "60");
        await db.SaveChangesAsync();

        var created = await Service(db).CreateAsync(tenantId, Hire("Typed", "SA", company.Id) with { NoticePeriodDays = typed }, Ctx(tenantId), default);

        (await db.Employees.AsNoTracking().SingleAsync(x => x.Id == created.Id)).NoticePeriodDays.Should().Be(typed);
    }

    [Fact]
    public async Task NoRuleForTheCountry_OrAnUnusableOne_MeansNoDefault_NothingIsGuessed()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, company) = await SeedAsync(db);
        SeedNoticeRule(db, tenantId, "AE", "30");                                     // another country's rule
        await db.SaveChangesAsync();
        var noRule = await Service(db).CreateAsync(tenantId, Hire("No Rule", "SA", company.Id), Ctx(tenantId), default);
        (await db.Employees.AsNoTracking().SingleAsync(x => x.Id == noRule.Id)).NoticePeriodDays.Should().BeNull();

        SeedNoticeRule(db, tenantId, "SA", "sixty");                                  // not a number
        await db.SaveChangesAsync();
        var badRule = await Service(db).CreateAsync(tenantId, Hire("Bad Rule", "SA", company.Id), Ctx(tenantId), default);
        (await db.Employees.AsNoTracking().SingleAsync(x => x.Id == badRule.Id)).NoticePeriodDays.Should().BeNull();
    }

    [Fact]
    public async Task TheRuleInForceWins_AnExpiredOrOtherTenantsRuleIsNotUsed()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, company) = await SeedAsync(db);
        var other = await PostgresFixture.SeedMinimalTenant(db);
        SeedNoticeRule(db, tenantId, "SA", "30", from: new DateTime(2018, 1, 1, 0, 0, 0, DateTimeKind.Utc), to: new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc)); // ended
        SeedNoticeRule(db, tenantId, "SA", "45", from: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        SeedNoticeRule(db, tenantId, "SA", "90", from: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));   // the latest in force
        SeedNoticeRule(db, other, "SA", "15");                                                                // another tenant
        await db.SaveChangesAsync();

        var created = await Service(db).CreateAsync(tenantId, Hire("Latest", "SA", company.Id), Ctx(tenantId), default);

        (await db.Employees.AsNoTracking().SingleAsync(x => x.Id == created.Id)).NoticePeriodDays.Should().Be(90);
    }

    [Fact]
    public async Task ExistingEmployeesWithABlankNotice_AreNotBackFilled()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, company) = await SeedAsync(db);
        var before = await Service(db).CreateAsync(tenantId, Hire("Before The Rule", "SA", company.Id), Ctx(tenantId), default);   // no rule yet: blank
        SeedNoticeRule(db, tenantId, "SA", "60");
        await db.SaveChangesAsync();

        var after = await Service(db).CreateAsync(tenantId, Hire("After The Rule", "SA", company.Id), Ctx(tenantId), default);
        // An autosave of the older draft that states no notice period leaves it blank too: only a CREATE applies the default.
        await Service(db).UpdateAsync(tenantId, before.Id, Hire("Before The Rule", "SA", company.Id) with { EmployeeCode = before.EmployeeCode }, Ctx(tenantId), default);

        (await db.Employees.AsNoTracking().SingleAsync(x => x.Id == after.Id)).NoticePeriodDays.Should().Be(60);
        (await db.Employees.AsNoTracking().SingleAsync(x => x.Id == before.Id)).NoticePeriodDays.Should().BeNull("no back-fill: it is not given a notice period behind HR's back");
    }

    [Fact]
    public async Task ADefaultedNoticePeriod_ExitsExactlyAsOneTypedAs60_AndABlankOneStaysAtZeroNotice()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, company) = await SeedAsync(db);
        var blankBefore = await Service(db).CreateAsync(tenantId, Hire("Blank", "SA", company.Id), Ctx(tenantId), default);
        SeedNoticeRule(db, tenantId, "SA", "60");
        await db.SaveChangesAsync();
        var defaulted = await Service(db).CreateAsync(tenantId, Hire("Defaulted", "SA", company.Id), Ctx(tenantId), default);
        var typed = await Service(db).CreateAsync(tenantId, Hire("Typed", "SA", company.Id) with { NoticePeriodDays = 60 }, Ctx(tenantId), default);
        foreach (var e in await db.Employees.Where(x => x.TenantId == tenantId).ToListAsync()) { e.Status = "Active"; e.JoiningDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc); }
        await db.SaveChangesAsync();

        async Task<EmployeeOffboarding> ExitAsync(int id)
        {
            var controller = new OffboardingController(db, new AuditService(db))
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                        {
                            new Claim("tenant_id", tenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                            new Claim(ClaimTypes.Name, "HR Tester"), new Claim(ClaimTypes.Role, "Admin"),
                        }, "test")),
                    },
                },
            };
            // -1 = "use the contractual notice on the employee record", the path settlement relies on.
            var result = await controller.Initiate(new InitiateOffboardingRequest(id, "Resignation", null, new DateOnly(2026, 12, 1), NoticePeriodDays: -1, LastWorkingDay: null), CancellationToken.None);
            result.Should().BeOfType<OkObjectResult>();
            return await db.EmployeeOffboardings.AsNoTracking().SingleAsync(x => x.EmployeeId == id);
        }

        var a = await ExitAsync(defaulted.Id);
        var b = await ExitAsync(typed.Id);
        var c = await ExitAsync(blankBefore.Id);

        (a.NoticePeriodDays, a.LastWorkingDay).Should().Be((b.NoticePeriodDays, b.LastWorkingDay), "once stored, a defaulted notice period is the same number as a typed one");
        a.NoticePeriodDays.Should().Be(60);
        a.LastWorkingDay.Should().Be(new DateOnly(2026, 12, 1).AddDays(60));
        c.NoticePeriodDays.Should().Be(0, "an employee with a blank notice settles exactly as before: no notice, nothing back-filled");
    }
}
