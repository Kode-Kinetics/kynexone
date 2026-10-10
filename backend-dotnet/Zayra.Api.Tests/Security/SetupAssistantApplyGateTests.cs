using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Setup;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// P1, the same class as #199: the setup assistant's Apply wrote its legal entity, branches, departments,
/// grades, cost centres and designations straight to the database. It skipped the Setup forms' gates — the
/// company creation gate (plan limit, single-company account, platform-controlled, draft approval), the ISO
/// country check, the branch no-move rule and the caller's company scope — and wrote one bulk audit row.
/// On real PostgreSQL; every test asserts on persisted rows.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class SetupAssistantApplyGateTests
{
    private readonly PostgresFixture _fx;
    public SetupAssistantApplyGateTests(PostgresFixture fx) => _fx = fx;

    private async Task<(Guid Tenant, Guid Existing)> SeedAsync(
        string accountType = TenantAccountTypes.Group, string mode = CompanyCreationModes.GroupSelfServiceWithinLimit, int maxCompanies = 10)
    {
        await using var db = _fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(db);
        var t = await db.Tenants.SingleAsync(x => x.Id == tenant);
        t.AccountType = accountType;
        t.CompanyCreationMode = mode;
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenant, Plan = "Enterprise", Status = "Active", MaxCompanies = maxCompanies, MaxEmployees = 100 });
        var existing = new Company { TenantId = tenant, LegalNameEn = "Existing Co", CountryCode = "SA", Jurisdiction = "SA", RegistrationNumber = "REG-EXISTING", DefaultCurrency = "SAR", IsActive = true };
        db.Companies.Add(existing);
        await db.SaveChangesAsync();
        return (tenant, existing.Id);
    }

    private static SetupAssistantController Controller(ZayraDbContext db, Guid tenant, Guid[]? scopedTo = null) =>
        new(db, null!, new AuditService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                        new Claim(ClaimTypes.Role, "Admin"), new Claim("permission", "organization.setup.apply"),
                        new Claim("permission", "organization.write"),
                        new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(scopedTo is null
                            ? new { v = 2, m = "group", c = Array.Empty<Guid>() }
                            : new { v = 2, m = "companies", c = scopedTo })),
                    }, "Test")),
                },
            },
        };

    private static ApplySetupRequest Request(string? legalEntity, string country = "SA", SetupDraft? draft = null) =>
        new(draft ?? SetupDraft.Empty(), country, "SAR", legalEntity);

    private async Task<int> Companies(Guid tenant)
    {
        await using var verify = _fx.CreateDb();
        return await verify.Companies.IgnoreQueryFilters().CountAsync(c => c.TenantId == tenant);
    }

    private static string Refusal(IActionResult result)
    {
        var refused = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, refused.StatusCode);
        return JsonSerializer.Serialize(refused.Value, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    [Fact]
    public async Task ASingleCompanyAccount_CannotAddALegalEntity_ThroughTheAssistant()
    {
        var (tenant, _) = await SeedAsync(accountType: TenantAccountTypes.SingleCompany);
        await using var db = _fx.CreateDb();
        var result = await Controller(db, tenant).Apply(Request("Second Co"), CancellationToken.None);

        Assert.Equal(1, await Companies(tenant));
        Assert.Contains("single-company account", Refusal(result));
    }

    [Fact]
    public async Task ThePlansCompanyLimit_AndPlatformControlledCreation_AreEnforced()
    {
        var (limited, _) = await SeedAsync(maxCompanies: 1);
        await using (var db = _fx.CreateDb())
        {
            var result = await Controller(db, limited).Apply(Request("Second Co"), CancellationToken.None);
            Assert.Equal(1, await Companies(limited));
            Assert.Contains("Your plan allows up to 1", Refusal(result));
        }
        var (controlled, _) = await SeedAsync(mode: CompanyCreationModes.PlatformControlled);
        await using (var db = _fx.CreateDb())
        {
            var result = await Controller(db, controlled).Apply(Request("Second Co"), CancellationToken.None);
            Assert.Equal(1, await Companies(controlled));
            Assert.Contains("managed by the platform", Refusal(result));
        }
    }

    [Fact]
    public async Task DraftApprovalMode_CreatesTheLegalEntityAsAnInactiveDraft()
    {
        var (tenant, _) = await SeedAsync(mode: CompanyCreationModes.GroupDraftPlatformApproval);
        await using var db = _fx.CreateDb();
        Assert.IsType<OkObjectResult>(await Controller(db, tenant).Apply(Request("Second Co"), CancellationToken.None));

        await using var verify = _fx.CreateDb();
        var created = await verify.Companies.IgnoreQueryFilters().SingleAsync(c => c.TenantId == tenant && c.LegalNameEn == "Second Co");
        Assert.False(created.IsActive);
        Assert.Equal(CompanyApprovalStatuses.Draft, created.ApprovalStatus);
    }

    [Fact]
    public async Task AnUnrecognisedCountry_IsRefused_AndNothingIsWritten()
    {
        var (tenant, _) = await SeedAsync();
        await using var db = _fx.CreateDb();
        var draft = SetupDraft.Empty() with { Departments = [new DraftDepartment("OPS", "Operations")] };
        var result = await Controller(db, tenant).Apply(Request("Second Co", country: "Saudi", draft: draft), CancellationToken.None);

        Assert.Equal(1, await Companies(tenant));
        await using var verify = _fx.CreateDb();
        Assert.False(await verify.Departments.IgnoreQueryFilters().AnyAsync(d => d.TenantId == tenant));
        Assert.Contains("Unrecognized country code 'SAUDI'", Refusal(result));
    }

    [Fact]
    public async Task EachCompanyCanUseTheSameBranchCode_ThroughTheAssistant()
    {
        var (tenant, existing) = await SeedAsync();
        await using (var seed = _fx.CreateDb())
        {
            seed.Companies.Add(new Company { TenantId = tenant, LegalNameEn = "Other Co", CountryCode = "SA", Jurisdiction = "SA", RegistrationNumber = "REG-OTHER", DefaultCurrency = "SAR", IsActive = true });
            seed.Branches.Add(new Branch { TenantId = tenant, CompanyId = existing, Code = "HQ", NameEn = "Head Office" });
            await seed.SaveChangesAsync();
        }
        await using var db = _fx.CreateDb();
        var draft = SetupDraft.Empty() with { Branches = [new DraftBranch("HQ", "Head Office", "Riyadh", true)] };
        var result = await Controller(db, tenant).Apply(Request("Other Co", draft: draft), CancellationToken.None);

        await using var verify = _fx.CreateDb();
        Assert.IsType<OkObjectResult>(result);
        var branches = await verify.Branches.IgnoreQueryFilters().Where(b => b.TenantId == tenant && b.Code == "HQ").ToListAsync();
        Assert.Equal(2, branches.Count);
        Assert.Contains(branches, branch => branch.CompanyId == existing);
        Assert.Contains(branches, branch => branch.CompanyId != existing && branch.City == "Riyadh");
    }

    [Fact]
    public async Task ACompanyScopedCaller_CannotCreateALegalEntity_OrWriteIntoACompanyOutsideTheirScope()
    {
        var (tenant, existing) = await SeedAsync();
        Guid other;
        await using (var seed = _fx.CreateDb())
        {
            var o = new Company { TenantId = tenant, LegalNameEn = "Other Co", CountryCode = "SA", Jurisdiction = "SA", RegistrationNumber = "REG-OTHER", DefaultCurrency = "SAR", IsActive = true };
            seed.Companies.Add(o);
            await seed.SaveChangesAsync();
            other = o.Id;
        }
        await using (var db = _fx.CreateDb())
        {
            var result = await Controller(db, tenant, scopedTo: [existing]).Apply(Request("Brand New Co"), CancellationToken.None);
            Assert.Equal(2, await Companies(tenant));
            Assert.Contains("group-scope", Refusal(result));
        }
        await using (var db = _fx.CreateDb())
        {
            var draft = SetupDraft.Empty() with { Branches = [new DraftBranch("JED", "Jeddah", "Jeddah", false)] };
            var result = await Controller(db, tenant, scopedTo: [existing]).Apply(Request("Other Co", draft: draft), CancellationToken.None);
            await using var verify = _fx.CreateDb();
            Assert.False(await verify.Branches.IgnoreQueryFilters().AnyAsync(b => b.TenantId == tenant && b.CompanyId == other));
            Assert.Contains("outside your company scope", Refusal(result));
        }
    }

    [Fact]
    public async Task ExplicitCompanyTarget_RejectsAStaleOrMismatchedLegalName()
    {
        var (tenant, existing) = await SeedAsync();
        await using var db = _fx.CreateDb();
        var draft = SetupDraft.Empty() with { Branches = [new DraftBranch("JED", "Jeddah", "Jeddah", false)] };

        var result = await Controller(db, tenant).Apply(
            new ApplySetupRequest(draft, "SA", "SAR", "A renamed or different company", existing), CancellationToken.None);

        Assert.Contains("does not match the selected company", Refusal(result));
        await using var verify = _fx.CreateDb();
        Assert.False(await verify.Branches.IgnoreQueryFilters().AnyAsync(b => b.TenantId == tenant && b.Code == "JED"));
    }

    [Fact]
    public async Task CompanyAdministrator_CannotApplyGroupSharedCatalogs()
    {
        var (tenant, existing) = await SeedAsync();
        await using var db = _fx.CreateDb();
        var draft = SetupDraft.Empty() with
        {
            Shifts = [new DraftShift("DAY", "Day shift", "08:00", "17:00", 60, "#2563eb")]
        };

        var result = await Controller(db, tenant, scopedTo: [existing]).Apply(
            new ApplySetupRequest(draft, "SA", "SAR", "Existing Co", existing), CancellationToken.None);

        Assert.Contains("group-shared shifts and working week", Refusal(result));
        await using var verify = _fx.CreateDb();
        Assert.False(await verify.ShiftDefinitions.IgnoreQueryFilters().AnyAsync(s => s.TenantId == tenant));
    }

    [Fact]
    public async Task CompanyHolidayDraft_NeverReusesASiblingOrSharedCalendar()
    {
        var (tenant, existing) = await SeedAsync();
        Guid other;
        Guid existingCalendar;
        await using (var seed = _fx.CreateDb())
        {
            var sibling = new Company
            {
                TenantId = tenant, LegalNameEn = "Other Co", CountryCode = "SA", Jurisdiction = "SA",
                RegistrationNumber = "REG-OTHER-HOLIDAY", DefaultCurrency = "SAR", IsActive = true,
            };
            seed.Companies.Add(sibling);
            var calendar = new PublicHolidayCalendar
            {
                TenantId = tenant, CompanyId = existing, CountryCode = "SA", CalendarYear = 2027, Name = "Existing Co Holidays",
            };
            seed.PublicHolidayCalendars.Add(calendar);
            await seed.SaveChangesAsync();
            other = sibling.Id;
            existingCalendar = calendar.Id;
        }

        var draft = SetupDraft.Empty() with
        {
            HolidayCalendar = new DraftHolidayCalendar("Other Co Holidays", 2027,
            [
                new DraftHoliday("Founding Day", "يوم التأسيس", "2027-02-22", false, false, "National", string.Empty),
            ]),
        };
        await using (var db = _fx.CreateDb())
        {
            Assert.IsType<OkObjectResult>(await Controller(db, tenant).Apply(
                new ApplySetupRequest(draft, "SA", "SAR", "Other Co", other), CancellationToken.None));
        }

        await using var verify = _fx.CreateDb();
        Assert.False(await verify.PublicHolidays.IgnoreQueryFilters().AnyAsync(holiday => holiday.CalendarId == existingCalendar));
        var otherCalendar = await verify.PublicHolidayCalendars.IgnoreQueryFilters().SingleAsync(calendar =>
            calendar.TenantId == tenant && calendar.CompanyId == other && calendar.CalendarYear == 2027);
        Assert.True(await verify.PublicHolidays.IgnoreQueryFilters().AnyAsync(holiday => holiday.CalendarId == otherCalendar.Id));
    }

    [Fact]
    public async Task CodesAreNormalisedLikeTheForms_AndEveryOrgEntityGetsItsOwnAuditRow()
    {
        var (tenant, _) = await SeedAsync();
        await using var db = _fx.CreateDb();
        var draft = SetupDraft.Empty() with
        {
            Branches = [new DraftBranch("hq-1", "Head Office", "Riyadh", true)],
            Departments = [new DraftDepartment("ops", "Operations")],
            Grades = [new DraftGrade("g1", "Grade 1", "Staff", 1, 5000, 7500, 10000, "SAR")],
            CostCenters = [new DraftCostCenter("cc-ops", "Operations", "ops")],
            Designations = [new DraftDesignation("ops-off", "Operations Officer", "ops", "g1", "Staff", false, 1)],
        };
        Assert.IsType<OkObjectResult>(await Controller(db, tenant).Apply(Request("New Co", draft: draft), CancellationToken.None));

        await using var verify = _fx.CreateDb();
        var company = await verify.Companies.IgnoreQueryFilters().SingleAsync(c => c.TenantId == tenant && c.LegalNameEn == "New Co");
        var branch = await verify.Branches.IgnoreQueryFilters().SingleAsync(b => b.TenantId == tenant);
        var dept = await verify.Departments.IgnoreQueryFilters().SingleAsync(b => b.TenantId == tenant);
        var grade = await verify.Grades.IgnoreQueryFilters().SingleAsync(b => b.TenantId == tenant);
        var cc = await verify.CostCenters.IgnoreQueryFilters().SingleAsync(b => b.TenantId == tenant);
        var desig = await verify.Designations.IgnoreQueryFilters().SingleAsync(b => b.TenantId == tenant);
        Assert.Equal(new[] { "HQ-1", "OPS", "G1", "CC-OPS", "OPS-OFF" }, new[] { branch.Code, dept.Code, grade.Code, cc.Code, desig.Code });

        var audits = await verify.AuditLogs.IgnoreQueryFilters().Where(a => a.TenantId == tenant).ToListAsync();
        foreach (var (action, id) in new[]
                 {
                     ("organization.company_created", company.Id), ("organization.branch_created", branch.Id),
                     ("organization.department_created", dept.Id), ("organization.grade_created", grade.Id),
                     ("organization.cost_center_created", cc.Id), ("organization.designation_created", desig.Id),
                 })
            Assert.Contains(audits, a => a.Action == action && a.EntityId == id.ToString() && a.Metadata!.Contains("setup_assistant"));
        Assert.Contains(audits, a => a.Action == "setup.assistant_applied");
    }

    private sealed class CompanyRacer : Microsoft.EntityFrameworkCore.Diagnostics.DbTransactionInterceptor
    {
        public Func<Task>? OnBegin;
        public override async ValueTask<System.Data.Common.DbTransaction> TransactionStartedAsync(System.Data.Common.DbConnection connection,
            Microsoft.EntityFrameworkCore.Diagnostics.TransactionEndEventData eventData, System.Data.Common.DbTransaction result, CancellationToken ct = default)
        {
            if (OnBegin is { } f) { OnBegin = null; await f(); }
            return result;
        }
    }

    [Fact]
    public async Task TheCompanyGate_IsAskedAgainInsideTheSave_SoACompanyCreatedMeanwhileStillCounts()
    {
        // A single-company account with NO company yet: the gate allows one. Between the gate and the save another
        // writer creates a company; the in-transaction re-check must refuse the assistant's, not make it the second.
        Guid tenant;
        await using (var seed = _fx.CreateDb())
        {
            tenant = await PostgresFixture.SeedMinimalTenant(seed);
            (await seed.Tenants.SingleAsync(x => x.Id == tenant)).AccountType = TenantAccountTypes.SingleCompany;
            await seed.SaveChangesAsync();
        }
        var racer = new CompanyRacer
        {
            OnBegin = async () =>
            {
                await using var other = _fx.CreateDb();
                other.Companies.Add(new Company { TenantId = tenant, LegalNameEn = "Racer Co", CountryCode = "SA", Jurisdiction = "SA", RegistrationNumber = "REG-RACER", DefaultCurrency = "SAR", IsActive = true });
                await other.SaveChangesAsync();
            },
        };
        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fx.ConnectionString, PostgresFixture.ProductionProviderOptions)
            .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance)
            .AddInterceptors(Zayra.Api.Infrastructure.Data.AdvisoryXactLockGuardInterceptor.Instance)
            .AddInterceptors(racer)
            .Options;
        await using var db = new ZayraDbContext(options);
        var result = await Controller(db, tenant).Apply(Request("First Co", country: "sa"), CancellationToken.None);

        Assert.Null(racer.OnBegin);
        Assert.Contains("single-company account", Refusal(result));
        await using var verify = _fx.CreateDb();
        Assert.Equal(new[] { "Racer Co" }, await verify.Companies.IgnoreQueryFilters().Where(c => c.TenantId == tenant).Select(c => c.LegalNameEn).ToArrayAsync());
    }

    [Fact]
    public async Task ALowerCaseCountry_IsStoredUpperCase_LikeTheForms()
    {
        var (tenant, _) = await SeedAsync();
        await using var db = _fx.CreateDb();
        var draft = SetupDraft.Empty() with { Branches = [new DraftBranch("RUH", "Riyadh", "Riyadh", true)] };
        Assert.IsType<OkObjectResult>(await Controller(db, tenant).Apply(Request("Lower Co", country: " sa ", draft: draft), CancellationToken.None));
        await using var verify = _fx.CreateDb();
        Assert.Equal("SA", (await verify.Companies.IgnoreQueryFilters().SingleAsync(c => c.TenantId == tenant && c.LegalNameEn == "Lower Co")).CountryCode);
        Assert.Equal("SA", (await verify.Branches.IgnoreQueryFilters().SingleAsync(b => b.TenantId == tenant && b.Code == "RUH")).CountryCode);
    }
}
