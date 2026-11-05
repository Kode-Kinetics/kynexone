using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CompanySetup;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.CompanySetup;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.WorkWeek;
using Zayra.Api.Models;
using V = Zayra.Api.Application.CompanySetup.CompanySetupVocabulary;

namespace Zayra.Api.Tests;

/// <summary>
/// The wizard for ESTABLISHED companies switching payroll from another system (migrationMode / previousSystem, go-live cutover).
/// Database-free (in-memory) so it runs without Docker: it covers the planner, the prefill and the lock rule. The commit itself
/// (Active cutover creation, audit row) is covered by the Postgres suite, CompanySetupExistingModePostgresTests.
/// </summary>
public class CompanySetupGoLiveTests
{
    private sealed record World(DbContextOptions<ZayraDbContext> Options, Guid TenantId, Guid AdminId, Guid CompanyId, Guid CutoverId);

    private static ZayraDbContext Open(DbContextOptions<ZayraDbContext> o) => new(o);

    private static async Task<World> SeedAsync(string status = CutoverStatuses.Active, string source = "SAP", params (int Year, int Month, string Status)[] runs)
    {
        var options = new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = Open(options);
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Group", Slug = $"g-{tenantId:N}", AccountType = TenantAccountTypes.Group,
            CompanyCreationMode = CompanyCreationModes.GroupSelfServiceWithinLimit });
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenantId, Plan = "Group", Status = SubscriptionStatuses.Active, MaxCompanies = 10, MaxUsers = 0, MaxAdminUsers = 0 });
        var adminRole = new Role { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Admin", NormalizedName = "ADMIN", Description = "Admin", IsSystem = true, IsActive = true, AuthorityLevel = 1 };
        db.Roles.Add(adminRole);
        var apply = new Permission { Key = "organization.setup.apply", Module = "Test", Description = "apply" };
        db.Permissions.Add(apply);
        db.RolePermissions.Add(new RolePermission { RoleId = adminRole.Id, PermissionId = apply.Id });
        db.GCCComplianceSettings.Add(new GCCComplianceSetting { TenantId = tenantId, CountryCode = "SA", EosbEnabled = true, WeekendDays = "Fri,Sat", WorkWeek = "Sun-Thu", WpsAgentId = "" });
        var adminId = Guid.NewGuid();
        db.Users.Add(new User { Id = adminId, TenantId = tenantId, Email = $"a.{adminId:N}@g.example", NormalizedEmail = $"A.{adminId:N}@G.EXAMPLE", FullName = "Admin",
            PasswordHash = "x", Status = "Active", AccessMode = AccessModes.FullPortal, IsActive = true, IsEmailConfirmed = true, IsGroupScope = true });
        db.UserRoles.Add(new UserRole { UserId = adminId, RoleId = adminRole.Id });
        var company = new Company { TenantId = tenantId, LegalNameEn = "Gulf Co", CountryCode = "SA", DefaultCurrency = "SAR", RegistrationNumber = "1010000077",
            IsActive = true, ApprovalStatus = CompanyApprovalStatuses.Active };
        db.Companies.Add(company);
        db.Branches.Add(new Branch { TenantId = tenantId, CompanyId = company.Id, Code = "HQ", NameEn = "HQ", City = "Riyadh", CountryCode = "SA", TimeZoneId = "Asia/Riyadh", IsHeadOffice = true });
        db.Designations.Add(new Designation { TenantId = tenantId, CompanyId = company.Id, Code = "DES1", TitleEn = "Officer" });
        var cutover = new CompanyCutover { TenantId = tenantId, CompanyId = company.Id, CutoverDate = new DateOnly(2026, 6, 1), Status = status, SourceSystem = source };
        db.CompanyCutovers.Add(cutover);
        foreach (var (year, month, runStatus) in runs)
            db.PayrollRuns.Add(new PayrollRun { TenantId = tenantId, CompanyId = company.Id, Year = year, Month = month, Status = runStatus });
        await db.SaveChangesAsync();
        return new World(options, tenantId, adminId, company.Id, cutover.Id);
    }

    private static CompanySetupService Service(ZayraDbContext db)
    {
        var audit = new AuditService(db);
        return new CompanySetupService(db, new OrganizationSetupService(db, audit),
            new AccessManagementService(db, new Pbkdf2PasswordHasher(), audit, new FakeTokens()), new WorkWeekService(db), new NoEmail());
    }

    private static CompanySetupCaller Caller(World w, Guid? userId = null) =>
        new(w.TenantId, userId ?? w.AdminId, true, false, new RequestContext("127.0.0.1", "tests", userId ?? w.AdminId, w.TenantId), null);

    private static async Task<CompanySetupDraftBody> PrefillAsync(World w)
    {
        await using var db = Open(w.Options);
        var prefill = await Service(db).GetExistingDefaultsAsync(Caller(w), w.CompanyId, default);
        prefill.Status.Should().Be(200, JsonSerializer.Serialize(prefill.Error));
        return prefill.Value!;
    }

    private static async Task<CompanySetupPlan> DryRunAsync(World w, CompanySetupDraftBody body, CompanySetupCaller? caller = null)
    {
        await using var db = Open(w.Options);
        return await Service(db).DryRunAsync(caller ?? Caller(w), body, default);
    }

    private static CompanySetupDraftBody NewBody(string month, string? mode, string? previousSystem) =>
        new(V.StartFromDefaults, null,
            new CompanySetupCompany("Switcher Co", "شركة التحول", null, "SA", "SAR", "2010000000", "7-1234567", null, null, null, null, null, null, null, month,
                MigrationMode: mode, PreviousSystem: previousSystem),
            [new CompanySetupBranch("HQ", "Head office", null, "Riyadh", "Asia/Riyadh", true, [])], [], [], [], [],
            new CompanySetupRules(new CompanySetupWorkWeek("Fri,Sat", false), new CompanySetupPackChoice(true), new CompanySetupPackChoice(true),
                new CompanySetupEmployeeNumbering("SWC", true, false, true, true, 4, 1)),
            new CompanySetupCompanyAdmin(V.AdminGroupOnly, null, null, null, null));

    [Fact]
    public async Task Planner_validates_the_migration_mode_and_the_previous_system_for_a_new_company()
    {
        var w = await SeedAsync();
        Task<CompanySetupPlan> Plan(string month, string? mode, string? system) => DryRunAsync(w, NewBody(month, mode, system));
        var thisMonth = DateTime.UtcNow.ToString("yyyy-MM");

        (await Plan("2026-12", "bogus", null)).Problems.Should().Contain(p => p.Code == "migration_mode_invalid" && p.Field == "company.migrationMode" && p.MessageAr != null);
        (await Plan("2026-12", "Switching", null)).Problems.Should().Contain(p => p.Code == "migration_mode_invalid", "the vocabulary is exact");
        (await Plan("2026-12", "switching", new string('x', 121))).Problems.Should().Contain(p => p.Code == "previous_system_too_long" && p.Field == "company.previousSystem");
        (await Plan("2026-12", "switching", new string('x', 120))).Problems.Should().NotContain(p => p.Code.StartsWith("previous_system"));
        (await Plan("2026-12", "switching", "SAP\u0000x")).Problems.Should().Contain(p => p.Code == "previous_system_invalid");
        (await Plan("2026-12", "switching", "line\nbreak")).Problems.Should().Contain(p => p.Code == "previous_system_invalid");

        (await Plan("2014-12", "switching", "SAP")).Problems.Should().Contain(p => p.Code == "first_payroll_month_out_of_range");
        (await Plan(DateTime.UtcNow.AddMonths(14).ToString("yyyy-MM"), "switching", "SAP")).Problems.Should().Contain(p => p.Code == "first_payroll_month_out_of_range");

        var past = await Plan("2026-01", "switching", "SAP");
        past.Problems.Should().NotContain(p => p.Field == "company.firstPayrollMonth" || p.Field == "company.migrationMode" || p.Field == "company.previousSystem",
            JsonSerializer.Serialize(past.Problems));
        past.Warnings.Should().NotContain(x => x.Code == "first_payroll_month_past", "a past month is normal when switching");
        (await Plan(thisMonth, "switching", "SAP")).Warnings.Should().NotContain(x => x.Code == "first_payroll_month_past");
        (await Plan("2026-01", "new", null)).Warnings.Should().Contain(x => x.Code == "first_payroll_month_past", "kept for a brand-new company");
        (await Plan("2026-01", null, null)).Warnings.Should().Contain(x => x.Code == "first_payroll_month_past", "absent means new");
        var item = (await Plan("2026-01", "switching", "SAP")).Items.Single(i => i.Area == V.Areas.Cutover);
        item.Action.Should().Be(V.ActionCreate);
    }

    [Fact]
    public async Task The_minimal_switching_body_the_Postgres_tests_create_companies_from_plans_without_problems()
    {
        var w = await SeedAsync();
        var plan = await DryRunAsync(w, NewBody("2026-03", "switching", "SAP"));
        plan.Ok.Should().BeTrue(JsonSerializer.Serialize(plan.Problems));
    }

    [Fact]
    public async Task A_caller_without_the_setup_permission_cannot_set_switching_mode()
    {
        var w = await SeedAsync();
        // A group-scope user holding a role with no permissions at all.
        Guid weakId;
        await using (var db = Open(w.Options))
        {
            var role = new Role { Id = Guid.NewGuid(), TenantId = w.TenantId, Name = "Reader", NormalizedName = "READER", Description = "r", IsActive = true };
            db.Roles.Add(role);
            weakId = Guid.NewGuid();
            db.Users.Add(new User { Id = weakId, TenantId = w.TenantId, Email = $"w.{weakId:N}@g.example", NormalizedEmail = $"W.{weakId:N}@G.EXAMPLE", FullName = "Weak",
                PasswordHash = "x", Status = "Active", AccessMode = AccessModes.FullPortal, IsActive = true, IsEmailConfirmed = true, IsGroupScope = true });
            db.UserRoles.Add(new UserRole { UserId = weakId, RoleId = role.Id });
            await db.SaveChangesAsync();
        }
        var plan = await DryRunAsync(w, NewBody("2026-03", "switching", "SAP"), Caller(w, weakId));
        plan.Ok.Should().BeFalse();
        plan.Problems.Should().Contain(p => p.Code == "migration_mode_needs_setup_permission" && p.MessageAr != null);

        (await DryRunAsync(w, NewBody("2026-03", "switching", "SAP"))).Problems.Should().NotContain(p => p.Code == "migration_mode_needs_setup_permission");

        // The controller itself is gated on the setup permission, reads and writes alike.
        typeof(Zayra.Api.Controllers.CompanySetupController).GetCustomAttributes(typeof(Zayra.Api.Infrastructure.Authorization.HasPermissionAttribute), true)
            .Cast<Zayra.Api.Infrastructure.Authorization.HasPermissionAttribute>()
            .Should().Contain(a => a.Permissions.Contains(Zayra.Api.Controllers.CompanySetupController.ApplyPermission));
    }

    [Fact]
    public async Task A_company_of_another_tenant_is_refused_even_with_switching_set()
    {
        var w = await SeedAsync();
        var other = await SeedAsync();
        var foreign = (await PrefillAsync(other)) with { ExistingCompanyId = other.CompanyId };
        // The other tenant's company id, planned by THIS tenant's admin on THIS tenant's database view.
        await using var db = Open(other.Options);
        var plan = await Service(db).DryRunAsync(Caller(w), foreign with { Company = foreign.Company with { MigrationMode = "switching", PreviousSystem = "Evil" } }, default);
        plan.Ok.Should().BeFalse();
        plan.Problems.Should().Contain(p => p.Code == "company_not_found");
        (await db.CompanyCutovers.IgnoreQueryFilters().SingleAsync(c => c.CompanyId == other.CompanyId)).SourceSystem.Should().Be("SAP");
    }

    [Theory]
    [InlineData("Planned", "", "new", null)]
    [InlineData("Planned", "SAP", "new", "SAP")]
    [InlineData("Active", "", "switching", null)]
    [InlineData("Closed", "Oracle", "switching", "Oracle")]
    public async Task Prefill_derives_the_mode_from_the_status_only_and_the_previous_system_from_the_source(string status, string source, string mode, string? previous)
    {
        var w = await SeedAsync(status, source);
        var body = await PrefillAsync(w);
        body.Company.MigrationMode.Should().Be(mode);
        body.Company.PreviousSystem.Should().Be(previous);
        body.Company.CutoverStatus.Should().Be(status);
    }

    [Fact]
    public async Task An_existing_company_with_a_cutover_and_locked_runs_plans_no_change_to_the_cutover()
    {
        var w = await SeedAsync(CutoverStatuses.Active, "SAP", (2026, 6, "Locked"), (2026, 7, "Locked"));
        var body = await PrefillAsync(w);
        var plan = await DryRunAsync(w, body);
        plan.Problems.Should().NotContain(p => p.Field.StartsWith("company."), JsonSerializer.Serialize(plan.Problems));
        plan.Items.Single(i => i.Area == V.Areas.Cutover).Action.Should().Be(V.ActionUnchanged);

        // An older API caller that knows nothing of the new fields neither clears nor rewrites them.
        var legacy = body with { Company = body.Company with { MigrationMode = null, PreviousSystem = null,
            Baseline = body.Company.Baseline!.Where(kv => kv.Key is not ("migrationMode" or "previousSystem")).ToDictionary(kv => kv.Key, kv => kv.Value) } };
        var legacyPlan = await DryRunAsync(w, legacy);
        legacyPlan.Problems.Should().BeEmpty(JsonSerializer.Serialize(legacyPlan.Problems));
        legacyPlan.Items.Single(i => i.Area == V.Areas.Cutover).Action.Should().Be(V.ActionUnchanged);
        legacyPlan.Problems.Should().NotContain(p => p.Field.StartsWith("company."));
    }

    private static async Task<CompanySetupDraftBody> MovedAsync(World w, string month)
    {
        var b = await PrefillAsync(w);
        return b with { Company = b.Company with { FirstPayrollMonth = month } };
    }

    [Theory]
    [InlineData("Locked", true)]
    [InlineData("Approved", true)]
    [InlineData("PendingFinanceReview", true)]
    [InlineData("Processed", true)]
    [InlineData("Draft", false)]
    [InlineData("Voided", false)]
    [InlineData("Cancelled", false)]
    public async Task A_governing_cutover_month_is_pinned_by_any_run_that_is_not_draft_voided_or_cancelled(string runStatus, bool pinned)
    {
        var w = await SeedAsync(CutoverStatuses.Active, "SAP", (2026, 7, runStatus));
        var plan = await DryRunAsync(w, await MovedAsync(w, "2026-07"));
        if (pinned) plan.Problems.Should().Contain(p => p.Code == "first_payroll_month_locked" && p.Field == "company.firstPayrollMonth");
        else plan.Problems.Should().NotContain(p => p.Code == "first_payroll_month_locked");
        (await PrefillAsync(w)).Company.CutoverPinned.Should().Be(pinned, "the read side tells the screen up front");
    }

    [Fact]
    public async Task The_go_live_month_is_pinned_only_by_a_run_in_or_after_the_earlier_month()
    {
        var free = await SeedAsync(CutoverStatuses.Active, "SAP");
        var plan = await DryRunAsync(free, await MovedAsync(free, "2026-07"));
        plan.Problems.Should().NotContain(p => p.Field == "company.firstPayrollMonth");
        plan.Items.Single(i => i.Area == V.Areas.Cutover).Action.Should().Be(V.ActionUpdate);

        var before = await SeedAsync(CutoverStatuses.Active, "SAP", (2026, 5, "Locked"));
        (await DryRunAsync(before, await MovedAsync(before, "2026-07"))).Problems.Should().NotContain(p => p.Code == "first_payroll_month_locked", "an earlier run is not affected");
        (await PrefillAsync(before)).Company.CutoverPinned.Should().BeFalse();

        var pinned = await SeedAsync(CutoverStatuses.Active, "SAP", (2026, 7, "Locked"));
        (await DryRunAsync(pinned, await MovedAsync(pinned, "2026-05"))).Problems
            .Should().Contain(p => p.Code == "first_payroll_month_locked", "moving earlier crosses the same month");
    }

    [Fact]
    public async Task A_planned_cutover_does_not_govern_so_its_month_moves_freely_even_with_runs()
    {
        var w = await SeedAsync(CutoverStatuses.Planned, "", (2026, 6, "Locked"));
        var plan = await DryRunAsync(w, await MovedAsync(w, "2026-07"));
        plan.Problems.Should().NotContain(p => p.Code == "first_payroll_month_locked");
        (await PrefillAsync(w)).Company.CutoverPinned.Should().BeFalse("a Planned cutover pins nothing");
    }

    [Fact]
    public async Task Choosing_switching_on_a_planned_cutover_promotes_it_only_when_no_run_exists_from_the_cutover_month()
    {
        var clean = await SeedAsync(CutoverStatuses.Planned, "", (2026, 5, "Locked"), (2026, 6, "Draft"), (2026, 6, "Voided"));
        var body = await PrefillAsync(clean);
        var plan = await DryRunAsync(clean, body with { Company = body.Company with { MigrationMode = "switching", PreviousSystem = "SAP" } });
        plan.Problems.Should().NotContain(p => p.Code == "switching_blocked_by_runs");
        var item = plan.Items.Single(i => i.Area == V.Areas.Cutover);
        item.Action.Should().Be(V.ActionUpdate);
        item.Reason.Should().Contain("status").And.Contain("Active");

        var blocked = await SeedAsync(CutoverStatuses.Planned, "", (2026, 6, "Processed"));
        var b2 = await PrefillAsync(blocked);
        var refused = await DryRunAsync(blocked, b2 with { Company = b2.Company with { MigrationMode = "switching", PreviousSystem = "SAP" } });
        refused.Ok.Should().BeFalse();
        refused.Problems.Should().Contain(p => p.Code == "switching_blocked_by_runs" && p.MessageAr != null && p.Message.Contains("Opening balances"));

        // Staying "new" on a Planned cutover changes nothing, with or without runs.
        (await DryRunAsync(blocked, b2)).Items.Single(i => i.Area == V.Areas.Cutover).Action.Should().Be(V.ActionUnchanged);
    }

    [Fact]
    public async Task Not_supplied_or_new_on_a_governing_cutover_never_demotes_or_clears_the_source_system()
    {
        var w = await SeedAsync(CutoverStatuses.Active, "SAP", (2026, 6, "Locked"));
        var body = await PrefillAsync(w);
        foreach (var edit in new Func<CompanySetupCompany, CompanySetupCompany>[]
        {
            c => c with { PreviousSystem = null },                       // cleared with the baseline intact: not supplied
            c => c with { PreviousSystem = "  " },
            c => c with { MigrationMode = null, PreviousSystem = null },
            c => c with { MigrationMode = "new" },                       // 'new' on a governing cutover is a no-op for the mode
            c => c with { MigrationMode = "new", PreviousSystem = null },
        })
        {
            var plan = await DryRunAsync(w, body with { Company = edit(body.Company) });
            plan.Problems.Should().BeEmpty(JsonSerializer.Serialize(plan.Problems));
            plan.Items.Single(i => i.Area == V.Areas.Cutover).Action.Should().Be(V.ActionUnchanged);
        }
        // Explicitly changing it while pinned is still refused, even with mode 'new'.
        var refused = await DryRunAsync(w, body with { Company = body.Company with { MigrationMode = "new", PreviousSystem = "Workday" } });
        refused.Problems.Should().Contain(p => p.Code == "previous_system_locked");
    }

    [Theory]
    [InlineData("\u200E")] [InlineData("\u200F")] [InlineData("\u202A")] [InlineData("\u202E")] [InlineData("\u2066")] [InlineData("\u2069")]
    [InlineData("\u200B")] [InlineData("\u200D")] [InlineData("\uFEFF")] [InlineData("\u2028")]
    public async Task The_previous_system_rejects_bidi_and_zero_width_format_characters(string bad)
    {
        var w = await SeedAsync();
        var plan = await DryRunAsync(w, NewBody("2026-03", "switching", "SAP" + bad + "x"));
        plan.Problems.Should().Contain(p => p.Code == "previous_system_invalid");
    }

    [Fact]
    public void The_go_live_window_is_computed_in_UTC_and_its_boundaries_are_exact()
    {
        // 23:59 UTC on the last day of March: still March, so the latest month is 2027-03 and 2027-04 is out.
        var lastMinute = new DateTime(2026, 3, 31, 23, 59, 0, DateTimeKind.Utc);
        CompanySetupPlanner.InGoLiveRange(new DateOnly(2027, 3, 1), lastMinute).Should().BeTrue();
        CompanySetupPlanner.InGoLiveRange(new DateOnly(2027, 4, 1), lastMinute).Should().BeFalse();
        // One minute later it is April in UTC and 2027-04 opens.
        var next = lastMinute.AddMinutes(1);
        CompanySetupPlanner.InGoLiveRange(new DateOnly(2027, 4, 1), next).Should().BeTrue();
        CompanySetupPlanner.InGoLiveRange(new DateOnly(2027, 5, 1), next).Should().BeFalse();
        // The floor.
        CompanySetupPlanner.InGoLiveRange(new DateOnly(2015, 1, 1), lastMinute).Should().BeTrue();
        CompanySetupPlanner.InGoLiveRange(new DateOnly(2014, 12, 1), lastMinute).Should().BeFalse();
    }

    [Fact]
    public async Task An_Active_wizard_cutover_for_a_company_with_no_runs_and_no_balances_leaves_the_ytd_basis_empty_and_Rule_14b_silent()
    {
        var w = await SeedAsync(CutoverStatuses.Active, "");
        await using var db = Open(w.Options);
        var run = new PayrollRun { TenantId = w.TenantId, CompanyId = w.CompanyId, Year = 2026, Month = 6, Status = "Draft" };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        var withCutover = await Zayra.Api.Infrastructure.Payroll.PayrollYtdBasis.LoadAsync(db, w.TenantId, w.CompanyId, run, [1, 2], default);
        withCutover.Cutover.Should().Be(new DateOnly(2026, 6, 1));
        withCutover.PriorSlips.Should().BeEmpty();
        withCutover.OpeningBalancesByEmployee.Should().BeEmpty();
        withCutover.UnresolvedOverlapEmployeeIds.Should().BeEmpty("Rule 14b raises its ERROR from this set");
        withCutover.PreCutoverSlipsExcludedEmployeeIds.Should().BeEmpty("Rule 14b raises its WARNING from this set");
        // Identical to the same company with no cutover at all.
        db.CompanyCutovers.RemoveRange(db.CompanyCutovers);
        await db.SaveChangesAsync();
        var without = await Zayra.Api.Infrastructure.Payroll.PayrollYtdBasis.LoadAsync(db, w.TenantId, w.CompanyId, run, [1, 2], default);
        without.PriorSlips.Should().BeEquivalentTo(withCutover.PriorSlips);
        without.OpeningBalancesByEmployee.Should().BeEquivalentTo(withCutover.OpeningBalancesByEmployee);
        without.UnresolvedOverlapEmployeeIds.Should().BeEquivalentTo(withCutover.UnresolvedOverlapEmployeeIds);
    }

    [Fact]
    public async Task The_commit_time_recheck_uses_the_same_decision_as_the_plan()
    {
        var w = await SeedAsync(CutoverStatuses.Active, "SAP");
        await using var db = Open(w.Options);
        var cutover = await db.CompanyCutovers.AsNoTracking().SingleAsync(c => c.Id == w.CutoverId);
        var month = new DateOnly(2026, 7, 1);
        (await CutoverLockRule.EvaluateAsync(db, w.TenantId, w.CompanyId, cutover, month, true, null, default)).RefusalCode.Should().BeNull();
        // A run is processed between the plan and the commit: the same call, run again, now refuses.
        db.PayrollRuns.Add(new PayrollRun { TenantId = w.TenantId, CompanyId = w.CompanyId, Year = 2026, Month = 7, Status = "Processed" });
        await db.SaveChangesAsync();
        (await CutoverLockRule.EvaluateAsync(db, w.TenantId, w.CompanyId, cutover, month, true, null, default)).RefusalCode.Should().Be("first_payroll_month_locked");
        (await CutoverLockRule.EvaluateAsync(db, w.TenantId, w.CompanyId, cutover, new DateOnly(2026, 6, 1), true, "Workday", default)).RefusalCode
            .Should().Be("previous_system_locked", "the date is unchanged, only the source moves, and a run pins the boundary");
    }

    [Fact]
    public async Task The_previous_system_changes_only_when_asked_and_only_while_no_locked_run_pins_the_boundary()
    {
        var free = await SeedAsync(CutoverStatuses.Active, "SAP");
        var body = await PrefillAsync(free);
        var plan = await DryRunAsync(free, body with { Company = body.Company with { PreviousSystem = "Oracle HCM" } });
        plan.Problems.Should().NotContain(p => p.Field == "company.previousSystem");
        plan.Items.Single(i => i.Area == V.Areas.Cutover).Action.Should().Be(V.ActionUpdate);
        plan.Items.Single(i => i.Area == V.Areas.Cutover).Reason.Should().Contain("previous system");

        var pinned = await SeedAsync(CutoverStatuses.Active, "SAP", (2026, 6, "Locked"));
        var again = await PrefillAsync(pinned);
        var refused = await DryRunAsync(pinned, again with { Company = again.Company with { PreviousSystem = "Workday" } });
        refused.Ok.Should().BeFalse();
        refused.Problems.Should().Contain(p => p.Code == "previous_system_locked" && p.Field == "company.previousSystem");
    }

    private sealed class FakeTokens : ITokenService
    {
        public string CreateAccessToken(User user, IReadOnlyCollection<string> roles, IReadOnlyCollection<string> permissions, Tenant tenant,
            IReadOnlyCollection<EntityAccessGrant> entityAccess, EntityScopeDescriptor entityScope, out DateTime expiresAtUtc)
        {
            expiresAtUtc = DateTime.UtcNow.AddHours(1);
            return "fake";
        }
        public string CreateSecureToken() => Guid.NewGuid().ToString("N");
        public string HashToken(string token) => token;
    }

    private sealed class NoEmail : Zayra.Api.Infrastructure.Email.IEmailService
    {
        public Task SendAsync(string toAddress, string toName, string subject, string htmlBody,
            IReadOnlyList<Zayra.Api.Infrastructure.Email.EmailAttachment>? attachments = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
