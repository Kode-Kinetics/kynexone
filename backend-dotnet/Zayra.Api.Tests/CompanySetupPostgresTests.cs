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
/// The create-company wizard (docs/design/company-setup-wizard.md, "Tests that must exist") against real
/// Postgres: the commit is one transaction under the tenant setup lock, so atomicity, the lost-response replay,
/// concurrent commits and the plan-limit race can only be proven where locks and transactions exist. Each
/// scenario seeds its own tenant on the shared container.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class CompanySetupPostgresTests(PostgresFixture fixture)
{
    private static readonly string[] AdminKeys =
        ["security.manage", "organization.setup.apply", "organization.write", "organization.read", "employees.read", "profile.read"];

    private sealed record World(Guid TenantId, Guid AdminId, Guid SisterId, Guid SisterDepartmentId, Guid SisterBranchId, Guid CompanyAdminId);

    // ── seed ────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<World> SeedAsync(string mode = CompanyCreationModes.GroupSelfServiceWithinLimit, int maxCompanies = 5,
        bool withSaGcc = false)
    {
        await using var db = fixture.CreateDb();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant
        {
            Id = tenantId, Name = "Wizard Group", Slug = $"wizard-{tenantId:N}",
            AccountType = TenantAccountTypes.Group, CompanyCreationMode = mode,
        });
        db.TenantSubscriptions.Add(new TenantSubscription
        {
            TenantId = tenantId, Plan = "Group", Status = SubscriptionStatuses.Active, MaxCompanies = maxCompanies, MaxUsers = 0, MaxAdminUsers = 0,
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
        db.Roles.Add(new Role { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Employee", NormalizedName = "EMPLOYEE", Description = "Employee", IsActive = true });

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
        var hr = new Department { TenantId = tenantId, Code = "HR", NameEn = "Human Resources", BranchId = sisterBranch.Id, CostCenterId = sisterCc.Id };
        db.Departments.Add(hr);
        db.Grades.Add(new Grade { TenantId = tenantId, Code = "G1", Name = "Grade 1", Level = 1, MinSalary = 3000, MidSalary = 4500, MaxSalary = 6000, Currency = "SAR" });
        if (withSaGcc)
            db.GCCComplianceSettings.Add(new GCCComplianceSetting
            {
                TenantId = tenantId, CountryCode = "SA", EosbEnabled = true, EosbYears1To5Rate = 21m, EosbYearsAbove5Rate = 30m,
                WeekendDays = "Fri,Sat", WorkWeek = "Sun-Thu", WpsAgentId = "",
            });
        await db.SaveChangesAsync();

        var adminId = await AddUserAsync(tenantId, "Group Admin", adminRole.Id, groupScope: true);
        // An existing company-scoped Admin of the sister company (for "make an existing user the company admin").
        var companyAdminId = await AddUserAsync(tenantId, "Sister Admin", adminRole.Id, groupScope: false, grantCompany: sister.Id);
        return new World(tenantId, adminId, sister.Id, hr.Id, sisterBranch.Id, companyAdminId);
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

    private static CompanySetupService Service(ZayraDbContext db)
    {
        var audit = new AuditService(db);
        return new CompanySetupService(db, new OrganizationSetupService(db, audit),
            new AccessManagementService(db, new Pbkdf2PasswordHasher(), audit, new FakeTokenService()), new WorkWeekService(db), new NoEmail());
    }

    private static CompanySetupCaller Caller(World w, bool group = true, Guid? userId = null)
    {
        var uid = userId ?? w.AdminId;
        return new CompanySetupCaller(w.TenantId, uid, group, false, new RequestContext("127.0.0.1", "tests", uid, w.TenantId));
    }

    private static CompanySetupDraftBody Body(
        string name = "New Gulf Co", string cr = "1010000099", string? emailDomain = null,
        List<CompanySetupCostCentre>? costCentres = null, List<CompanySetupDepartment>? departments = null,
        CompanySetupCompanyAdmin? admin = null, List<CompanySetupGrade>? grades = null) =>
        new(V.StartFromDefaults, null,
            new CompanySetupCompany(name, "شركة الخليج الجديدة", null, "SA", "SAR", cr, "7-1234567", null, "7001234567", null, null,
                emailDomain, null, null, "2026-11"),
            [new CompanySetupBranch("JED", "Jeddah HQ", null, "Jeddah", "Asia/Riyadh", true,
                [new CompanySetupLocation($"JED-L{cr[^4..]}", "Jeddah site", null, 21.5m, 39.2m, 150m)])],
            costCentres ?? [new CompanySetupCostCentre($"CC_OPS_{cr[^4..]}", "Operations cost centre", null)],
            departments ?? [new CompanySetupDepartment($"OPS{cr[^4..]}", "Operations", null, null, "JED", $"CC_OPS_{cr[^4..]}")],
            [new CompanySetupDesignation($"OPS_OFF{cr[^4..]}", "Operations Officer", null, $"OPS{cr[^4..]}", "G1")],
            grades ?? [],
            new CompanySetupRules(new CompanySetupWorkWeek("Fri,Sat", false), new CompanySetupPackChoice(true), new CompanySetupPackChoice(true),
                new CompanySetupEmployeeNumbering("NGC", true, false, true, true, 4, 1)),
            admin ?? new CompanySetupCompanyAdmin(V.AdminGroupOnly, null, null, null, null));

    private async Task<CompanySetupDraft> NewDraftAsync(World w, CompanySetupDraftBody body)
    {
        await using var db = fixture.CreateDb();
        var created = await Service(db).CreateDraftAsync(Caller(w), body, default);
        created.Status.Should().Be(201, JsonSerializer.Serialize(created.Error));
        return created.Value!;
    }

    private async Task<CompanySetupOutcome<CompanySetupResult>> CommitAsync(World w, CompanySetupDraft draft, Guid? key = null)
    {
        await using var db = fixture.CreateDb();
        return await Service(db).CommitAsync(Caller(w), new CompanySetupCommitRequest(draft.Id, draft.Version), key ?? draft.Id, default);
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static JsonElement ErrorBody(object? error) => JsonDocument.Parse(JsonSerializer.Serialize(error, Web)).RootElement;
    private static string ErrorCode(object? error) => ErrorBody(error).GetProperty("code").GetString()!;

    // ── the commit ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Commit_creates_the_company_and_its_setup_in_one_transaction_and_audits_it()
    {
        var w = await SeedAsync();
        var draft = await NewDraftAsync(w, Body());
        var outcome = await CommitAsync(w, draft);
        outcome.Status.Should().Be(201, JsonSerializer.Serialize(outcome.Error));
        var result = outcome.Value!;
        result.Replayed.Should().BeFalse();
        await using (var planDb = fixture.CreateDb())
        {
            var plan = await Service(planDb).DryRunAsync(Caller(w), Body(name: "Label Check Co", cr: "1010000098"), default);
            plan.Items.Single(i => i.Area == "company").LabelAr.Should().Be("شركة الخليج الجديدة");
        }

        await using var db = fixture.CreateDb();
        var company = await db.Companies.AsNoTracking().SingleAsync(c => c.Id == result.CompanyId);
        company.TenantId.Should().Be(w.TenantId);
        company.WpsEmployerId.Should().Be("7-1234567");
        company.QiwaEstablishmentId.Should().Be("7-1234567", "the Qiwa establishment id defaults to the MOL number");
        company.UnifiedNationalNumber.Should().Be("7001234567");
        company.IsActive.Should().BeTrue();
        company.ApprovalStatus.Should().Be(CompanyApprovalStatuses.Active);
        (await db.Branches.CountAsync(b => b.CompanyId == company.Id)).Should().Be(1);
        (await db.Locations.CountAsync(l => l.TenantId == w.TenantId && l.Code == "JED-L0099")).Should().Be(1);
        (await db.CostCenters.SingleAsync(c => c.Code == "CC_OPS_0099" && c.TenantId == w.TenantId)).CompanyId.Should().Be(company.Id);
        var ops = await db.Departments.SingleAsync(d => d.TenantId == w.TenantId && d.Code == "OPS0099");
        ops.CostCenterId.Should().NotBeNull();
        (await db.Designations.SingleAsync(d => d.TenantId == w.TenantId && d.Code == "OPS_OFF0099")).GradeId.Should().NotBeNull("G1 is reused");
        (await db.EmployeeIdRules.SingleAsync(r => r.TenantId == w.TenantId && r.CompanyId == company.Id)).CompanyPrefix.Should().Be("NGC");
        var cutover = await db.CompanyCutovers.SingleAsync(c => c.TenantId == w.TenantId && c.CompanyId == company.Id);
        cutover.CutoverDate.Should().Be(new DateOnly(2026, 11, 1));
        cutover.Status.Should().Be(CutoverStatuses.Planned);
        (await db.PublicHolidayCalendars.CountAsync(c => c.TenantId == w.TenantId && c.CompanyId == company.Id)).Should().Be(1);
        var gcc = await db.GCCComplianceSettings.SingleAsync(g => g.TenantId == w.TenantId && g.CompanyId == null && g.CountryCode == "SA");
        gcc.EosbEnabled.Should().BeTrue();
        gcc.EosbYears1To5Rate.Should().Be(15m, "Art. 84's half month per year, in the days-per-year unit the calculator reads");
        gcc.EosbYearsAbove5Rate.Should().Be(30m);
        gcc.WeekendDays.Should().Be("Fri,Sat");
        (await db.LeavePolicies.CountAsync(p => p.TenantId == w.TenantId && p.CompanyId == null && p.CountryCode == "SA")).Should().Be(2);

        var draftRow = await db.CompanySetupDrafts.SingleAsync(d => d.Id == draft.Id);
        draftRow.Status.Should().Be(CompanySetupDraftStatuses.Submitted);
        draftRow.ResultCompanyId.Should().Be(company.Id);
        var marker = await db.AuditLogs.SingleAsync(a => a.TenantId == w.TenantId && a.Action == CompanySetupService.SetupCreatedAction);
        marker.CompanyId.Should().Be(company.Id);
        marker.Metadata.Should().Contain("bodySha256").And.Contain("created");
        (await db.AuditLogs.AnyAsync(a => a.TenantId == w.TenantId && a.Action == "organization.company_created" && a.EntityId == company.Id.ToString())).Should().BeTrue();
        (await db.AuditLogs.AnyAsync(a => a.TenantId == w.TenantId && a.Action == "organization.branch_created")).Should().BeTrue();
        (await db.AuditLogs.AnyAsync(a => a.TenantId == w.TenantId && a.Action == "organization.company_cutover_planned")).Should().BeTrue();

        result.Readiness.Should().NotBeEmpty();
        result.Readiness.Should().OnlyContain(r => r.Href.StartsWith("/"));
        result.Readiness.Single(r => r.Key == "glMapping").Href.Should().Be($"/setup?tab=glMapping&companyId={company.Id}");
        result.Readiness.Single(r => r.Key == "companyAdmin").Status.Should().Be(V.ReadinessRecommended, "group admins manage it");
    }

    [Fact]
    public async Task Lost_response_retry_replays_the_same_result_and_creates_nothing_more()
    {
        var w = await SeedAsync();
        var draft = await NewDraftAsync(w, Body());
        var first = await CommitAsync(w, draft);
        first.Status.Should().Be(201);
        var retry = await CommitAsync(w, draft);
        retry.Status.Should().Be(200);
        retry.Value!.Replayed.Should().BeTrue();
        retry.Value.CompanyId.Should().Be(first.Value!.CompanyId);
        retry.Value.Created.Should().BeEquivalentTo(first.Value.Created);
        retry.Value.Reused.Should().BeEquivalentTo(first.Value.Reused);
        await using var db = fixture.CreateDb();
        (await db.Companies.CountAsync(c => c.TenantId == w.TenantId && c.LegalNameEn == "New Gulf Co")).Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_commits_of_one_draft_create_one_company()
    {
        var w = await SeedAsync();
        var draft = await NewDraftAsync(w, Body());
        var results = await Task.WhenAll(CommitAsync(w, draft), CommitAsync(w, draft));
        results.Select(r => r.Status).Should().BeEquivalentTo([201, 200]);
        results.Select(r => r.Value!.CompanyId).Distinct().Should().HaveCount(1);
        await using var db = fixture.CreateDb();
        (await db.Companies.CountAsync(c => c.TenantId == w.TenantId && c.LegalNameEn == "New Gulf Co")).Should().Be(1);
    }

    [Fact]
    public async Task Idempotency_key_must_be_the_draft_id()
    {
        var w = await SeedAsync();
        var draft = await NewDraftAsync(w, Body());
        var outcome = await CommitAsync(w, draft, key: Guid.NewGuid());
        outcome.Status.Should().Be(400);
        ErrorCode(outcome.Error).Should().Be("idempotency_key_mismatch");
    }

    [Fact]
    public async Task A_legal_name_clash_is_409_and_creates_nothing()
    {
        var w = await SeedAsync();
        var draft = await NewDraftAsync(w, Body(name: "SISTER trading co"));
        var outcome = await CommitAsync(w, draft);
        outcome.Status.Should().Be(409);
        ErrorCode(outcome.Error).Should().Be(CompanySetupGates.NameTakenCode);
        await AssertNothingCreatedAsync(w);
    }

    [Fact]
    public async Task A_cr_number_clash_is_409_and_creates_nothing()
    {
        var w = await SeedAsync();
        var draft = await NewDraftAsync(w, Body(cr: "1010000001"));
        var outcome = await CommitAsync(w, draft);
        outcome.Status.Should().Be(409);
        ErrorCode(outcome.Error).Should().Be(CompanySetupGates.CrTakenCode);
        // The contract's error shape: { code, message, messageAr?, field?, step? } with a dotted field path.
        var body = ErrorBody(outcome.Error);
        body.GetProperty("field").GetString().Should().Be("company.registrationNumber");
        body.GetProperty("step").GetInt32().Should().Be(2);
        body.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        body.GetProperty("messageAr").GetString().Should().NotBeNullOrWhiteSpace();
        await AssertNothingCreatedAsync(w);
    }

    [Fact]
    public async Task A_cost_centre_code_clash_blocks_the_commit()
    {
        var w = await SeedAsync();
        var body = Body(costCentres: [new CompanySetupCostCentre("cc_hr", "Clashing", null)], departments: []);
        await using (var db = fixture.CreateDb())
        {
            var plan = await Service(db).DryRunAsync(Caller(w), body, default);
            plan.Ok.Should().BeFalse();
            plan.Items.Should().Contain(i => i.Area == "costCentre" && i.Code == "CC_HR" && i.Action == V.ActionConflict);
            plan.Problems.Should().Contain(p => p.Code == "cost_centre_code_taken" && p.Field == "costCentres.0.code");
        }
        var draft = await NewDraftAsync(w, body with { Designations = [] });
        (await CommitAsync(w, draft)).Status.Should().Be(409);
        await AssertNothingCreatedAsync(w);
    }

    [Fact]
    public async Task An_existing_department_is_reused_and_never_relinked()
    {
        var w = await SeedAsync();
        var body = Body(departments: [new CompanySetupDepartment("HR", "Human Resources", null, null, "JED", "CC_OPS_0099")]) with { Designations = [] };
        await using (var db = fixture.CreateDb())
        {
            var plan = await Service(db).DryRunAsync(Caller(w), body, default);
            plan.Items.Should().Contain(i => i.Area == "department" && i.Code == "HR" && i.Action == V.ActionReuse);
            plan.Warnings.Should().Contain(p => p.Code == "existing_department_not_relinked");
        }
        var draft = await NewDraftAsync(w, body);
        (await CommitAsync(w, draft)).Status.Should().Be(201);
        await using var verify = fixture.CreateDb();
        var hr = await verify.Departments.AsNoTracking().SingleAsync(d => d.Id == w.SisterDepartmentId);
        hr.BranchId.Should().Be(w.SisterBranchId);
        hr.CostCenterId.Should().Be((await verify.CostCenters.SingleAsync(c => c.TenantId == w.TenantId && c.Code == "CC_HR")).Id);
    }

    [Fact]
    public async Task A_company_scoped_user_cannot_add_shared_catalog_codes()
    {
        var w = await SeedAsync();
        await using var db = fixture.CreateDb();
        var plan = await Service(db).DryRunAsync(Caller(w, group: false), Body(grades: [new CompanySetupGrade("G9", "Grade 9", null, 9, 1, 2, 3)]), default);
        plan.Ok.Should().BeFalse();
        plan.Problems.Should().Contain(p => p.Code == CompanySetupGates.GroupScopeRequiredCode);
        plan.Items.Where(i => i.Area is "department" or "designation" or "grade" && i.Action != V.ActionReuse)
            .Should().NotBeEmpty().And.OnlyContain(i => i.Action == V.ActionConflict);
        plan.Problems.Should().Contain(p => p.Code == "shared_catalog_needs_group_scope");
    }

    [Fact]
    public async Task A_draft_approval_tenant_is_refused_at_context_and_cannot_start_a_draft()
    {
        var w = await SeedAsync(mode: CompanyCreationModes.GroupDraftPlatformApproval);
        await using var db = fixture.CreateDb();
        var service = Service(db);
        var context = await service.GetContextAsync(Caller(w), default);
        context.CanCreate.Should().BeFalse();
        context.RefusalCode.Should().Be("company_creation_platform_approval");
        context.CreationMode.Should().Be(CompanyCreationModes.GroupDraftPlatformApproval);
        var draft = await service.CreateDraftAsync(Caller(w), Body(), default);
        draft.Status.Should().Be(409, "no stranded drafts");
        (await db.CompanySetupDrafts.AnyAsync(d => d.TenantId == w.TenantId)).Should().BeFalse();
    }

    [Fact]
    public async Task The_plan_limit_is_refused_at_context_and_rechecked_at_commit()
    {
        var w = await SeedAsync(maxCompanies: 2);
        var draft = await NewDraftAsync(w, Body());
        await using (var db = fixture.CreateDb())
            (await Service(db).GetContextAsync(Caller(w), default)).Should().Match<CompanySetupContext>(c =>
                c.CanCreate && c.CompaniesUsed == 1 && c.CompaniesAllowed == 2 && c.CanGrantCompanyAdmin && c.GradesCurrency == "SAR");
        // The last seat is taken by another door after the draft was started.
        await using (var db = fixture.CreateDb())
        {
            db.Companies.Add(new Company { TenantId = w.TenantId, LegalNameEn = "Raced Co", RegistrationNumber = "1010000077", CountryCode = "SA", DefaultCurrency = "SAR" });
            await db.SaveChangesAsync();
        }
        var outcome = await CommitAsync(w, draft);
        outcome.Status.Should().Be(402);
        ErrorCode(outcome.Error).Should().Be("company_limit_reached");
        await using (var db = fixture.CreateDb())
        {
            var context = await Service(db).GetContextAsync(Caller(w), default);
            context.CanCreate.Should().BeFalse();
            context.RefusalCode.Should().Be("company_limit_reached");
            (await db.Companies.CountAsync(c => c.TenantId == w.TenantId && c.LegalNameEn == "New Gulf Co")).Should().Be(0);
        }
    }

    [Fact]
    public async Task Dry_run_and_commit_produce_the_same_item_list()
    {
        var w = await SeedAsync();
        var body = Body();
        CompanySetupPlan dryRun;
        await using (var db = fixture.CreateDb())
            dryRun = await Service(db).DryRunAsync(Caller(w), body, default);
        dryRun.Ok.Should().BeTrue(JsonSerializer.Serialize(dryRun.Problems));
        var draft = await NewDraftAsync(w, body);
        var result = (await CommitAsync(w, draft)).Value!;
        var (created, reused) = CompanySetupService.Counts(dryRun.Items);
        result.Created.Should().BeEquivalentTo(created);
        result.Reused.Should().BeEquivalentTo(reused);
    }

    [Fact]
    public async Task Every_plan_row_carries_an_arabic_label_for_the_arabic_review()
    {
        // The review renders labelAr in Arabic; a row without one showed English sentences in an Arabic screen.
        var w = await SeedAsync();
        await using var db = fixture.CreateDb();
        var plan = await Service(db).DryRunAsync(Caller(w), Body(), default);
        plan.Items.Where(i => i.Area is not ("branch" or "location" or "costCentre" or "department" or "designation" or "grade" or "companyAdmin"))
            .Where(i => string.IsNullOrWhiteSpace(i.LabelAr))
            .Select(i => $"{i.Area}:{i.Label}").Should().BeEmpty("fixed-text plan rows are written by the planner and must name themselves in Arabic too");
    }

    [Fact]
    public async Task The_country_settings_row_is_created_only_when_missing_and_never_overwritten()
    {
        var w = await SeedAsync(withSaGcc: true);
        await using (var db = fixture.CreateDb())
        {
            var plan = await Service(db).DryRunAsync(Caller(w), Body(), default);
            plan.Items.Should().Contain(i => i.Area == "gccSetting" && i.Action == V.ActionReuse);
        }
        var draft = await NewDraftAsync(w, Body());
        (await CommitAsync(w, draft)).Status.Should().Be(201);
        await using var verify = fixture.CreateDb();
        var rows = await verify.GCCComplianceSettings.AsNoTracking().Where(g => g.TenantId == w.TenantId && g.CountryCode == "SA").ToListAsync();
        rows.Should().ContainSingle();
        rows[0].EosbYears1To5Rate.Should().Be(21m, "an existing row is never overwritten");
    }

    [Fact]
    public async Task A_stale_draft_version_is_409()
    {
        var w = await SeedAsync();
        var draft = await NewDraftAsync(w, Body());
        await using var db = fixture.CreateDb();
        var service = Service(db);
        var saved = await service.UpdateDraftAsync(Caller(w), draft.Id, new CompanySetupDraftUpdate(1, 2, Body(name: "Renamed")), default);
        saved.Status.Should().Be(200);
        saved.Value!.Version.Should().Be(2);
        saved.Value.Step.Should().Be(2);
        var stale = await service.UpdateDraftAsync(Caller(w), draft.Id, new CompanySetupDraftUpdate(1, 3, Body(name: "Lost update")), default);
        stale.Status.Should().Be(409);
        ErrorCode(stale.Error).Should().Be("draft_version_stale");
        // The commit is held to the version that was reviewed, too.
        var commit = await Service(db).CommitAsync(Caller(w), new CompanySetupCommitRequest(draft.Id, 1), draft.Id, default);
        commit.Status.Should().Be(409);
    }

    [Fact]
    public async Task Another_tenants_draft_is_404_everywhere()
    {
        var mine = await SeedAsync();
        var theirs = await SeedAsync();
        var draft = await NewDraftAsync(theirs, Body());
        await using var db = fixture.CreateDb();
        var service = Service(db);
        (await service.GetDraftAsync(Caller(mine), draft.Id, default)).Should().BeNull();
        (await service.UpdateDraftAsync(Caller(mine), draft.Id, new CompanySetupDraftUpdate(1, 2, Body()), default)).Status.Should().Be(404);
        (await service.DiscardDraftAsync(Caller(mine), draft.Id, default)).Status.Should().Be(404);
        (await service.CommitAsync(Caller(mine), new CompanySetupCommitRequest(draft.Id, 1), draft.Id, default)).Status.Should().Be(404);
        (await service.ListDraftsAsync(Caller(mine), default)).Should().NotContain(d => d.Id == draft.Id);
    }

    [Fact]
    public async Task Discard_keeps_the_row_and_hides_it_from_resume()
    {
        var w = await SeedAsync();
        var draft = await NewDraftAsync(w, Body());
        await using var db = fixture.CreateDb();
        var service = Service(db);
        (await service.ListDraftsAsync(Caller(w), default)).Should().Contain(d => d.Id == draft.Id && d.LegalNameEn == "New Gulf Co" && d.CreatedByName == "Group Admin");
        (await service.DiscardDraftAsync(Caller(w), draft.Id, default)).Status.Should().Be(204);
        (await service.ListDraftsAsync(Caller(w), default)).Should().NotContain(d => d.Id == draft.Id);
        (await db.CompanySetupDrafts.AsNoTracking().SingleAsync(d => d.Id == draft.Id)).Status.Should().Be(CompanySetupDraftStatuses.Discarded);
        (await CommitAsync(w, draft)).Status.Should().Be(409);
    }

    [Fact]
    public async Task The_database_bounds_a_draft_body_at_128_KiB_and_holds_the_status_vocabulary()
    {
        var w = await SeedAsync();
        async Task<Exception?> Insert(Action<CompanySetupDraftRecord> shape)
        {
            await using var db = fixture.CreateDb();
            var row = new CompanySetupDraftRecord { TenantId = w.TenantId, CreatedByUserId = w.AdminId, Body = "{}" };
            shape(row);
            db.CompanySetupDrafts.Add(row);
            try { await db.SaveChangesAsync(); return null; } catch (DbUpdateException ex) { return ex; }
        }
        (await Insert(_ => { })).Should().BeNull();
        (await Insert(r => r.Body = JsonSerializer.Serialize(new { pad = new string('x', 140_000) }))).Should().NotBeNull("the 128 KiB CHECK");
        (await Insert(r => r.Status = "Approved")).Should().NotBeNull("the status CHECK");
        (await Insert(r => r.Step = 8)).Should().NotBeNull("the step CHECK");
        (await Insert(r => r.Status = CompanySetupDraftStatuses.Submitted)).Should().NotBeNull("Submitted needs submitted_at_utc");
    }

    // ── Company Super Admin ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_invited_company_super_admin_is_scoped_to_the_new_company_only()
    {
        var w = await SeedAsync();
        var admin = new CompanySetupCompanyAdmin(V.AdminInvite, null, "Sara Admin", "سارة", "sara.admin@newgulf.sa");
        var draft = await NewDraftAsync(w, Body(emailDomain: "newgulf.sa", admin: admin));
        var outcome = await CommitAsync(w, draft);
        outcome.Status.Should().Be(201, JsonSerializer.Serialize(outcome.Error));
        var companyId = outcome.Value!.CompanyId;

        await using var db = fixture.CreateDb();
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Include(u => u.UserRoles).ThenInclude(r => r.Role).Include(u => u.EntityAccesses)
            .SingleAsync(u => u.TenantId == w.TenantId && u.NormalizedEmail == "SARA.ADMIN@NEWGULF.SA");
        user.IsGroupScope.Should().BeFalse();
        user.UserRoles.Select(r => r.Role!.Name).Should().BeEquivalentTo(["Admin"]);
        user.EntityAccesses.Where(e => e.IsActive).Should().ContainSingle()
            .Which.Should().Match<UserEntityAccess>(e => e.CompanyId == companyId && e.GrantMode == EntityGrantModes.SelectedCompanies);
        // What their session resolves to: this company, and never the sister company.
        var activeCompanies = await db.Companies.AsNoTracking().Where(c => c.TenantId == w.TenantId && c.IsActive && !c.IsDeleted).Select(c => c.Id).ToListAsync();
        var scope = EntityScopeClaims.Resolve(user.IsGroupScope,
            user.EntityAccesses.Where(e => e.IsActive).Select(e => new EntityAccessGrant(e.CompanyId, e.Role, e.GrantMode)).ToList(), activeCompanies);
        scope.Mode.Should().Be(EntityScopeModes.Companies);
        scope.CompanyIds.Should().BeEquivalentTo([companyId]);
        scope.CompanyIds.Should().NotContain(w.SisterId);
        // Nobody typed a password for them: until they use the sign-in link, readiness says so.
        outcome.Value.Readiness.Single(r => r.Key == "companyAdmin").Status.Should().Be(V.ReadinessTodo);
    }

    [Fact]
    public async Task An_off_domain_invite_email_is_refused()
    {
        var w = await SeedAsync();
        var admin = new CompanySetupCompanyAdmin(V.AdminInvite, null, "Sara Admin", null, "sara@elsewhere.sa");
        await using var db = fixture.CreateDb();
        var plan = await Service(db).DryRunAsync(Caller(w), Body(emailDomain: "newgulf.sa", admin: admin), default);
        plan.Ok.Should().BeFalse();
        plan.Problems.Should().Contain(p => p.Code == "admin_email_off_domain" && p.Step == 6);
        var gmail = await Service(db).DryRunAsync(Caller(w), Body(admin: admin with { Email = "sara@gmail.com" }), default);
        gmail.Problems.Should().Contain(p => p.Code == "admin_email_public_domain");
    }

    [Fact]
    public async Task Making_an_existing_user_the_company_admin_keeps_their_other_access()
    {
        var w = await SeedAsync();
        var admin = new CompanySetupCompanyAdmin(V.AdminExisting, w.CompanyAdminId, null, null, null);
        var draft = await NewDraftAsync(w, Body(admin: admin));
        var outcome = await CommitAsync(w, draft);
        outcome.Status.Should().Be(201, JsonSerializer.Serialize(outcome.Error));
        await using var db = fixture.CreateDb();
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().Include(u => u.UserRoles).ThenInclude(r => r.Role).Include(u => u.EntityAccesses)
            .SingleAsync(u => u.Id == w.CompanyAdminId);
        user.IsGroupScope.Should().BeFalse("a company admin never widens to group scope");
        user.UserRoles.Select(r => r.Role!.Name).Should().BeEquivalentTo(["Admin"]);
        user.EntityAccesses.Where(e => e.IsActive).Select(e => e.CompanyId).Should().BeEquivalentTo(new Guid?[] { w.SisterId, outcome.Value!.CompanyId });
    }

    [Fact]
    public async Task An_existing_user_without_the_admin_role_is_not_silently_promoted()
    {
        var w = await SeedAsync();
        Guid employeeRole;
        await using (var db = fixture.CreateDb())
            employeeRole = (await db.Roles.IgnoreQueryFilters().SingleAsync(r => r.TenantId == w.TenantId && r.NormalizedName == "EMPLOYEE")).Id;
        var staff = await AddUserAsync(w.TenantId, "Staff Member", employeeRole, groupScope: false, grantCompany: w.SisterId);
        await using var verify = fixture.CreateDb();
        var plan = await Service(verify).DryRunAsync(Caller(w), Body(admin: new CompanySetupCompanyAdmin(V.AdminExisting, staff, null, null, null)), default);
        plan.Problems.Should().Contain(p => p.Code == "existing_user_not_admin");
    }

    [Fact]
    public async Task A_company_scoped_admin_cannot_open_the_wizard_or_grant_admin()
    {
        var w = await SeedAsync();
        await using var db = fixture.CreateDb();
        var controller = Controller(db, w.TenantId, w.CompanyAdminId, scopedCompany: w.SisterId);
        var context = (await controller.Context(default)).Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<CompanySetupContext>().Subject;
        context.CanCreate.Should().BeFalse();
        context.RefusalCode.Should().Be("not_group_scope");
        context.CanGrantCompanyAdmin.Should().BeFalse();
        context.SisterCompanies.Should().BeEmpty();
        ((ObjectResult)(await controller.DryRun(Body(), default)).Result!).StatusCode.Should().Be(403);
        ((ObjectResult)(await controller.CreateDraft(Body(), default)).Result!).StatusCode.Should().Be(403);
        ((ObjectResult)(await controller.Commit(new CompanySetupCommitRequest(Guid.NewGuid(), 1), Guid.NewGuid().ToString(), default)).Result!).StatusCode.Should().Be(403);
        // And through the planner: a company-scoped caller may not grant the company admin role.
        var plan = await Service(db).DryRunAsync(Caller(w, group: false, userId: w.CompanyAdminId),
            Body(admin: new CompanySetupCompanyAdmin(V.AdminInvite, null, "X", null, "x@group.example")), default);
        plan.Problems.Should().Contain(p => p.Code == "admin_grant_not_permitted");
    }

    // ── defaults, readiness, the company form lock ──────────────────────────────────────────────────────

    [Fact]
    public async Task Defaults_are_deterministic_and_copy_from_renames_codes()
    {
        var w = await SeedAsync();
        await using var db = fixture.CreateDb();
        var service = Service(db);
        var a = (await service.GetDefaultsAsync(Caller(w), "SA", null, default)).Value!;
        var b = (await service.GetDefaultsAsync(Caller(w), "SA", null, default)).Value!;
        JsonSerializer.Serialize(a).Should().Be(JsonSerializer.Serialize(b), "deterministic: no model call");
        a.Company.Currency.Should().Be("SAR");
        a.Rules.WorkWeek.WeekendDays.Should().Be("Fri,Sat");
        a.CostCentres!.Select(c => c.Code).Should().NotContain("CC_HR", "a template cost centre never clashes with a sister's code");
        a.Departments!.Single(d => d.Code == "HR").CostCentreCode.Should().BeNull("an existing department is reused, not re-linked");

        var copy = (await service.GetDefaultsAsync(Caller(w), null, w.SisterId, default)).Value!;
        copy.StartFrom.Should().Be(V.StartFromCopy);
        copy.Company.CountryCode.Should().Be("SA");
        copy.Branches!.Should().ContainSingle().Which.Code.Should().Be("HQ-2");
        copy.CostCentres!.Should().ContainSingle().Which.Code.Should().Be("CC_HR-2");
        (await service.GetDefaultsAsync(Caller(w, group: false), null, w.SisterId, default)).Status.Should().Be(404);
    }

    [Fact]
    public async Task Concurrent_company_form_creates_cannot_exceed_the_plan_limit()
    {
        var w = await SeedAsync(maxCompanies: 2);
        async Task<ActionResult<CompanyDto>> Create(string name, string cr)
        {
            await using var db = fixture.CreateDb();
            var controller = Controller(db, w.TenantId, w.AdminId);
            return await CompanyEndpoints.CreateAsync(controller, new OrganizationSetupService(db, new AuditService(db)), db,
                new CompanyRequest(name, null, null, "SA", null, cr, null, null, null, null, "SAR"),
                new RequestContext("127.0.0.1", "tests", w.AdminId, w.TenantId), c => new OkObjectResult(c), default);
        }
        var results = await Task.WhenAll(Create("Form Co A", "1010000201"), Create("Form Co B", "1010000202"));
        results.Count(r => r.Result is OkObjectResult).Should().Be(1, "the tenant setup lock serializes the gate and the insert");
        results.Count(r => r.Result is ObjectResult { StatusCode: 402 }).Should().Be(1);
        await using var verify = fixture.CreateDb();
        (await verify.Companies.CountAsync(c => c.TenantId == w.TenantId)).Should().Be(2);
    }

    private async Task AssertNothingCreatedAsync(World w)
    {
        await using var db = fixture.CreateDb();
        (await db.Companies.CountAsync(c => c.TenantId == w.TenantId)).Should().Be(1, "only the sister company");
        (await db.CostCenters.CountAsync(c => c.TenantId == w.TenantId)).Should().Be(1);
        (await db.Departments.CountAsync(c => c.TenantId == w.TenantId)).Should().Be(1);
        (await db.Branches.CountAsync(c => c.TenantId == w.TenantId)).Should().Be(1);
        (await db.CompanyCutovers.CountAsync(c => c.TenantId == w.TenantId)).Should().Be(0);
        (await db.GCCComplianceSettings.CountAsync(c => c.TenantId == w.TenantId)).Should().Be(0);
        (await db.AuditLogs.CountAsync(a => a.TenantId == w.TenantId && a.Action == CompanySetupService.SetupCreatedAction)).Should().Be(0);
    }

    private static CompanySetupController Controller(ZayraDbContext db, Guid tenantId, Guid userId, Guid? scopedCompany = null)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()), new(ClaimTypes.NameIdentifier, userId.ToString()), new(ClaimTypes.Role, "Admin"),
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
