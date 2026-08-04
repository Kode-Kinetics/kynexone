using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// GL Phase 2 coverage: per-company GL, the extensible driver store, and client rates CRUD with the
/// compliance boundary enforced. Uses SQLite in-memory + EnsureCreated (model schema). NULLS NOT
/// DISTINCT uniqueness is a Postgres integration concern and is not exercised here.
/// </summary>
public class GlPhase2Tests
{
    private static (ZayraDbContext db, SqliteConnection conn) NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static void Attach(ControllerBase c, Guid tenantId, IEnumerable<string> perms, string? entityScopeJson = null, Guid? userId = null)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, (userId ?? Guid.NewGuid()).ToString()),
        };
        claims.AddRange(perms.Select(p => new Claim("permission", p)));
        if (entityScopeJson is not null) claims.Add(new Claim(EntityScopeClaimType, entityScopeJson));
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        c.ControllerContext = new ControllerContext { HttpContext = http };
    }

    private const string EntityScopeClaimType = "entity_scope";

    private static FinanceGlController GlCtrl(ZayraDbContext db, Guid tid, string[] perms, string? scope = null, Guid? uid = null)
    {
        var c = new FinanceGlController(db);
        Attach(c, tid, perms, scope, uid);
        return c;
    }

    private static readonly string[] AllGlPerms =
        { "finance.gl.read", "finance.gl.manage", "finance.gl.drivers.manage", FinanceGlController.PredicateAuthorPermission };

    // ── D1: per-company GL ────────────────────────────────────────────────────

    [Fact]
    public async Task SetMappings_CompanyA_LeavesCompanyBAndTenantDefaultsIntact()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        await GlDriverSeeder.SeedTenantDefaultsAsync(db, tid, CancellationToken.None);
        await db.SaveChangesAsync();

        var companyA = Guid.NewGuid();
        var companyB = Guid.NewGuid();
        // A company-specific account in each company plus reuse of a tenant-default account.
        var acctA = new GlAccount { TenantId = tid, CompanyId = companyA, Code = "5001A", Name = "A Basic", AccountType = "Expense" };
        var acctB = new GlAccount { TenantId = tid, CompanyId = companyB, Code = "5001B", Name = "B Basic", AccountType = "Expense" };
        db.GlAccounts.AddRange(acctA, acctB);
        await db.SaveChangesAsync();

        var ctrl = GlCtrl(db, tid, AllGlPerms);
        (await ctrl.SetMappings(companyA, new() { new GlMappingRequest("EARN:BASIC", acctA.Id) }, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        (await ctrl.SetMappings(companyB, new() { new GlMappingRequest("EARN:BASIC", acctB.Id) }, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();

        // Re-saving company A must NOT wipe company B or the tenant defaults.
        (await ctrl.SetMappings(companyA, new() { new GlMappingRequest("EARN:BASIC", acctA.Id) }, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();

        var bStill = await db.GlAccountMappings.IgnoreQueryFilters()
            .CountAsync(m => m.TenantId == tid && m.CompanyId == companyB && m.DriverKey == "EARN:BASIC");
        bStill.Should().Be(1, "company B override must survive a company A save");
        var tenantDefaults = await db.GlAccountMappings.IgnoreQueryFilters()
            .CountAsync(m => m.TenantId == tid && m.CompanyId == null);
        tenantDefaults.Should().BeGreaterThan(0, "tenant defaults must survive a company save");
    }

    [Fact]
    public async Task SetMappings_RejectsCrossCompanyAccountReference()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        await GlDriverSeeder.SeedTenantDefaultsAsync(db, tid, CancellationToken.None);
        await db.SaveChangesAsync();

        var companyA = Guid.NewGuid();
        var companyB = Guid.NewGuid();
        var acctB = new GlAccount { TenantId = tid, CompanyId = companyB, Code = "5001B", Name = "B Basic", AccountType = "Expense" };
        db.GlAccounts.Add(acctB);
        await db.SaveChangesAsync();

        var ctrl = GlCtrl(db, tid, AllGlPerms);
        // Company A mapping referencing company B's private account must be rejected.
        (await ctrl.SetMappings(companyA, new() { new GlMappingRequest("EARN:BASIC", acctB.Id) }, CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ListMappings_CompanyOverrideWins_TenantDefaultInheritedOtherwise()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        await GlDriverSeeder.SeedTenantDefaultsAsync(db, tid, CancellationToken.None);
        await db.SaveChangesAsync();
        var company = Guid.NewGuid();
        var acct = new GlAccount { TenantId = tid, CompanyId = company, Code = "5001C", Name = "C Basic", AccountType = "Expense" };
        db.GlAccounts.Add(acct);
        await db.SaveChangesAsync();

        var ctrl = GlCtrl(db, tid, AllGlPerms);
        await ctrl.SetMappings(company, new() { new GlMappingRequest("EARN:BASIC", acct.Id) }, CancellationToken.None);

        var res = (OkObjectResult)await ctrl.ListMappings(company, CancellationToken.None);
        var rows = Rows(res.Value!);
        var basic = rows.First(r => (string)r["driverKey"] == "EARN:BASIC");
        ((Guid?)basic["mappedAccountId"]).Should().Be(acct.Id, "company override account wins");
        ((bool)basic["inherited"]).Should().BeFalse();
        var housing = rows.First(r => (string)r["driverKey"] == "EARN:HOUSING");
        ((bool)housing["inherited"]).Should().BeTrue("unset driver inherits the tenant default");
    }

    // ── Residual Phase-1 account guards ───────────────────────────────────────

    [Fact]
    public async Task UpdateAccount_BlankName_Returns400_Not500()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var acct = new GlAccount { TenantId = tid, Code = "5001", Name = "Basic", AccountType = "Expense" };
        db.GlAccounts.Add(acct); await db.SaveChangesAsync();

        var ctrl = GlCtrl(db, tid, AllGlPerms);
        (await ctrl.UpdateAccount(acct.Id, false, new GlAccountRequest("5001", "", "Expense"), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task UpdateAccount_DeactivateWhileMapped_Requires_Force()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var acct = new GlAccount { TenantId = tid, Code = "5001", Name = "Basic", AccountType = "Expense", IsActive = true };
        db.GlAccounts.Add(acct); await db.SaveChangesAsync();
        db.GlAccountMappings.Add(new GlAccountMapping { TenantId = tid, DriverKey = "EARN:BASIC", AccountId = acct.Id, IsActive = true });
        await db.SaveChangesAsync();

        var ctrl = GlCtrl(db, tid, AllGlPerms);
        (await ctrl.UpdateAccount(acct.Id, false, new GlAccountRequest("5001", "Basic", "Expense", IsActive: false), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>("cannot deactivate a mapped account without force");
        (await ctrl.UpdateAccount(acct.Id, true, new GlAccountRequest("5001", "Basic", "Expense", IsActive: false), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>("force=true allows deactivation");
    }

    // ── D2: driver store ──────────────────────────────────────────────────────

    [Fact]
    public async Task Seed_Reproduces_CompiledRouting_ForKnownComponents()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        await GlDriverSeeder.SeedTenantDefaultsAsync(db, tid, CancellationToken.None);
        await db.SaveChangesAsync();

        var drivers = await db.GlDrivers.IgnoreQueryFilters().Where(d => d.TenantId == tid).ToListAsync();
        // 17 original + POD-B1 CASH_BANK + POD-B1b BONUS_PAYABLE / LOAN_RECEIVABLE / ADVANCE_RECEIVABLE.
        // All four additions are Category=Balancing, so the component ROUTING assertions below are
        // unaffected — ResolveDriverForComponent only ever considers Earning/Deduction rows.
        drivers.Should().HaveCount(21);
        // Golden checks against the compiled switch semantics.
        Route(drivers, "BASIC", "Salary", GlDriverCategories.Earning).Should().Be("EARN:BASIC");
        Route(drivers, "HOUSING", "Salary", GlDriverCategories.Earning).Should().Be("EARN:HOUSING");
        Route(drivers, "ANYTHING", "Bonus", GlDriverCategories.Earning).Should().Be("EARN:BONUS", "bonus source wins");
        Route(drivers, "WHATEVER", "Salary", GlDriverCategories.Earning).Should().Be("EARN:OTHER", "earning catch-all");
        Route(drivers, "GOSI-ANN-ER", "Statutory", GlDriverCategories.Deduction).Should().Be("DED:STATUTORY_ER");
        Route(drivers, "GOSI-ANN-EE", "Statutory", GlDriverCategories.Deduction).Should().Be("DED:STATUTORY_EE");
        Route(drivers, "X", "Tax", GlDriverCategories.Deduction).Should().Be("DED:TAX");
        Route(drivers, "X", "Loan", GlDriverCategories.Deduction).Should().Be("DED:LOAN");
        Route(drivers, "FIXED_DEDUCTION", "Manual", GlDriverCategories.Deduction).Should().Be("DED:FIXED_DEDUCTION");
        Route(drivers, "MISC", "Manual", GlDriverCategories.Deduction).Should().Be("DED:OTHER", "deduction catch-all");
    }

    [Fact]
    public async Task CreateDriver_PredicateAuthoring_RequiresHigherTrust()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        await GlDriverSeeder.SeedTenantDefaultsAsync(db, tid, CancellationToken.None);
        await db.SaveChangesAsync();

        // Client (manage but NOT author_predicates) may create an Exact-code custom earning driver…
        var client = GlCtrl(db, tid, new[] { "finance.gl.read", "finance.gl.manage", "finance.gl.drivers.manage" });
        (await client.CreateDriver(null, new GlDriverRequest("EARN:WELLNESS", "Wellness", GlDriverCategories.Earning, "DR", "5006", "Wellness Expense", MatchMode: "Exact", MatchComponentCode: "WELLNESS"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();

        // …but NOT a Suffix predicate (can post an unbalanced journal) without author_predicates.
        (await client.CreateDriver(null, new GlDriverRequest("EARN:SUFFIXY", "Suffixy", GlDriverCategories.Earning, "DR", "5007", "Suffixy", MatchMode: "Suffix", MatchComponentCode: "-X"), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task CreateDriver_RejectsSystemKeyShadow()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        await GlDriverSeeder.SeedTenantDefaultsAsync(db, tid, CancellationToken.None);
        await db.SaveChangesAsync();
        var ctrl = GlCtrl(db, tid, AllGlPerms);
        // Company-scoped custom driver reusing a system key must be rejected (cross-scope shadow guard).
        (await ctrl.CreateDriver(Guid.NewGuid(), new GlDriverRequest("NET_PAYABLE", "Hijack", GlDriverCategories.Balancing, "CR", "9998", "Bad", MatchMode: "Any"), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task SystemDriver_LockedFieldEdit_And_Delete_Are_Rejected()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        await GlDriverSeeder.SeedTenantDefaultsAsync(db, tid, CancellationToken.None);
        await db.SaveChangesAsync();
        var sys = await db.GlDrivers.IgnoreQueryFilters().FirstAsync(d => d.TenantId == tid && d.Key == "EARN:BASIC");
        var ctrl = GlCtrl(db, tid, AllGlPerms);

        // Change a locked routing field → 400.
        (await ctrl.UpdateDriver(sys.Id, new GlDriverRequest("EARN:BASIC", "Basic", GlDriverCategories.Earning, "DR", "5001", "Basic", MatchMode: "Any"), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
        // Delete a system driver → 400.
        (await ctrl.DeleteDriver(sys.Id, CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
    }

    // ── D3: rates ─────────────────────────────────────────────────────────────

    private static RatesController RatesCtrl(ZayraDbContext db, Guid tid, string[] perms, string? scope = null, Guid? uid = null)
    {
        var reader = new StatutoryRuleReader(db);
        var c = new RatesController(db, reader, new StatutoryRateResolver(db, reader));
        Attach(c, tid, perms, scope, uid);
        return c;
    }

    [Fact]
    public async Task CompanyRate_RejectsStatutoryKey_And_UnregisteredKey()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        await GlDriverSeeder.SeedTenantDefaultsAsync(db, tid, CancellationToken.None);
        await db.SaveChangesAsync();
        var ctrl = RatesCtrl(db, tid, new[] { "payroll.rates.read", "payroll.rates.manage" });

        (await ctrl.CreateCompanyRate(null, new CompanyRateRequest("gosi.saudi_employee_rate", "0.05", new DateOnly(2026, 1, 1)), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>("statutory key must be refused on the free-CRUD surface");
        (await ctrl.CreateCompanyRate(null, new CompanyRateRequest("qatarization.target_ratio", "0.1", new DateOnly(2026, 1, 1)), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>("Saudization/nationalization key must be refused");
        (await ctrl.CreateCompanyRate(null, new CompanyRateRequest("eosb.days_per_year_1to5", "10", new DateOnly(2026, 1, 1)), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>("statutory EOSB floor must be refused");
        (await ctrl.CreateCompanyRate(null, new CompanyRateRequest("random.unknown.key", "5", new DateOnly(2026, 1, 1)), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>("unregistered key must be refused (allow-list)");
    }

    [Fact]
    public async Task CompanyRate_FullCrud_Bounded_And_Supersedes()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        await GlDriverSeeder.SeedTenantDefaultsAsync(db, tid, CancellationToken.None);
        await db.SaveChangesAsync();
        var company = Guid.NewGuid();
        var ctrl = RatesCtrl(db, tid, new[] { "payroll.rates.read", "payroll.rates.manage" });

        // Over-max is rejected (bounded input).
        (await ctrl.CreateCompanyRate(company, new CompanyRateRequest("allowance.wellness.monthly_amount", "999999999", new DateOnly(2026, 1, 1)), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();

        var created = (OkObjectResult)await ctrl.CreateCompanyRate(company, new CompanyRateRequest("allowance.wellness.monthly_amount", "500", new DateOnly(2026, 1, 1)), CancellationToken.None);
        var id = (Guid)Prop(created.Value!, "Id")!;

        // Update supersedes: old archived, new Active row inserted.
        (await ctrl.UpdateCompanyRate(id, new CompanyRateRequest("allowance.wellness.monthly_amount", "750", new DateOnly(2026, 6, 1)), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        var rows = await db.CompanyRatePolicies.IgnoreQueryFilters().Where(p => p.TenantId == tid && p.CompanyId == company).ToListAsync();
        rows.Should().HaveCount(2);
        rows.Count(r => r.Status == CompanyPolicyStatuses.Active).Should().Be(1);
        rows.Count(r => r.Status == CompanyPolicyStatuses.Archived).Should().Be(1);
    }

    [Fact]
    public async Task StatutoryOverride_Requires_Reason_EffectiveFrom_CompanyId_ReviewBy_And_KnownKey()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        db.StatutoryRules.Add(new StatutoryRule { TenantId = null, CountryCode = "SAU", Jurisdiction = "KSA-mainland", RuleKey = "gosi.saudi_employee_rate", RuleValue = "0.09", DataType = "decimal", EffectiveFrom = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
        await db.SaveChangesAsync();
        var company = Guid.NewGuid();
        var ctrl = RatesCtrl(db, tid, new[] { "payroll.rates.statutory_override" });
        var ef = new DateOnly(2026, 1, 1);
        var rev = new DateOnly(2026, 12, 31);

        // Missing companyId
        (await ctrl.CreateStatutoryOverride(new StatutoryOverrideRequest(null, "SAU", "KSA-mainland", "gosi.saudi_employee_rate", "0.08", ef, "reason", rev), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
        // Missing reason
        (await ctrl.CreateStatutoryOverride(new StatutoryOverrideRequest(company, "SAU", "KSA-mainland", "gosi.saudi_employee_rate", "0.08", ef, "", rev), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
        // Missing reviewBy
        (await ctrl.CreateStatutoryOverride(new StatutoryOverrideRequest(company, "SAU", "KSA-mainland", "gosi.saudi_employee_rate", "0.08", ef, "reason", null), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
        // Unknown ruleKey
        (await ctrl.CreateStatutoryOverride(new StatutoryOverrideRequest(company, "SAU", "KSA-mainland", "made.up.key", "0.08", ef, "reason", rev), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
        // Valid → PendingApproval
        (await ctrl.CreateStatutoryOverride(new StatutoryOverrideRequest(company, "SAU", "KSA-mainland", "gosi.saudi_employee_rate", "0.08", ef, "regulator letter 123", rev), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        var row = await db.CompanyStatutoryOverrides.IgnoreQueryFilters().FirstAsync(o => o.TenantId == tid);
        row.Status.Should().Be(RatesController.PendingApproval);
        row.PlatformDefaultAtCreation.Should().Be("0.09");
    }

    [Fact]
    public async Task StatutoryOverride_MakerChecker_And_ResolutionPrecedence()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        db.StatutoryRules.Add(new StatutoryRule { TenantId = null, CountryCode = "SAU", Jurisdiction = "KSA-mainland", RuleKey = "gosi.saudi_employee_rate", RuleValue = "0.09", DataType = "decimal", EffectiveFrom = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
        await db.SaveChangesAsync();
        var company = Guid.NewGuid();
        var maker = Guid.NewGuid();
        var ef = new DateOnly(2026, 1, 1);

        var makerCtrl = RatesCtrl(db, tid, new[] { "payroll.rates.statutory_override" }, uid: maker);
        var created = (OkObjectResult)await makerCtrl.CreateStatutoryOverride(new StatutoryOverrideRequest(company, "SAU", "KSA-mainland", "gosi.saudi_employee_rate", "0.08", ef, "regulator letter", new DateOnly(2026, 12, 31)), CancellationToken.None);
        var id = (Guid)Prop(created.Value!, "Id")!;
        var reader = new StatutoryRuleReader(db);
        var resolver = new StatutoryRateResolver(db, reader);

        // Pending (not Active) → resolution still returns the platform default.
        (await resolver.ResolveDecimalAsync(tid, company, "SAU", "KSA-mainland", "gosi.saudi_employee_rate", ef)).Should().Be(0.09m);

        // Maker cannot approve their own override.
        var selfApprove = RatesCtrl(db, tid, new[] { "approvals.decide" }, uid: maker);
        (await selfApprove.ApproveStatutoryOverride(id, CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();

        // A second person approves → Active.
        var checker = RatesCtrl(db, tid, new[] { "approvals.decide" }, uid: Guid.NewGuid());
        (await checker.ApproveStatutoryOverride(id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        // Now the company override wins over the platform default.
        (await resolver.ResolveDecimalAsync(tid, company, "SAU", "KSA-mainland", "gosi.saudi_employee_rate", ef)).Should().Be(0.08m);
        // A different company still resolves the platform default (per-entity isolation).
        (await resolver.ResolveDecimalAsync(tid, Guid.NewGuid(), "SAU", "KSA-mainland", "gosi.saudi_employee_rate", ef)).Should().Be(0.09m);

        // Revert (archive) → back to the platform default.
        var reverter = RatesCtrl(db, tid, new[] { "payroll.rates.statutory_override" });
        (await reverter.RevertStatutoryOverride(id, CancellationToken.None)).Should().BeOfType<NoContentResult>();
        (await resolver.ResolveDecimalAsync(tid, company, "SAU", "KSA-mainland", "gosi.saudi_employee_rate", ef)).Should().Be(0.09m);
    }

    [Fact]
    public async Task StatutoryOverride_Write_Requires_HigherTrust_Permission()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        db.StatutoryRules.Add(new StatutoryRule { TenantId = null, CountryCode = "SAU", Jurisdiction = "KSA-mainland", RuleKey = "gosi.saudi_employee_rate", RuleValue = "0.09", DataType = "decimal", EffectiveFrom = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
        await db.SaveChangesAsync();
        // payroll.rates.manage alone (no statutory_override) must be forbidden on Surface B.
        var ctrl = RatesCtrl(db, tid, new[] { "payroll.rates.manage" });
        (await ctrl.CreateStatutoryOverride(new StatutoryOverrideRequest(Guid.NewGuid(), "SAU", "KSA-mainland", "gosi.saudi_employee_rate", "0.08", new DateOnly(2026, 1, 1), "reason", new DateOnly(2026, 12, 31)), CancellationToken.None))
            .Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task CompanyScopedUser_CannotWrite_AnotherCompany()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        await GlDriverSeeder.SeedTenantDefaultsAsync(db, tid, CancellationToken.None);
        await db.SaveChangesAsync();
        var companyA = Guid.NewGuid();
        var companyB = Guid.NewGuid();
        // Token scoped to company A only.
        var scope = JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { companyA } });
        var ctrl = GlCtrl(db, tid, AllGlPerms, scope);

        (await ctrl.CreateAccount(companyB, new GlAccountRequest("7001", "Sneaky", "Expense"), CancellationToken.None))
            .Should().BeOfType<ForbidResult>("a company-A user must not write into company B");
        // And cannot write group-level (tenant-default) rows either.
        (await ctrl.CreateAccount(null, new GlAccountRequest("7002", "GroupSneaky", "Expense"), CancellationToken.None))
            .Should().BeOfType<ForbidResult>("a company-scoped user must not write group defaults");
        // Own company is allowed.
        (await ctrl.CreateAccount(companyA, new GlAccountRequest("7003", "Mine", "Expense"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    // Mirrors the compiled routing precedence via the seeded driver rows (public-surface proxy for the
    // private ResolveDriverForComponent): most-specific-first, source-with beats source-without.
    private static string Route(List<GlDriver> drivers, string code, string source, string category)
    {
        bool Matches(GlDriver d) =>
            (d.MatchSource is null || d.MatchSource == source) && d.MatchMode switch
            {
                "Exact" => d.MatchComponentCode == code,
                "Suffix" => d.MatchComponentCode is not null && code.EndsWith(d.MatchComponentCode),
                "Prefix" => d.MatchComponentCode is not null && code.StartsWith(d.MatchComponentCode),
                _ => d.MatchComponentCode is null,
            };
        int Rank(string m) => m switch { "Exact" => 3, "Suffix" => 2, "Prefix" => 2, _ => 1 };
        return drivers.Where(d => d.Category == category && Matches(d))
            .OrderByDescending(d => Rank(d.MatchMode))
            .ThenByDescending(d => d.MatchSource != null)
            .ThenBy(d => d.SortOrder)
            .ThenByDescending(d => d.IsSystem)
            .ThenBy(d => d.Key, StringComparer.Ordinal)
            .First().Key;
    }

    private static List<Dictionary<string, object?>> Rows(object value)
    {
        var list = new List<Dictionary<string, object?>>();
        foreach (var item in (System.Collections.IEnumerable)value)
        {
            var dict = new Dictionary<string, object?>();
            foreach (var p in item!.GetType().GetProperties()) dict[p.Name] = p.GetValue(item);
            list.Add(dict);
        }
        return list;
    }

    private static object? Prop(object obj, string name)
    {
        var p = obj.GetType().GetProperty(name);
        if (p is not null) return p.GetValue(obj);
        // anonymous DTO wrapped as { driver = ... } etc — search one level down.
        foreach (var pi in obj.GetType().GetProperties())
        {
            var inner = pi.GetValue(obj);
            var ip = inner?.GetType().GetProperty(name);
            if (ip is not null) return ip.GetValue(inner);
        }
        return null;
    }
}
