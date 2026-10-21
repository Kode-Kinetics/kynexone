using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CompanySetup;
using Zayra.Api.Application.Organization;
using Zayra.Api.Controllers;
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
/// Addendum A of docs/design/company-setup-wizard.md — the same wizard completing an EXISTING company — against real
/// Postgres (A8's list). The company under test is first created by the create mode, so "nothing changed" means a
/// fully set-up company, and each scenario seeds its own tenant on the shared container.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class CompanySetupExistingModePostgresTests(PostgresFixture fixture)
{
    private static readonly string[] AdminKeys =
        ["security.manage", "organization.setup.apply", "organization.write", "organization.read", "employees.read", "profile.read"];

    /// <param name="CompanyId">The company the create mode made (fully set up).</param>
    /// <param name="SisterId">A bare company (one branch, one cost centre) whose company-scoped Admin is <paramref name="CompanyAdminId"/>.</param>
    private sealed record World(Guid TenantId, Guid AdminId, Guid SisterId, Guid CompanyAdminId, Guid CompanyId, Guid HrManagerId, Guid HrManagerRoleId);

    // ── seed ────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<World> SeedAsync()
    {
        await using var db = fixture.CreateDb();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant
        {
            Id = tenantId, Name = "Existing Group", Slug = $"existing-{tenantId:N}",
            AccountType = TenantAccountTypes.Group, CompanyCreationMode = CompanyCreationModes.GroupSelfServiceWithinLimit,
        });
        db.TenantSubscriptions.Add(new TenantSubscription
        {
            TenantId = tenantId, Plan = "Group", Status = SubscriptionStatuses.Active, MaxCompanies = 10, MaxUsers = 0, MaxAdminUsers = 0,
        });
        var permissionIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var key in AdminKeys)
        {
            var existing = await db.Permissions.SingleOrDefaultAsync(x => x.Key == key);
            if (existing is null)
            {
                existing = new Permission { Key = key, Module = "Test", Description = key };
                db.Permissions.Add(existing);
                await db.SaveChangesAsync();
            }
            permissionIds[key] = existing.Id;
        }
        var adminRole = new Role
        {
            Id = Guid.NewGuid(), TenantId = tenantId, Name = "Admin", NormalizedName = "ADMIN", Description = "Admin",
            IsSystem = true, IsEditable = false, IsActive = true, AuthorityLevel = 1,
        };
        db.Roles.Add(adminRole);
        foreach (var key in AdminKeys) db.RolePermissions.Add(new RolePermission { RoleId = adminRole.Id, PermissionId = permissionIds[key] });
        // HR Manager holds organization.* (setup.apply included) — the role that must NOT reach existing mode company-scoped.
        var hrRole = new Role
        {
            Id = Guid.NewGuid(), TenantId = tenantId, Name = "HR Manager", NormalizedName = "HR MANAGER", Description = "HR Manager",
            IsSystem = true, IsEditable = true, IsActive = true, AuthorityLevel = 3,
        };
        db.Roles.Add(hrRole);
        foreach (var key in new[] { "organization.setup.apply", "organization.write", "organization.read" })
            db.RolePermissions.Add(new RolePermission { RoleId = hrRole.Id, PermissionId = permissionIds[key] });
        db.Roles.Add(new Role { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Employee", NormalizedName = "EMPLOYEE", Description = "Employee", IsActive = true });
        db.GCCComplianceSettings.Add(new GCCComplianceSetting
        {
            TenantId = tenantId, CountryCode = "SA", EosbEnabled = true, EosbYears1To5Rate = 15m, EosbYearsAbove5Rate = 30m,
            WeekendDays = "Fri,Sat", WorkWeek = "Sun-Thu", WpsAgentId = "",
        });

        var sister = new Company
        {
            TenantId = tenantId, LegalNameEn = "Sister Trading Co", LegalNameAr = "شركة الأخت", CountryCode = "SA", DefaultCurrency = "SAR",
            RegistrationNumber = "1010000001", IsActive = true, ApprovalStatus = CompanyApprovalStatuses.Active,
        };
        db.Companies.Add(sister);
        var sisterBranch = new Branch { TenantId = tenantId, CompanyId = sister.Id, Code = "HQ", NameEn = "Riyadh HQ", City = "Riyadh", CountryCode = "SA", TimeZoneId = "Asia/Riyadh", IsHeadOffice = true };
        db.Branches.Add(sisterBranch);
        var sisterCc = new CostCenter { TenantId = tenantId, CompanyId = sister.Id, Code = "CC_HR", Name = "HR cost centre" };
        db.CostCenters.Add(sisterCc);
        db.Departments.Add(new Department { TenantId = tenantId, Code = "HR", NameEn = "Human Resources", BranchId = sisterBranch.Id, CostCenterId = sisterCc.Id });
        db.Grades.Add(new Grade { TenantId = tenantId, Code = "G1", Name = "Grade 1", Level = 1, MinSalary = 3000, MidSalary = 4500, MaxSalary = 6000, Currency = "SAR" });
        await db.SaveChangesAsync();

        var adminId = await AddUserAsync(tenantId, "Group Admin", adminRole.Id, groupScope: true);
        var companyAdminId = await AddUserAsync(tenantId, "Sister Admin", adminRole.Id, groupScope: false, grantCompany: sister.Id);
        var hrManagerId = await AddUserAsync(tenantId, "Sister HR Manager", hrRole.Id, groupScope: false, grantCompany: sister.Id);

        // The company under test: created by the wizard itself, so every piece of its setup exists.
        var partial = new World(tenantId, adminId, sister.Id, companyAdminId, Guid.Empty, hrManagerId, hrRole.Id);
        await using var createDb = fixture.CreateDb();
        var service = Service(createDb);
        var draft = (await service.CreateDraftAsync(Caller(partial), NewCompanyBody(), default)).Value!;
        var created = await service.CommitAsync(Caller(partial), new CompanySetupCommitRequest(draft.Id, draft.Version), draft.Id, default);
        created.Status.Should().Be(201, JsonSerializer.Serialize(created.Error));
        return partial with { CompanyId = created.Value!.CompanyId };
    }

    private async Task<Guid> AddUserAsync(Guid tenantId, string name, Guid roleId, bool groupScope, Guid? grantCompany = null)
    {
        await using var db = fixture.CreateDb();
        var id = Guid.NewGuid();
        var email = $"{name.Replace(" ", ".").ToLowerInvariant()}.{id:N}@group.example";
        db.Users.Add(new User
        {
            Id = id, TenantId = tenantId, Email = email, NormalizedEmail = AuthService.Normalize(email), FullName = name,
            PasswordHash = "test-only-hash", Status = "Active", AccessMode = AccessModes.FullPortal, IsActive = true,
            IsEmailConfirmed = true, IsGroupScope = groupScope,
        });
        db.UserRoles.Add(new UserRole { UserId = id, RoleId = roleId });
        if (grantCompany is Guid company)
            db.UserEntityAccesses.Add(new UserEntityAccess
            {
                TenantId = tenantId, UserId = id, CompanyId = company, GrantMode = EntityGrantModes.SelectedCompanies,
                Role = "Company Administrator", IsActive = true,
            });
        await db.SaveChangesAsync();
        return id;
    }

    private static CompanySetupDraftBody NewCompanyBody() =>
        new(V.StartFromDefaults, null,
            new CompanySetupCompany("Existing Gulf Co", "شركة الخليج القائمة", "Gulf", "SA", "SAR", "1010000077", "7-1234567", "G-55", "7001234567", null, null,
                null, null, null, "2026-11"),
            [new CompanySetupBranch("JED", "Jeddah HQ", "جدة", "Jeddah", "Asia/Riyadh", true,
                [new CompanySetupLocation("JED-L1", "Jeddah site", null, 21.5m, 39.2m, 150m)]),
             new CompanySetupBranch("DMM", "Dammam", null, "Dammam", "Asia/Riyadh", false, [])],
            [new CompanySetupCostCentre("CC_OPS_EX", "Operations cost centre", null)],
            [new CompanySetupDepartment("OPS_EX", "Operations", null, null, "JED", "CC_OPS_EX")],
            [new CompanySetupDesignation("OPS_OFF_EX", "Operations Officer", null, "OPS_EX", "G1")],
            [],
            new CompanySetupRules(new CompanySetupWorkWeek("Fri,Sat", false), new CompanySetupPackChoice(true), new CompanySetupPackChoice(true),
                new CompanySetupEmployeeNumbering("EGC", true, false, true, true, 4, 1)),
            new CompanySetupCompanyAdmin(V.AdminGroupOnly, null, null, null, null));

    private static CompanySetupService Service(ZayraDbContext db)
    {
        var audit = new AuditService(db);
        return new CompanySetupService(db, new OrganizationSetupService(db, audit),
            new AccessManagementService(db, new Pbkdf2PasswordHasher(), audit, new FakeTokenService()), new WorkWeekService(db), new NoEmail());
    }

    /// <summary>A group admin (<paramref name="userId"/> null), or a company-scoped caller limited to <paramref name="companies"/>.</summary>
    private static CompanySetupCaller Caller(World w, Guid? userId = null, params Guid[] companies)
    {
        var uid = userId ?? w.AdminId;
        var group = userId is null;
        return new CompanySetupCaller(w.TenantId, uid, group, false, new RequestContext("127.0.0.1", "tests", uid, w.TenantId),
            group ? null : companies);
    }

    private async Task<CompanySetupDraftBody> PrefillAsync(World w, Guid companyId, CompanySetupCaller? caller = null)
    {
        await using var db = fixture.CreateDb();
        var prefill = await Service(db).GetExistingDefaultsAsync(caller ?? Caller(w), companyId, default);
        prefill.Status.Should().Be(200, JsonSerializer.Serialize(prefill.Error));
        return prefill.Value!;
    }

    private async Task<CompanySetupPlan> DryRunAsync(World w, CompanySetupDraftBody body, CompanySetupCaller? caller = null)
    {
        await using var db = fixture.CreateDb();
        return await Service(db).DryRunAsync(caller ?? Caller(w), body, default);
    }

    private async Task<(CompanySetupDraft Draft, CompanySetupOutcome<CompanySetupResult> Result)> DraftAndCommitAsync(World w, CompanySetupDraftBody body,
        CompanySetupCaller? caller = null)
    {
        await using var db = fixture.CreateDb();
        var service = Service(db);
        var created = await service.CreateDraftAsync(caller ?? Caller(w), body, default);
        created.Status.Should().Be(201, JsonSerializer.Serialize(created.Error));
        var draft = created.Value!;
        await using var commitDb = fixture.CreateDb();
        var result = await Service(commitDb).CommitAsync(caller ?? Caller(w), new CompanySetupCommitRequest(draft.Id, draft.Version), draft.Id, default);
        return (draft, result);
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static JsonElement ErrorBody(object? error) => JsonDocument.Parse(JsonSerializer.Serialize(error, Web)).RootElement;
    private static string ErrorCode(object? error) => ErrorBody(error).GetProperty("code").GetString()!;

    /// <summary>Every business row the wizard can write, counted for the tenant, plus each row's last-change stamp.</summary>
    private async Task<string> FingerprintAsync(Guid tenantId)
    {
        await using var db = fixture.CreateDb();
        var parts = new List<object>
        {
            await db.Companies.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).OrderBy(x => x.Id).Select(x => new { x.Id, x.UpdatedAtUtc, x.LegalNameEn, x.LegalNameAr, x.WpsEmployerId }).ToListAsync(),
            await db.Branches.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).OrderBy(x => x.Id).Select(x => new { x.Id, x.UpdatedAtUtc, x.NameEn, x.IsHeadOffice }).ToListAsync(),
            await db.Locations.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).OrderBy(x => x.Id).Select(x => new { x.Id, x.UpdatedAtUtc, x.NameEn }).ToListAsync(),
            await db.CostCenters.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).OrderBy(x => x.Id).Select(x => new { x.Id, x.UpdatedAtUtc, x.Name }).ToListAsync(),
            await db.Departments.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).OrderBy(x => x.Id).Select(x => new { x.Id, x.UpdatedAtUtc, x.BranchId, x.CostCenterId }).ToListAsync(),
            await db.Designations.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).OrderBy(x => x.Id).Select(x => new { x.Id, x.UpdatedAtUtc }).ToListAsync(),
            await db.Grades.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).OrderBy(x => x.Id).Select(x => new { x.Id, x.UpdatedAtUtc }).ToListAsync(),
            await db.EmployeeIdRules.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).OrderBy(x => x.Id).Select(x => new { x.Id, x.UpdatedAtUtc, x.NextSequence }).ToListAsync(),
            await db.CompanyCutovers.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).OrderBy(x => x.Id).Select(x => new { x.Id, x.UpdatedAtUtc, x.CutoverDate }).ToListAsync(),
            await db.GCCComplianceSettings.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).OrderBy(x => x.Id).Select(x => new { x.Id, x.UpdatedAtUtc, x.WeekendDays }).ToListAsync(),
            await db.PublicHolidayCalendars.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).CountAsync(),
            await db.PublicHolidays.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).CountAsync(),
            await db.LeavePolicies.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).CountAsync(),
            await db.Users.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).CountAsync(),
            await db.UserEntityAccesses.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).CountAsync(),
        };
        return JsonSerializer.Serialize(parts);
    }

    private async Task<List<string>> AuditActionsSinceAsync(Guid tenantId, DateTime since)
    {
        await using var db = fixture.CreateDb();
        return await db.AuditLogs.IgnoreQueryFilters().Where(a => a.TenantId == tenantId && a.CreatedAtUtc >= since)
            .OrderBy(a => a.CreatedAtUtc).Select(a => a.Action).ToListAsync();
    }

    // ── A2/A3: prefill and the planner's update / unchanged ─────────────────────────────────────────────

    [Fact]
    public async Task A_prefill_round_trip_with_no_changes_plans_only_unchanged_or_reuse_and_the_commit_writes_only_the_audit_row()
    {
        var w = await SeedAsync();
        var body = await PrefillAsync(w, w.CompanyId);
        body.ExistingCompanyId.Should().Be(w.CompanyId);
        body.Company.CountryCode.Should().Be("SA");
        body.Company.FirstPayrollMonth.Should().Be("2026-11", "the company's own planned cutover month");
        body.Branches!.Should().HaveCount(2).And.OnlyContain(b => b.ExistingId != null);
        body.Branches!.SelectMany(b => b.Locations!).Should().ContainSingle().Which.ExistingId.Should().NotBeNull();
        body.CostCentres!.Should().ContainSingle().Which.ExistingId.Should().NotBeNull();
        body.Departments!.Should().Contain(d => d.Code == "OPS_EX" && d.ExistingId != null && d.BranchCode == "JED" && d.CostCentreCode == "CC_OPS_EX");
        body.Rules.EmployeeNumbering.Prefix.Should().Be("EGC");

        var plan = await DryRunAsync(w, body);
        plan.Ok.Should().BeTrue(JsonSerializer.Serialize(plan.Problems));
        plan.Items.Should().OnlyContain(i => i.Action == V.ActionUnchanged || i.Action == V.ActionReuse,
            JsonSerializer.Serialize(plan.Items.Where(i => i.Action != V.ActionUnchanged && i.Action != V.ActionReuse)));
        plan.Warnings.Should().NotContain(x => x.Code == "existing_department_not_relinked", "the prefill carries the department's own links");

        var before = await FingerprintAsync(w.TenantId);
        var since = DateTime.UtcNow;
        var (draft, result) = await DraftAndCommitAsync(w, body);
        result.Status.Should().Be(201, JsonSerializer.Serialize(result.Error));
        result.Value!.CompanyId.Should().Be(w.CompanyId, "the company is completed, never recreated");
        result.Value.Created.Should().BeEmpty();
        result.Value.Updated.Should().BeEmpty();
        (await FingerprintAsync(w.TenantId)).Should().Be(before, "nothing changed, so no business row is written");
        (await AuditActionsSinceAsync(w.TenantId, since)).Should().BeEquivalentTo(
            ["company.setup_draft_created", CompanySetupService.SetupCompletedAction]);

        await using var db = fixture.CreateDb();
        var marker = await db.AuditLogs.IgnoreQueryFilters().SingleAsync(a => a.TenantId == w.TenantId && a.Action == CompanySetupService.SetupCompletedAction);
        marker.EntityId.Should().Be(draft.Id.ToString());
        marker.CompanyId.Should().Be(w.CompanyId);
        using var meta = JsonDocument.Parse(marker.Metadata!);
        meta.RootElement.GetProperty("unchanged").GetProperty("branch").GetInt32().Should().Be(2, "the marker counts by action");
    }

    [Fact]
    public async Task Editing_a_branch_name_is_an_update_of_that_row_and_creates_no_duplicate()
    {
        var w = await SeedAsync();
        var body = await PrefillAsync(w, w.CompanyId);
        var dammam = body.Branches!.Single(b => b.Code == "DMM");
        body = body with { Branches = body.Branches!.Select(b => b.Code == "DMM" ? b with { NameEn = "Dammam Branch", NameAr = "فرع الدمام" } : b).ToList() };

        var plan = await DryRunAsync(w, body);
        plan.Ok.Should().BeTrue(JsonSerializer.Serialize(plan.Problems));
        var item = plan.Items.Single(i => i.Area == V.Areas.Branch && i.Code == "DMM");
        item.Action.Should().Be(V.ActionUpdate);
        item.Reason.Should().Contain("name (English)").And.Contain("name (Arabic)");
        plan.Items.Where(i => i.Action == V.ActionUpdate).Should().ContainSingle("only that one row changes");

        var (_, result) = await DraftAndCommitAsync(w, body);
        result.Status.Should().Be(201, JsonSerializer.Serialize(result.Error));
        result.Value!.Updated.Should().ContainKey(V.Areas.Branch).WhoseValue.Should().Be(1);
        await using var db = fixture.CreateDb();
        var rows = await db.Branches.IgnoreQueryFilters().Where(b => b.TenantId == w.TenantId && b.CompanyId == w.CompanyId).ToListAsync();
        rows.Should().HaveCount(2, "no duplicate branch");
        var row = rows.Single(b => b.Id == dammam.ExistingId);
        row.NameEn.Should().Be("Dammam Branch");
        row.NameAr.Should().Be("فرع الدمام");
        row.City.Should().Be("Dammam");
        (await db.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.TenantId == w.TenantId && a.Action == "organization.branch_updated" && a.EntityId == row.Id.ToString()))
            .Should().Be(1);
    }

    [Fact]
    public async Task Adding_a_branch_is_a_create_and_existing_rows_left_out_of_the_body_are_kept()
    {
        var w = await SeedAsync();
        var body = await PrefillAsync(w, w.CompanyId);
        // Leave DMM out entirely (the wizard never deletes) and add a new branch.
        body = body with
        {
            Branches = body.Branches!.Where(b => b.Code != "DMM")
                .Append(new CompanySetupBranch("RUH", "Riyadh", null, "Riyadh", "Asia/Riyadh", false, [new CompanySetupLocation("RUH-EX1", "Riyadh site", null, 24.7m, 46.7m, 200m)]))
                .ToList(),
        };
        var plan = await DryRunAsync(w, body);
        plan.Ok.Should().BeTrue(JsonSerializer.Serialize(plan.Problems));
        plan.Items.Single(i => i.Area == V.Areas.Branch && i.Code == "RUH").Action.Should().Be(V.ActionCreate);
        plan.Items.Single(i => i.Area == V.Areas.Location && i.Code == "RUH-EX1").Action.Should().Be(V.ActionCreate);
        plan.Items.Single(i => i.Area == V.Areas.Branch && i.Code == "DMM").Action.Should().Be(V.ActionUnchanged, "omitted is kept, not deleted");

        var (_, result) = await DraftAndCommitAsync(w, body);
        result.Status.Should().Be(201, JsonSerializer.Serialize(result.Error));
        result.Value!.Created.Should().ContainKey(V.Areas.Branch).WhoseValue.Should().Be(1);
        await using var db = fixture.CreateDb();
        var codes = await db.Branches.IgnoreQueryFilters().Where(b => b.TenantId == w.TenantId && b.CompanyId == w.CompanyId && !b.IsDeleted)
            .Select(b => b.Code).ToListAsync();
        codes.Should().BeEquivalentTo(["JED", "DMM", "RUH"]);
    }

    [Fact]
    public async Task Locations_cost_centres_and_the_company_weekend_are_edited_in_place()
    {
        var w = await SeedAsync();
        var body = await PrefillAsync(w, w.CompanyId);
        body = body with
        {
            Branches = body.Branches!.Select(b => b with { Locations = b.Locations!.Select(l => l with { RadiusMeters = 300m }).ToList() }).ToList(),
            CostCentres = body.CostCentres!.Select(c => c with { NameEn = "Operations (Jeddah)" }).ToList(),
            Rules = body.Rules with { WorkWeek = new CompanySetupWorkWeek("Sat", true) },
        };
        var plan = await DryRunAsync(w, body);
        plan.Ok.Should().BeTrue(JsonSerializer.Serialize(plan.Problems));
        plan.Items.Single(i => i.Area == V.Areas.Location).Action.Should().Be(V.ActionUpdate);
        plan.Items.Single(i => i.Area == V.Areas.CostCentre).Action.Should().Be(V.ActionUpdate);
        plan.Items.Single(i => i.Area == V.Areas.WorkWeek).Action.Should().Be(V.ActionCreate, "the company had no weekend of its own yet");
        var (_, first) = await DraftAndCommitAsync(w, body);
        first.Status.Should().Be(201, JsonSerializer.Serialize(first.Error));

        await using (var db = fixture.CreateDb())
        {
            (await db.Locations.IgnoreQueryFilters().SingleAsync(l => l.TenantId == w.TenantId && l.Code == "JED-L1")).GeofenceRadiusMeters.Should().Be(300m);
            (await db.CostCenters.IgnoreQueryFilters().SingleAsync(c => c.TenantId == w.TenantId && c.Code == "CC_OPS_EX")).Name.Should().Be("Operations (Jeddah)");
            (await db.GCCComplianceSettings.IgnoreQueryFilters().SingleAsync(g => g.TenantId == w.TenantId && g.CompanyId == w.CompanyId)).WeekendDays.Should().Be("Sat");
        }

        // The company now has its own weekend: the prefill says so, and changing it edits that row (no second row).
        var again = await PrefillAsync(w, w.CompanyId);
        again.Rules.WorkWeek.Should().Be(new CompanySetupWorkWeek("Sat", true));
        (await DryRunAsync(w, again)).Items.Should().OnlyContain(i => i.Action == V.ActionUnchanged || i.Action == V.ActionReuse);
        var changed = again with { Rules = again.Rules with { WorkWeek = new CompanySetupWorkWeek("Fri", true) } };
        var (_, second) = await DraftAndCommitAsync(w, changed);
        second.Status.Should().Be(201, JsonSerializer.Serialize(second.Error));
        second.Value!.Updated.Should().ContainKey(V.Areas.WorkWeek);
        await using var verify = fixture.CreateDb();
        var rows = await verify.GCCComplianceSettings.IgnoreQueryFilters().Where(g => g.TenantId == w.TenantId && g.CompanyId == w.CompanyId).ToListAsync();
        rows.Should().ContainSingle().Which.WeekendDays.Should().Be("Fri");
        // Unticking the override never deletes the company's row: it is kept, with a warning.
        var untick = await DryRunAsync(w, again with { Rules = again.Rules with { WorkWeek = new CompanySetupWorkWeek("Fri,Sat", false) } });
        untick.Warnings.Should().Contain(x => x.Code == "work_week_override_kept");
    }

    [Fact]
    public async Task A_new_branch_reusing_one_of_the_company_s_codes_is_a_conflict_and_an_existing_code_cannot_be_renamed()
    {
        var w = await SeedAsync();
        var body = await PrefillAsync(w, w.CompanyId);
        var clash = body with { Branches = [.. body.Branches!, new CompanySetupBranch("DMM", "Second Dammam", null, "Dammam", "Asia/Riyadh", false, [])] };
        (await DryRunAsync(w, clash)).Problems.Should().Contain(p => p.Code == "duplicate_code");
        var renamed = body with { Branches = body.Branches!.Select(b => b.Code == "DMM" ? b with { Code = "DMM2" } : b).ToList() };
        var plan = await DryRunAsync(w, renamed);
        plan.Ok.Should().BeFalse();
        plan.Problems.Should().Contain(p => p.Code == "existing_code_locked" && p.Field == "branches.1.code");
    }

    [Fact]
    public async Task Country_and_currency_changes_are_refused_and_nothing_is_written()
    {
        var w = await SeedAsync();
        var body = await PrefillAsync(w, w.CompanyId);
        var plan = await DryRunAsync(w, body with { Company = body.Company with { CountryCode = "AE", Currency = "AED" } });
        plan.Ok.Should().BeFalse();
        plan.Problems.Should().Contain(p => p.Code == "country_locked" && p.Field == "company.countryCode" && p.Step == 2);
        plan.Problems.Should().Contain(p => p.Code == "currency_locked" && p.Field == "company.currency");

        var before = await FingerprintAsync(w.TenantId);
        var (_, result) = await DraftAndCommitAsync(w, body with { Company = body.Company with { Currency = "USD" } });
        result.Status.Should().Be(422);
        ErrorCode(result.Error).Should().Be("currency_locked");
        (await FingerprintAsync(w.TenantId)).Should().Be(before);
    }

    [Fact]
    public async Task Company_fields_are_updated_through_the_company_form_path_with_its_validations()
    {
        var w = await SeedAsync();
        var body = await PrefillAsync(w, w.CompanyId);
        var edited = body with { Company = body.Company with { LegalNameAr = "شركة الخليج القائمة المحدودة", MolEstablishmentNumber = "7-7654321", TaxNumber = "300000000000003" } };
        var plan = await DryRunAsync(w, edited);
        plan.Ok.Should().BeTrue(JsonSerializer.Serialize(plan.Problems));
        var item = plan.Items.Single(i => i.Area == V.Areas.Company);
        item.Action.Should().Be(V.ActionUpdate);
        item.Reason.Should().Contain("legal name (Arabic)").And.Contain("MOL establishment number").And.Contain("VAT number");

        var (_, result) = await DraftAndCommitAsync(w, edited);
        result.Status.Should().Be(201, JsonSerializer.Serialize(result.Error));
        await using var db = fixture.CreateDb();
        var company = await db.Companies.IgnoreQueryFilters().SingleAsync(c => c.Id == w.CompanyId);
        company.LegalNameAr.Should().Be("شركة الخليج القائمة المحدودة");
        company.WpsEmployerId.Should().Be("7-7654321");
        company.CountryCode.Should().Be("SA");
        company.DefaultCurrency.Should().Be("SAR");
        company.IsActive.Should().BeTrue();
        (await db.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.TenantId == w.TenantId && a.Action == "organization.company_updated" && a.EntityId == w.CompanyId.ToString()))
            .Should().Be(1, "UpdateCompanyAsync's own audit, inside the wizard's transaction");

        // A changed MOL number is validated as on create; a name another company holds is a 409 conflict.
        var badMol = await DryRunAsync(w, body with { Company = body.Company with { MolEstablishmentNumber = "ABC" } });
        badMol.Problems.Should().Contain(p => p.Field == "company.molEstablishmentNumber");
        var clash = await DryRunAsync(w, body with { Company = body.Company with { LegalNameEn = "Sister Trading Co" } });
        clash.Problems.Should().Contain(p => p.Code == CompanySetupGates.NameTakenCode);
        // The company's own CR number and name are not a clash with itself.
        (await DryRunAsync(w, body)).Problems.Should().NotContain(p => p.Code == CompanySetupGates.CrTakenCode || p.Code == CompanySetupGates.NameTakenCode);
    }

    [Fact]
    public async Task The_company_form_validates_a_changed_mol_number_on_update_and_leaves_an_unchanged_legacy_one_alone()
    {
        var w = await SeedAsync();
        await using (var seed = fixture.CreateDb())
        {
            var sister = await seed.Companies.IgnoreQueryFilters().SingleAsync(c => c.Id == w.SisterId);
            sister.WpsEmployerId = "LEGACY";
            await seed.SaveChangesAsync();
        }
        CompanyRequest Request(string mol, string name = "Sister Trading Co") =>
            new(name, "شركة الأخت", null, "SA", null, "1010000001", null, mol, null, null, "SAR");
        var ctx = new RequestContext("127.0.0.1", "tests", w.AdminId, w.TenantId);

        await using (var db = fixture.CreateDb())
        {
            var updated = await new OrganizationSetupService(db, new AuditService(db)).UpdateCompanyAsync(w.TenantId, w.SisterId, Request("LEGACY", "Sister Trading Company"), ctx, default);
            updated!.LegalNameEn.Should().Be("Sister Trading Company", "an unchanged legacy MOL value does not block editing the rest");
        }
        await using (var db = fixture.CreateDb())
        {
            var act = () => new OrganizationSetupService(db, new AuditService(db)).UpdateCompanyAsync(w.TenantId, w.SisterId, Request("ABC"), ctx, default);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        await using (var db = fixture.CreateDb())
        {
            var updated = await new OrganizationSetupService(db, new AuditService(db)).UpdateCompanyAsync(w.TenantId, w.SisterId, Request("7-1112223"), ctx, default);
            updated!.WpsEmployerId.Should().Be("7-1112223");
        }

        // Through the wizard: the unchanged legacy value is a warning, not a refusal.
        await using (var seed = fixture.CreateDb())
        {
            var sister = await seed.Companies.IgnoreQueryFilters().SingleAsync(c => c.Id == w.SisterId);
            sister.WpsEmployerId = "LEGACY";
            await seed.SaveChangesAsync();
        }
        var body = await PrefillAsync(w, w.SisterId);
        var plan = await DryRunAsync(w, body);
        plan.Problems.Should().NotContain(p => p.Field == "company.molEstablishmentNumber");
        plan.Warnings.Should().Contain(p => p.Field == "company.molEstablishmentNumber");
    }

    [Fact]
    public async Task The_first_payroll_month_moves_only_while_the_cutover_is_planned()
    {
        var w = await SeedAsync();
        var body = await PrefillAsync(w, w.CompanyId);
        var moved = body with { Company = body.Company with { FirstPayrollMonth = "2026-12" } };
        var plan = await DryRunAsync(w, moved);
        plan.Ok.Should().BeTrue(JsonSerializer.Serialize(plan.Problems));
        plan.Items.Single(i => i.Area == V.Areas.Cutover).Action.Should().Be(V.ActionUpdate);
        var (_, result) = await DraftAndCommitAsync(w, moved);
        result.Status.Should().Be(201, JsonSerializer.Serialize(result.Error));
        await using (var db = fixture.CreateDb())
        {
            var cutover = await db.CompanyCutovers.IgnoreQueryFilters().SingleAsync(c => c.TenantId == w.TenantId && c.CompanyId == w.CompanyId);
            cutover.CutoverDate.Should().Be(new DateOnly(2026, 12, 1));
            cutover.Status = CutoverStatuses.Active;
            await db.SaveChangesAsync();
        }
        var again = await PrefillAsync(w, w.CompanyId);
        again.Company.FirstPayrollMonth.Should().Be("2026-12");
        (await DryRunAsync(w, again)).Ok.Should().BeTrue("an unchanged month is fine once payroll started");
        var locked = await DryRunAsync(w, again with { Company = again.Company with { FirstPayrollMonth = "2027-01" } });
        locked.Ok.Should().BeFalse();
        locked.Problems.Should().Contain(p => p.Code == "first_payroll_month_locked" && p.Field == "company.firstPayrollMonth");
    }

    [Fact]
    public async Task The_employee_number_counter_cannot_go_back()
    {
        var w = await SeedAsync();
        await using (var db = fixture.CreateDb())
        {
            var rule = await db.EmployeeIdRules.IgnoreQueryFilters().SingleAsync(r => r.TenantId == w.TenantId && r.CompanyId == w.CompanyId);
            rule.NextSequence = 42;
            await db.SaveChangesAsync();
        }
        var body = await PrefillAsync(w, w.CompanyId);
        body.Rules.EmployeeNumbering.NextSequence.Should().Be(42);
        var plan = await DryRunAsync(w, body with { Rules = body.Rules with { EmployeeNumbering = body.Rules.EmployeeNumbering with { NextSequence = 5 } } });
        plan.Problems.Should().Contain(p => p.Code == "sequence_cannot_go_back");
        var forward = await DryRunAsync(w, body with { Rules = body.Rules with { EmployeeNumbering = body.Rules.EmployeeNumbering with { NextSequence = 50 } } });
        forward.Items.Single(i => i.Area == V.Areas.EmployeeIdRule).Action.Should().Be(V.ActionUpdate);
    }

    [Fact]
    public async Task A_bare_company_is_prefilled_from_what_it_has_and_its_gaps_are_new_rows()
    {
        var w = await SeedAsync();
        var body = await PrefillAsync(w, w.SisterId);
        body.Branches!.Should().ContainSingle().Which.ExistingId.Should().NotBeNull();
        body.CostCentres!.Should().ContainSingle(c => c.Code == "CC_HR").Which.ExistingId.Should().NotBeNull();
        body.Departments!.Should().ContainSingle(d => d.Code == "HR").Which.ExistingId.Should().NotBeNull("referenced by the company's branch");
        var plan = await DryRunAsync(w, body);
        plan.Ok.Should().BeTrue(JsonSerializer.Serialize(plan.Problems));
        plan.Items.Single(i => i.Area == V.Areas.EmployeeIdRule).Action.Should().Be(V.ActionCreate);
        plan.Items.Single(i => i.Area == V.Areas.Cutover).Action.Should().Be(V.ActionCreate);
        plan.Items.Single(i => i.Area == V.Areas.Branch).Action.Should().Be(V.ActionUnchanged);
        var (_, result) = await DraftAndCommitAsync(w, body);
        result.Status.Should().Be(201, JsonSerializer.Serialize(result.Error));
        await using var db = fixture.CreateDb();
        (await db.CompanyCutovers.IgnoreQueryFilters().CountAsync(c => c.CompanyId == w.SisterId)).Should().Be(1);
        (await db.EmployeeIdRules.IgnoreQueryFilters().CountAsync(c => c.CompanyId == w.SisterId)).Should().Be(1);
        (await db.Branches.IgnoreQueryFilters().CountAsync(c => c.CompanyId == w.SisterId)).Should().Be(1);
    }

    // ── A5: who may run it ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_company_super_admin_completes_their_own_company_and_is_403_for_a_sister()
    {
        var w = await SeedAsync();
        await using var db = fixture.CreateDb();
        var controller = Controller(db, w.TenantId, w.CompanyAdminId, scopedCompany: w.SisterId);

        var context = (await controller.Context(default)).Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<CompanySetupContext>().Subject;
        context.CanCreate.Should().BeFalse("creating a company stays a group action");
        context.ManageableCompanies.Should().ContainSingle().Which.Id.Should().Be(w.SisterId);

        var prefill = (await controller.Defaults(null, null, w.SisterId, default)).Result.Should().BeOfType<ObjectResult>().Subject;
        prefill.StatusCode.Should().Be(200);
        var body = (CompanySetupDraftBody)prefill.Value!;
        var sisterBody = body;

        ((ObjectResult)(await controller.Defaults(null, null, w.CompanyId, default)).Result!).StatusCode.Should().Be(403);
        ((ObjectResult)(await controller.Readiness(w.CompanyId, default)).Result!).StatusCode.Should().Be(403);
        ((ObjectResult)(await controller.Readiness(w.SisterId, default)).Result!).StatusCode.Should().Be(200);

        var otherBody = await PrefillAsync(w, w.CompanyId);
        var otherDryRun = (ObjectResult)(await controller.DryRun(otherBody, default)).Result!;
        otherDryRun.StatusCode.Should().Be(403);
        ErrorCode(otherDryRun.Value).Should().Be(CompanySetupGates.CompanyNotManageableCode);
        ((ObjectResult)(await controller.CreateDraft(otherBody, default)).Result!).StatusCode.Should().Be(403);

        // A group admin's draft for the other company is invisible to them, and its commit is 403.
        await using var groupDb = fixture.CreateDb();
        var groupDraft = (await Service(groupDb).CreateDraftAsync(Caller(w), otherBody, default)).Value!;
        await using var db2 = fixture.CreateDb();
        var controller2 = Controller(db2, w.TenantId, w.CompanyAdminId, scopedCompany: w.SisterId);
        ((ObjectResult)(await controller2.Draft(groupDraft.Id, default)).Result!).StatusCode.Should().Be(404);
        (((await controller2.Drafts(default)).Result as OkObjectResult)!.Value as IReadOnlyList<CompanySetupDraftSummary>)!
            .Should().NotContain(d => d.Id == groupDraft.Id);
        ((ObjectResult)(await controller2.Commit(new CompanySetupCommitRequest(groupDraft.Id, groupDraft.Version), groupDraft.Id.ToString(), default)).Result!)
            .StatusCode.Should().Be(403);

        // Their own company: draft, dry run, commit.
        var edited = sisterBody with { Branches = sisterBody.Branches!.Select(b => b with { NameEn = "Riyadh Head Office" }).ToList() };
        var draftResult = (ObjectResult)(await controller2.CreateDraft(edited, default)).Result!;
        draftResult.StatusCode.Should().Be(201, JsonSerializer.Serialize(draftResult.Value, Web));
        var draft = (CompanySetupDraft)draftResult.Value!;
        var plan = (CompanySetupPlan)((OkObjectResult)(await controller2.DryRun(edited, default)).Result!).Value!;
        plan.Ok.Should().BeTrue(JsonSerializer.Serialize(plan.Problems));
        await using var db3 = fixture.CreateDb();
        var controller3 = Controller(db3, w.TenantId, w.CompanyAdminId, scopedCompany: w.SisterId);
        var commit = (ObjectResult)(await controller3.Commit(new CompanySetupCommitRequest(draft.Id, draft.Version), draft.Id.ToString(), default)).Result!;
        commit.StatusCode.Should().Be(201, JsonSerializer.Serialize(commit.Value, Web));
        ((CompanySetupResult)commit.Value!).CompanyId.Should().Be(w.SisterId);
        await using var verify = fixture.CreateDb();
        (await verify.Branches.IgnoreQueryFilters().SingleAsync(b => b.CompanyId == w.SisterId)).NameEn.Should().Be("Riyadh Head Office");
    }

    [Fact]
    public async Task A_company_scoped_hr_manager_cannot_run_existing_mode_even_holding_the_setup_permission()
    {
        var w = await SeedAsync();
        await using var db = fixture.CreateDb();
        var controller = Controller(db, w.TenantId, w.HrManagerId, scopedCompany: w.SisterId, role: "HR Manager");
        var context = (CompanySetupContext)((OkObjectResult)(await controller.Context(default)).Result!).Value!;
        context.ManageableCompanies.Should().BeEmpty();
        context.CanGrantCompanyAdmin.Should().BeFalse();
        var defaults = (ObjectResult)(await controller.Defaults(null, null, w.SisterId, default)).Result!;
        defaults.StatusCode.Should().Be(403);
        ErrorCode(defaults.Value).Should().Be(CompanySetupGates.CompanyAdminRequiredCode);
        var body = await PrefillAsync(w, w.SisterId);
        ((ObjectResult)(await controller.CreateDraft(body, default)).Result!).StatusCode.Should().Be(403);
        ((ObjectResult)(await controller.DryRun(body, default)).Result!).StatusCode.Should().Be(403);
        // And the planner refuses it by itself (the commit path), whatever the door.
        var plan = await DryRunAsync(w, body, Caller(w, w.HrManagerId, w.SisterId));
        plan.Ok.Should().BeFalse();
        plan.Problems.Should().ContainSingle().Which.Code.Should().Be(CompanySetupGates.CompanyAdminRequiredCode);
    }

    [Fact]
    public async Task A_company_super_admin_cannot_add_shared_catalog_codes_but_may_add_a_company_admin_for_their_own_company()
    {
        var w = await SeedAsync();
        var caller = Caller(w, w.CompanyAdminId, w.SisterId);
        var body = await PrefillAsync(w, w.SisterId, caller);
        body.Departments!.Should().NotContain(d => d.ExistingId == null, "the prefill offers a company admin no new shared codes");
        body.Grades!.Should().NotContain(g => g.ExistingId == null);
        body.Designations!.Should().NotContain(d => d.ExistingId == null);

        var added = body with { Departments = [.. body.Departments!, new CompanySetupDepartment("FIN_NEW", "Finance", null, null, "HQ", null)] };
        var plan = await DryRunAsync(w, added, caller);
        plan.Ok.Should().BeFalse();
        plan.Items.Should().Contain(i => i.Code == "FIN_NEW" && i.Action == V.ActionConflict);
        plan.Problems.Should().Contain(p => p.Code == "shared_catalog_needs_group_scope");

        // Step 6: another company-scoped admin for their own company (never group scope).
        var invite = body with { CompanyAdmin = new CompanySetupCompanyAdmin(V.AdminInvite, null, "Second Admin", null, "second.admin@sister.example") };
        var invitePlan = await DryRunAsync(w, invite, caller);
        invitePlan.Problems.Should().NotContain(p => p.Code == "admin_grant_not_permitted");
        invitePlan.Items.Single(i => i.Area == V.Areas.CompanyAdmin).Action.Should().Be(V.ActionCreate);

        // On a NEW company the same caller still may not grant it (create mode is group only).
        await using var db = fixture.CreateDb();
        var createPlan = await Service(db).DryRunAsync(caller, NewCompanyBody() with
        {
            CompanyAdmin = new CompanySetupCompanyAdmin(V.AdminInvite, null, "X", null, "x@group.example"),
        }, default);
        createPlan.Problems.Should().Contain(p => p.Code == "admin_grant_not_permitted");
    }

    [Fact]
    public async Task A_company_super_admin_s_invite_is_scoped_to_their_own_company_only()
    {
        var w = await SeedAsync();
        var caller = Caller(w, w.CompanyAdminId, w.SisterId);
        var body = await PrefillAsync(w, w.SisterId, caller);
        var invite = body with { CompanyAdmin = new CompanySetupCompanyAdmin(V.AdminInvite, null, "Second Admin", "المسؤول الثاني", "second.admin@sister.example") };
        var (_, result) = await DraftAndCommitAsync(w, invite, caller);
        result.Status.Should().Be(201, JsonSerializer.Serialize(result.Error));

        await using var db = fixture.CreateDb();
        var user = await db.Users.IgnoreQueryFilters().Include(u => u.UserRoles).ThenInclude(r => r.Role).Include(u => u.EntityAccesses)
            .SingleAsync(u => u.TenantId == w.TenantId && u.NormalizedEmail == AuthService.Normalize("second.admin@sister.example"));
        user.IsGroupScope.Should().BeFalse("a company admin never grants group scope");
        user.UserRoles.Should().ContainSingle(r => r.Role!.NormalizedName == "ADMIN");
        user.EntityAccesses.Where(e => e.IsActive).Should().ContainSingle().Which.CompanyId.Should().Be(w.SisterId);
        result.Value!.Readiness.Single(i => i.Key == "companyAdmin").Status.Should().Be(V.ReadinessTodo, "no admin of this company has signed in yet");
    }

    [Fact]
    public async Task A_company_super_admin_never_writes_workspace_wide_defaults_and_is_told_so()
    {
        var w = await SeedAsync();
        await using (var db = fixture.CreateDb())
        {
            db.LeavePolicies.RemoveRange(await db.LeavePolicies.IgnoreQueryFilters().Where(x => x.TenantId == w.TenantId).ToListAsync());
            db.GCCComplianceSettings.RemoveRange(await db.GCCComplianceSettings.IgnoreQueryFilters().Where(x => x.TenantId == w.TenantId && x.CompanyId == null).ToListAsync());
            await db.SaveChangesAsync();
        }
        var caller = Caller(w, w.CompanyAdminId, w.SisterId);
        var body = await PrefillAsync(w, w.SisterId, caller);
        var plan = await DryRunAsync(w, body, caller);
        plan.Ok.Should().BeTrue(JsonSerializer.Serialize(plan.Problems));
        plan.Items.Should().NotContain(i => (i.Area == V.Areas.LeavePolicy || i.Area == V.Areas.GccSetting) && i.Action == V.ActionCreate);
        plan.Warnings.Count(x => x.Code == "workspace_defaults_need_group_scope").Should().Be(2);
        var (_, result) = await DraftAndCommitAsync(w, body, caller);
        result.Status.Should().Be(201, JsonSerializer.Serialize(result.Error));
        await using var verify = fixture.CreateDb();
        (await verify.LeavePolicies.IgnoreQueryFilters().CountAsync(x => x.TenantId == w.TenantId)).Should().Be(0);
        (await verify.GCCComplianceSettings.IgnoreQueryFilters().CountAsync(x => x.TenantId == w.TenantId && x.CompanyId == null)).Should().Be(0);
        // The group admin's plan for the same company does add them.
        var groupPlan = await DryRunAsync(w, await PrefillAsync(w, w.SisterId));
        groupPlan.Items.Should().Contain(i => i.Area == V.Areas.GccSetting && i.Action == V.ActionCreate);
        groupPlan.Items.Should().Contain(i => i.Area == V.Areas.LeavePolicy && i.Action == V.ActionCreate);
    }

    [Fact]
    public async Task A_draft_or_inactive_company_is_refused()
    {
        var w = await SeedAsync();
        await using (var db = fixture.CreateDb())
        {
            var sister = await db.Companies.IgnoreQueryFilters().SingleAsync(c => c.Id == w.SisterId);
            sister.ApprovalStatus = CompanyApprovalStatuses.Draft;
            sister.IsActive = false;
            await db.SaveChangesAsync();
        }
        await using var check = fixture.CreateDb();
        var outcome = await Service(check).GetExistingDefaultsAsync(Caller(w), w.SisterId, default);
        outcome.Status.Should().Be(409);
        ErrorCode(outcome.Error).Should().Be("company_not_active");
        var context = await Service(check).GetContextAsync(Caller(w), default);
        context.ManageableCompanies.Select(c => c.Id).Should().NotContain(w.SisterId);
    }

    [Fact]
    public async Task The_context_lists_manageable_companies_with_readiness_that_reconciles_to_the_checklist()
    {
        var w = await SeedAsync();
        await using var db = fixture.CreateDb();
        var service = Service(db);
        var body = await PrefillAsync(w, w.CompanyId);
        var draft = (await service.CreateDraftAsync(Caller(w), body, default)).Value!;

        var context = await service.GetContextAsync(Caller(w), default);
        context.ManageableCompanies.Select(c => c.Id).Should().BeEquivalentTo([w.SisterId, w.CompanyId]);
        var card = context.ManageableCompanies.Single(c => c.Id == w.CompanyId);
        card.OpenDraftId.Should().Be(draft.Id);
        card.CountryCode.Should().Be("SA");
        var checklist = await service.ReadinessAsync(w.TenantId, w.CompanyId, default);
        card.ReadinessTotal.Should().Be(checklist!.Count);
        card.ReadinessDone.Should().Be(checklist.Count(i => i.Status == V.ReadinessDone));
        context.ManageableCompanies.Single(c => c.Id == w.SisterId).OpenDraftId.Should().BeNull();

        var json = JsonSerializer.Serialize(context, Web);
        json.Should().Contain("\"manageableCompanies\":[").And.Contain("\"readinessDone\"").And.Contain("\"openDraftId\"");
        var summaries = await service.ListDraftsAsync(Caller(w), default);
        summaries.Single(s => s.Id == draft.Id).ExistingCompanyId.Should().Be(w.CompanyId);
    }

    // ── A1: one open draft per existing company; the draft's target is fixed ─────────────────────────────

    [Fact]
    public async Task One_open_draft_per_existing_company_even_when_two_tabs_race()
    {
        var w = await SeedAsync();
        var body = await PrefillAsync(w, w.CompanyId);
        async Task<CompanySetupOutcome<CompanySetupDraft>> Create()
        {
            await using var db = fixture.CreateDb();
            return await Service(db).CreateDraftAsync(Caller(w), body, default);
        }
        var results = await Task.WhenAll(Create(), Create(), Create());
        results.Count(r => r.Status == 201).Should().Be(1, "the tenant setup lock serializes the check and the insert");
        var winner = results.Single(r => r.Status == 201).Value!;
        foreach (var loser in results.Where(r => r.Status != 201))
        {
            loser.Status.Should().Be(409);
            var error = ErrorBody(loser.Error);
            error.GetProperty("code").GetString().Should().Be(CompanySetupGates.ExistingDraftOpenCode);
            error.GetProperty("draftId").GetGuid().Should().Be(winner.Id, "the open draft to resume");
        }

        // Another company, and a new-company draft, are unaffected; discarding frees the slot.
        (await Create()).Status.Should().Be(409);
        await using (var db = fixture.CreateDb())
        {
            (await Service(db).CreateDraftAsync(Caller(w), await PrefillAsync(w, w.SisterId), default)).Status.Should().Be(201);
            (await Service(db).DiscardDraftAsync(Caller(w), winner.Id, default)).Status.Should().Be(204);
        }
        (await Create()).Status.Should().Be(201);
    }

    [Fact]
    public async Task A_draft_keeps_the_company_it_was_started_for()
    {
        var w = await SeedAsync();
        var body = await PrefillAsync(w, w.CompanyId);
        await using var db = fixture.CreateDb();
        var service = Service(db);
        var draft = (await service.CreateDraftAsync(Caller(w), body, default)).Value!;
        var retarget = await service.UpdateDraftAsync(Caller(w), draft.Id, new CompanySetupDraftUpdate(draft.Version, 3, body with { ExistingCompanyId = w.SisterId }), default);
        retarget.Status.Should().Be(409);
        ErrorCode(retarget.Error).Should().Be("draft_target_locked");
        var toCreate = await service.UpdateDraftAsync(Caller(w), draft.Id, new CompanySetupDraftUpdate(draft.Version, 3, body with { ExistingCompanyId = null }), default);
        toCreate.Status.Should().Be(409);
        (await service.UpdateDraftAsync(Caller(w), draft.Id, new CompanySetupDraftUpdate(draft.Version, 3, body), default)).Status.Should().Be(200);
    }

    // ── A6: commit idempotency ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_retried_existing_mode_commit_replays_the_first_result_and_changes_nothing_more()
    {
        var w = await SeedAsync();
        var body = await PrefillAsync(w, w.CompanyId);
        body = body with { Branches = [.. body.Branches!, new CompanySetupBranch("TIF", "Taif", null, "Taif", "Asia/Riyadh", false, [])] };
        var (draft, first) = await DraftAndCommitAsync(w, body);
        first.Status.Should().Be(201, JsonSerializer.Serialize(first.Error));
        var after = await FingerprintAsync(w.TenantId);

        await using var db = fixture.CreateDb();
        var retry = await Service(db).CommitAsync(Caller(w), new CompanySetupCommitRequest(draft.Id, draft.Version), draft.Id, default);
        retry.Status.Should().Be(200);
        retry.Value!.Replayed.Should().BeTrue();
        retry.Value.CompanyId.Should().Be(w.CompanyId);
        retry.Value.Created.Should().BeEquivalentTo(first.Value!.Created);
        (await FingerprintAsync(w.TenantId)).Should().Be(after, "a replay writes nothing");
        await using var verify = fixture.CreateDb();
        (await verify.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.TenantId == w.TenantId && a.Action == CompanySetupService.SetupCompletedAction)).Should().Be(1);
        (await verify.Branches.IgnoreQueryFilters().CountAsync(b => b.CompanyId == w.CompanyId && b.Code == "TIF")).Should().Be(1);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────

    private static CompanySetupController Controller(ZayraDbContext db, Guid tenantId, Guid userId, Guid? scopedCompany = null, string role = "Admin")
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()), new(ClaimTypes.NameIdentifier, userId.ToString()), new(ClaimTypes.Role, role),
            new("permission", "organization.setup.apply"), new("permission", "security.manage"),
            new(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(scopedCompany is { } company
                ? new { v = 2, m = "companies", c = new[] { company } }
                : new { v = 2, m = "group", c = Array.Empty<Guid>() })),
        };
        var audit = new AuditService(db);
        return new CompanySetupController(db, new OrganizationSetupService(db, audit),
            new AccessManagementService(db, new Pbkdf2PasswordHasher(), audit, new FakeTokenService()), new WorkWeekService(db), new NoEmail())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) } },
        };
    }

    private sealed class FakeTokenService : ITokenService
    {
        public string CreateAccessToken(User user, IReadOnlyCollection<string> roles, IReadOnlyCollection<string> permissions, Tenant tenant,
            IReadOnlyCollection<EntityAccessGrant> entityAccess, EntityScopeDescriptor entityScope, out DateTime expiresAtUtc)
        {
            expiresAtUtc = DateTime.UtcNow.AddHours(1);
            return $"fake-access-{user.Id}";
        }
        public string CreateSecureToken() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        public string HashToken(string token) => Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    }

    private sealed class NoEmail : Zayra.Api.Infrastructure.Email.IEmailService
    {
        public Task SendAsync(string toAddress, string toName, string subject, string htmlBody,
            IReadOnlyList<Zayra.Api.Infrastructure.Email.EmailAttachment>? attachments = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
