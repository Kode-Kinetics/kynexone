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
using Zayra.Api.Infrastructure.Employees.Onboarding;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.WorkWeek;
using Zayra.Api.Models;
using V = Zayra.Api.Application.CompanySetup.CompanySetupVocabulary;

namespace Zayra.Api.Tests;

/// <summary>
/// The independent pay reviewer of the create-company wizard: a direct login (Finance Approver, this company only) set up
/// beside the company super admin, usable before any employee record is onboarded, verified by the existing
/// independentPayReview readiness item, which stays derived from PayApprovalRouting.UsableOperatorsAsync.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class CompanySetupPayReviewerPostgresTests(PostgresFixture fixture)
{
    private static readonly string[] AdminKeys =
    [
        "security.manage", "organization.setup.apply", "organization.write", "organization.read", "employees.read", "employees.write",
        "profile.read", "payroll.approve", "approvals.read",
    ];

    private sealed record World(Guid TenantId, Guid AdminId, Guid SisterId);

    private async Task<World> SeedAsync(bool withFinanceApprover = true, int maxUsers = 0)
    {
        await using var db = fixture.CreateDb();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant
        {
            Id = tenantId, Name = "Reviewer Group", Slug = $"rev-{tenantId:N}",
            AccountType = TenantAccountTypes.Group, CompanyCreationMode = CompanyCreationModes.GroupSelfServiceWithinLimit,
        });
        db.TenantSubscriptions.Add(new TenantSubscription
        {
            TenantId = tenantId, Plan = "Group", Status = SubscriptionStatuses.Active, MaxCompanies = 5, MaxUsers = maxUsers, MaxAdminUsers = 0,
        });
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var key in AdminKeys)
        {
            var existing = await db.Permissions.SingleOrDefaultAsync(x => x.Key == key);
            if (existing is null)
            {
                existing = new Permission { Key = key, Module = "Test", Description = key };
                db.Permissions.Add(existing);
                await db.SaveChangesAsync();
            }
            ids[key] = existing.Id;
        }
        Role AddRole(string name, params string[] keys)
        {
            var role = new Role
            {
                Id = Guid.NewGuid(), TenantId = tenantId, Name = name, NormalizedName = AuthService.Normalize(name), Description = name,
                IsSystem = true, IsEditable = false, IsActive = true, AuthorityLevel = 1,
            };
            db.Roles.Add(role);
            foreach (var key in keys) db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = ids[key] });
            return role;
        }
        var admin = AddRole("Admin", AdminKeys);
        if (withFinanceApprover) AddRole("Finance Approver", "payroll.approve", "approvals.read");
        AddRole("Employee", "profile.read");
        var sister = new Company
        {
            TenantId = tenantId, LegalNameEn = "Sister Trading Co", CountryCode = "SA", DefaultCurrency = "SAR",
            RegistrationNumber = "1010000001", IsActive = true, ApprovalStatus = CompanyApprovalStatuses.Active,
        };
        db.Companies.Add(sister);
        await db.SaveChangesAsync();
        var adminId = await AddUserAsync(tenantId, "Group Admin", admin.Id, groupScope: true);
        return new World(tenantId, adminId, sister.Id);
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
            // The setup caller is deliberately not a usable pay operator here: any other usable operator would satisfy the check on its own.
            MustChangePassword = groupScope,
        });
        db.UserRoles.Add(new UserRole { UserId = id, RoleId = roleId });
        if (grantCompany is Guid company)
            db.UserEntityAccesses.Add(new UserEntityAccess
            {
                TenantId = tenantId, UserId = id, CompanyId = company, GrantMode = EntityGrantModes.SelectedCompanies,
                Role = "Company User", IsActive = true,
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

    private static CompanySetupCaller Caller(World w) =>
        new(w.TenantId, w.AdminId, true, false, new RequestContext("127.0.0.1", "tests", w.AdminId, w.TenantId));

    private static CompanySetupDraftBody Body(string domain, CompanySetupCompanyAdmin admin, CompanySetupCompanyAdmin? reviewer, string cr = "1010000099") =>
        new(V.StartFromDefaults, null,
            new CompanySetupCompany("New Gulf Co", "شركة الخليج الجديدة", null, "SA", "SAR", cr, "7-1234567", null, "7001234567", null, null,
                domain, null, null, "2026-11"),
            [new CompanySetupBranch("JED", "Jeddah HQ", null, "Jeddah", "Asia/Riyadh", true,
                [new CompanySetupLocation($"JED-L{cr[^4..]}", "Jeddah site", null, 21.5m, 39.2m, 150m)])],
            [new CompanySetupCostCentre($"CC_OPS_{cr[^4..]}", "Operations cost centre", null)],
            [new CompanySetupDepartment($"OPS{cr[^4..]}", "Operations", null, null, "JED", $"CC_OPS_{cr[^4..]}")],
            [new CompanySetupDesignation($"OPS_OFF{cr[^4..]}", "Operations Officer", null, $"OPS{cr[^4..]}", "G1")],
            [new CompanySetupGrade("G1", "Grade 1", "الدرجة 1", 1, 3000, 4500, 6000)],
            new CompanySetupRules(new CompanySetupWorkWeek("Fri,Sat", false), new CompanySetupPackChoice(true), new CompanySetupPackChoice(true),
                new CompanySetupEmployeeNumbering("NGC", true, false, true, true, 4, 1)),
            admin, null, reviewer);

    private static CompanySetupCompanyAdmin Invite(string name, string email) => new(V.AdminInvite, null, name, null, email);

    private async Task<CompanySetupOutcome<CompanySetupResult>> CommitAsync(World w, CompanySetupDraftBody body)
    {
        await using var db = fixture.CreateDb();
        var created = await Service(db).CreateDraftAsync(Caller(w), body, default);
        created.Status.Should().Be(201, JsonSerializer.Serialize(created.Error));
        await using var commit = fixture.CreateDb();
        return await Service(commit).CommitAsync(Caller(w), new CompanySetupCommitRequest(created.Value!.Id, created.Value.Version), created.Value.Id, default);
    }

    private async Task<IReadOnlyList<ReadinessItem>> ReadinessAsync(World w, Guid companyId)
    {
        await using var db = fixture.CreateDb();
        return (await Service(db).ReadinessAsync(w.TenantId, companyId, default))!;
    }

    /// <summary>The person has chosen their own password (the first-sign-in step), which is all a direct login still needs here.</summary>
    private async Task SetOwnPasswordAsync(string email)
    {
        await using var db = fixture.CreateDb();
        var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.NormalizedEmail == AuthService.Normalize(email));
        user.MustChangePassword = false;
        await db.SaveChangesAsync();
    }

    private static string Item(IReadOnlyList<ReadinessItem> items, string key) => items.Single(i => i.Key == key).Status;

    [Fact]
    public async Task Admin_and_reviewer_leave_independent_review_todo_until_the_reviewer_is_usable()
    {
        var w = await SeedAsync();
        var outcome = await CommitAsync(w, Body("pair.sa", Invite("Sara Admin", "sara@pair.sa"), Invite("Omar Reviewer", "omar@pair.sa")));
        outcome.Status.Should().Be(201, JsonSerializer.Serialize(outcome.Error));
        var companyId = outcome.Value!.CompanyId;

        await using (var db = fixture.CreateDb())
        {
            var reviewer = await db.Users.IgnoreQueryFilters().AsNoTracking().Include(u => u.UserRoles).ThenInclude(r => r.Role)
                .Include(u => u.EntityAccesses).Include(u => u.EmployeeUserAccounts)
                .SingleAsync(u => u.TenantId == w.TenantId && u.NormalizedEmail == "OMAR@PAIR.SA");
            reviewer.UserRoles.Select(r => r.Role!.Name).Should().BeEquivalentTo(["Finance Approver"]);
            reviewer.IsGroupScope.Should().BeFalse();
            reviewer.EntityAccesses.Where(e => e.IsActive).Should().ContainSingle().Which.CompanyId.Should().Be(companyId);
            reviewer.EmployeeUserAccounts.Should().BeEmpty("a direct login needs no employee record");
        }

        // Nothing is usable yet: both logins still carry their temporary password.
        var first = outcome.Value.Readiness;
        Item(first, "independentPayReview").Should().Be(V.ReadinessTodo);
        var pending = first.Single(i => i.Key == "payReviewer");
        pending.Status.Should().Be(V.ReadinessTodo);
        pending.Label.Should().Contain("Omar Reviewer").And.Contain("password");
        pending.UserId.Should().NotBeNull("the screen can send their sign-in link");
        pending.Href.Should().Be($"/setup?tab=users&companyId={companyId}");

        // The administrator becomes usable: a pay entry operator exists, but still no independent reviewer.
        await SetOwnPasswordAsync("sara@pair.sa");
        Item(await ReadinessAsync(w, companyId), "independentPayReview").Should().Be(V.ReadinessTodo);

        // The reviewer sets their own password: no employee record is involved, and the check turns done.
        await SetOwnPasswordAsync("omar@pair.sa");
        var done = await ReadinessAsync(w, companyId);
        Item(done, "independentPayReview").Should().Be(V.ReadinessDone);
        Item(done, "payReviewer").Should().Be(V.ReadinessDone);
        done.Single(i => i.Key == "payReviewer").UserId.Should().BeNull();
    }

    [Fact]
    public async Task A_reviewer_linked_to_a_draft_employee_does_not_count()
    {
        var w = await SeedAsync();
        var outcome = await CommitAsync(w, Body("draft.sa", Invite("Sara Admin", "sara@draft.sa"), Invite("Omar Reviewer", "omar@draft.sa")));
        var companyId = outcome.Value!.CompanyId;
        await SetOwnPasswordAsync("sara@draft.sa");
        await SetOwnPasswordAsync("omar@draft.sa");
        Item(await ReadinessAsync(w, companyId), "independentPayReview").Should().Be(V.ReadinessDone);

        int employeeId;
        await using (var db = fixture.CreateDb())
        {
            var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.NormalizedEmail == "OMAR@DRAFT.SA");
            var employee = new Employee
            {
                TenantId = w.TenantId, CompanyId = companyId, EmployeeCode = "DR-0001", FullName = "Omar Reviewer", WorkEmail = "omar@draft.sa",
                Status = "Draft", JoiningDate = DateTime.UtcNow.Date,
            };
            db.Employees.Add(employee);
            await db.SaveChangesAsync();
            db.EmployeeUserAccounts.Add(new EmployeeUserAccount
            {
                TenantId = w.TenantId, EmployeeId = employee.Id, UserId = user.Id, AccessMode = AccessModes.FullPortal, Status = "Active",
                RequiresPasswordSetup = false, IsPrimary = true,
            });
            await db.SaveChangesAsync();
            employeeId = employee.Id;
        }
        var blocked = await ReadinessAsync(w, companyId);
        Item(blocked, "independentPayReview").Should().Be(V.ReadinessTodo);
        blocked.Single(i => i.Key == "payReviewer").Label.Should().Contain("employee record");

        await using (var db = fixture.CreateDb())
        {
            (await db.Employees.IgnoreQueryFilters().SingleAsync(e => e.Id == employeeId)).Status = "Active";
            await db.SaveChangesAsync();
        }
        Item(await ReadinessAsync(w, companyId), "independentPayReview").Should().Be(V.ReadinessDone);
    }

    [Fact]
    public async Task The_same_person_cannot_be_administrator_and_reviewer()
    {
        var w = await SeedAsync();
        await using var db = fixture.CreateDb();
        var invite = await Service(db).DryRunAsync(Caller(w),
            Body("same.sa", Invite("Sara Admin", "sara@same.sa"), Invite("Sara Again", "SARA@same.sa")), default);
        invite.Ok.Should().BeFalse();
        invite.Problems.Should().Contain(p => p.Code == "pay_reviewer_same_as_admin");

        var sisterAdmin = (await db.Roles.IgnoreQueryFilters().SingleAsync(r => r.TenantId == w.TenantId && r.NormalizedName == "ADMIN")).Id;
        var existing = await AddUserAsync(w.TenantId, "Sister Admin", sisterAdmin, groupScope: false, grantCompany: w.SisterId);
        var same = await Service(db).DryRunAsync(Caller(w), Body("same.sa",
            new CompanySetupCompanyAdmin(V.AdminExisting, existing, null, null, null),
            new CompanySetupCompanyAdmin(V.AdminExisting, existing, null, null, null)), default);
        same.Ok.Should().BeFalse();
        same.Problems.Should().Contain(p => p.Code == "pay_reviewer_same_as_admin");

        // An administrator cannot stand in as the independent reviewer either.
        var other = await Service(db).DryRunAsync(Caller(w), Body("same.sa", Invite("Sara Admin", "sara@same.sa"),
            new CompanySetupCompanyAdmin(V.AdminExisting, existing, null, null, null)), default);
        other.Problems.Should().Contain(p => p.Code == "pay_reviewer_is_administrator");
    }

    [Fact]
    public async Task A_reviewer_refused_by_the_user_limit_writes_its_own_failure_audit_and_readiness_says_so()
    {
        // Two seats: the group admin and the invited company super admin. The reviewer is the third.
        var w = await SeedAsync(maxUsers: 2);
        var outcome = await CommitAsync(w, Body("seats.sa", Invite("Sara Admin", "sara@seats.sa"), Invite("Omar Reviewer", "omar@seats.sa")));
        outcome.Status.Should().Be(201, "a reviewer failure never rolls the company back");

        await using var verify = fixture.CreateDb();
        (await verify.Users.IgnoreQueryFilters().AnyAsync(u => u.TenantId == w.TenantId && u.NormalizedEmail == "OMAR@SEATS.SA")).Should().BeFalse();
        (await verify.Users.IgnoreQueryFilters().AnyAsync(u => u.TenantId == w.TenantId && u.NormalizedEmail == "SARA@SEATS.SA")).Should().BeTrue();
        var failure = await verify.AuditLogs.SingleAsync(a => a.TenantId == w.TenantId && a.Action == CompanySetupService.ReviewerFailedAction);
        failure.CompanyId.Should().Be(outcome.Value!.CompanyId);
        JsonDocument.Parse(failure.Metadata!).RootElement.GetProperty("code").GetString().Should().Be(UserSeatLimit.Refusal.Code);
        (await verify.AuditLogs.AnyAsync(a => a.TenantId == w.TenantId && a.Action == CompanySetupService.AdminFailedAction)).Should().BeFalse();
        var item = outcome.Value.Readiness.Single(r => r.Key == "payReviewer");
        item.Status.Should().Be(V.ReadinessTodo);
        item.Label.Should().Contain("user limit");
    }

    [Fact]
    public async Task A_workspace_without_the_approver_role_fails_visibly_with_an_audit_row()
    {
        var w = await SeedAsync(withFinanceApprover: false);
        await using (var db = fixture.CreateDb())
        {
            var plan = await Service(db).DryRunAsync(Caller(w), Body("norole.sa", Invite("Sara Admin", "sara@norole.sa"), Invite("Omar Reviewer", "omar@norole.sa")), default);
            plan.Warnings.Should().Contain(p => p.Code == "pay_reviewer_role_missing");
        }
        var outcome = await CommitAsync(w, Body("norole.sa", Invite("Sara Admin", "sara@norole.sa"), Invite("Omar Reviewer", "omar@norole.sa")));
        outcome.Status.Should().Be(201);
        await using var verify = fixture.CreateDb();
        var failure = await verify.AuditLogs.SingleAsync(a => a.TenantId == w.TenantId && a.Action == CompanySetupService.ReviewerFailedAction);
        JsonDocument.Parse(failure.Metadata!).RootElement.GetProperty("code").GetString().Should().Be("pay_reviewer_role_missing");
        (await verify.Users.IgnoreQueryFilters().AnyAsync(u => u.TenantId == w.TenantId && u.NormalizedEmail == "OMAR@NOROLE.SA")).Should().BeFalse();
        outcome.Value!.Readiness.Single(r => r.Key == "payReviewer").Label.Should().Contain("Finance Approver");
    }

    [Fact]
    public async Task An_existing_approver_is_granted_this_company_without_changing_their_roles()
    {
        var w = await SeedAsync();
        Guid financeRole;
        await using (var db = fixture.CreateDb())
            financeRole = (await db.Roles.IgnoreQueryFilters().SingleAsync(r => r.TenantId == w.TenantId && r.NormalizedName == "FINANCE APPROVER")).Id;
        var existing = await AddUserAsync(w.TenantId, "Lina Finance", financeRole, groupScope: false, grantCompany: w.SisterId);
        var outcome = await CommitAsync(w, Body("exist.sa", Invite("Sara Admin", "sara@exist.sa"), new CompanySetupCompanyAdmin(V.AdminExisting, existing, null, null, null)));
        outcome.Status.Should().Be(201, JsonSerializer.Serialize(outcome.Error));
        await using var verify = fixture.CreateDb();
        var user = await verify.Users.IgnoreQueryFilters().AsNoTracking().Include(u => u.UserRoles).ThenInclude(r => r.Role).Include(u => u.EntityAccesses)
            .SingleAsync(u => u.Id == existing);
        user.UserRoles.Select(r => r.Role!.Name).Should().BeEquivalentTo(["Finance Approver"]);
        user.EntityAccesses.Where(e => e.IsActive).Select(e => e.CompanyId).Should().BeEquivalentTo(new Guid?[] { w.SisterId, outcome.Value!.CompanyId });
    }

    [Fact]
    public async Task Without_a_reviewer_the_plan_warns_and_readiness_asks_for_one()
    {
        var w = await SeedAsync();
        var outcome = await CommitAsync(w, Body("none.sa", Invite("Sara Admin", "sara@none.sa"), null));
        outcome.Status.Should().Be(201);
        var item = outcome.Value!.Readiness.Single(r => r.Key == "payReviewer");
        item.Status.Should().Be(V.ReadinessTodo);
        item.Label.Should().Contain("Add an independent pay reviewer");
        Item(outcome.Value.Readiness, "independentPayReview").Should().Be(V.ReadinessTodo);
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
