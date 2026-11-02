using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CompanySetup;
using Zayra.Api.Application.Organization;
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
/// The Guided Setup wizard removes existing organisation rows through the same rule as Setup (owner decision 2026-10-25):
/// never used = deleted, in use = turned off; a kept row may not still link to a removed one; a code is editable until
/// the row is in use. Database-free (in-memory) so it runs without Docker; the transaction itself is covered by the
/// Postgres wizard suites.
/// </summary>
public class CompanySetupRemovalTests
{
    private sealed record World(DbContextOptions<ZayraDbContext> Options, Guid TenantId, Guid AdminId, Guid CompanyId,
        Guid UnusedCc, Guid UsedCc, Guid UnusedDept, Guid UsedDept, Guid UnusedGrade, Guid UsedGrade);

    private static ZayraDbContext Open(DbContextOptions<ZayraDbContext> o) => new(o);

    private static async Task<World> SeedAsync()
    {
        var options = new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = Open(options);
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Group", Slug = $"g-{tenantId:N}", AccountType = TenantAccountTypes.Group,
            CompanyCreationMode = CompanyCreationModes.GroupSelfServiceWithinLimit });
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenantId, Plan = "Group", Status = SubscriptionStatuses.Active, MaxCompanies = 10, MaxUsers = 0, MaxAdminUsers = 0 });
        var adminRole = new Role { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Admin", NormalizedName = "ADMIN", Description = "Admin", IsSystem = true, IsActive = true, AuthorityLevel = 1 };
        db.Roles.Add(adminRole);
        db.GCCComplianceSettings.Add(new GCCComplianceSetting { TenantId = tenantId, CountryCode = "SA", EosbEnabled = true, WeekendDays = "Fri,Sat", WorkWeek = "Sun-Thu", WpsAgentId = "" });
        var adminId = Guid.NewGuid();
        db.Users.Add(new User { Id = adminId, TenantId = tenantId, Email = $"a.{adminId:N}@g.example", NormalizedEmail = $"A.{adminId:N}@G.EXAMPLE", FullName = "Admin",
            PasswordHash = "x", Status = "Active", AccessMode = AccessModes.FullPortal, IsActive = true, IsEmailConfirmed = true, IsGroupScope = true });
        db.UserRoles.Add(new UserRole { UserId = adminId, RoleId = adminRole.Id });
        var company = new Company { TenantId = tenantId, LegalNameEn = "Gulf Co", CountryCode = "SA", DefaultCurrency = "SAR", RegistrationNumber = "1010000077",
            IsActive = true, ApprovalStatus = CompanyApprovalStatuses.Active };
        db.Companies.Add(company);
        var hq = new Branch { TenantId = tenantId, CompanyId = company.Id, Code = "HQ", NameEn = "HQ", City = "Riyadh", CountryCode = "SA", TimeZoneId = "Asia/Riyadh", IsHeadOffice = true };
        db.Branches.Add(hq);
        var unusedCc = new CostCenter { TenantId = tenantId, CompanyId = company.Id, Code = "CC_OLD", Name = "Old" };
        var usedCc = new CostCenter { TenantId = tenantId, CompanyId = company.Id, Code = "CC_OPS", Name = "Ops" };
        var unusedDept = new Department { TenantId = tenantId, CompanyId = company.Id, Code = "OLD", NameEn = "Old dept", BranchId = hq.Id };
        var usedDept = new Department { TenantId = tenantId, CompanyId = company.Id, Code = "OPS", NameEn = "Operations", BranchId = hq.Id, CostCenterId = usedCc.Id };
        var unusedGrade = new Grade { TenantId = tenantId, CompanyId = company.Id, Code = "G9", Name = "Grade 9", Level = 9, MinSalary = 1, MidSalary = 2, MaxSalary = 3, Currency = "SAR" };
        var usedGrade = new Grade { TenantId = tenantId, CompanyId = company.Id, Code = "G1", Name = "Grade 1", Level = 1, MinSalary = 1, MidSalary = 2, MaxSalary = 3, Currency = "SAR" };
        // One designation with no links, so the prefill does not fill the section from the country pack (whose rows link to grades).
        db.AddRange(unusedCc, usedCc, unusedDept, usedDept, unusedGrade, usedGrade,
            new Designation { TenantId = tenantId, CompanyId = company.Id, Code = "DES1", TitleEn = "Officer" });
        db.Employees.Add(new Employee { TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "E1", DepartmentId = usedDept.Id, GradeId = usedGrade.Id, CostCenterId = usedCc.Id });
        await db.SaveChangesAsync();
        return new World(options, tenantId, adminId, company.Id, unusedCc.Id, usedCc.Id, unusedDept.Id, usedDept.Id, unusedGrade.Id, usedGrade.Id);
    }

    private static CompanySetupService Service(ZayraDbContext db)
    {
        var audit = new AuditService(db);
        return new CompanySetupService(db, new OrganizationSetupService(db, audit),
            new AccessManagementService(db, new Pbkdf2PasswordHasher(), audit, new FakeTokens()), new WorkWeekService(db), new NoEmail());
    }

    private static CompanySetupCaller Caller(World w) =>
        new(w.TenantId, w.AdminId, true, false, new RequestContext("127.0.0.1", "tests", w.AdminId, w.TenantId), null);

    private static async Task<(CompanySetupDraft Draft, CompanySetupService Svc, ZayraDbContext Db)> DraftAsync(World w, Func<CompanySetupDraftBody, CompanySetupDraftBody> edit)
    {
        var db = Open(w.Options);
        var svc = Service(db);
        var prefill = await svc.GetExistingDefaultsAsync(Caller(w), w.CompanyId, default);
        prefill.Status.Should().Be(200, JsonSerializer.Serialize(prefill.Error));
        var created = await svc.CreateDraftAsync(Caller(w), edit(prefill.Value!), default);
        created.Status.Should().Be(201, JsonSerializer.Serialize(created.Error));
        return (created.Value!, svc, db);
    }

    [Fact]
    public async Task Prefill_ShowsTurnedOffRows_AndTheDraftCarriesInUse()
    {
        var w = await SeedAsync();
        await using var db0 = Open(w.Options);
        (await new OrgRowRemoval(db0, null).RemoveAsync(OrgRowKind.Department, w.TenantId, w.UsedDept, new RequestContext(null, null, w.AdminId, w.TenantId), default))!
            .Outcome.Should().Be(OrgRowOutcomes.TurnedOff);

        var (draft, _, db) = await DraftAsync(w, b => b);
        await using var _ = db;

        draft.Body.Departments!.Select(d => d.Code).Should().Contain("OPS", "a turned-off row is still listed so it can be restored");
        var used = draft.RowStates![w.UsedDept];
        (used.IsActive, used.InUse, used.UsageSummary).Should().Be((false, true, "Used by 1 employees"));
        used.Usage.Should().ContainSingle().Which.Should().Be(new OrgRowUsageItem("employee", "employees", 1));
        draft.RowStates[w.UnusedDept].Should().Be(new CompanySetupRowState(true, false, null, null));
        draft.RowStates[w.UsedGrade].InUse.Should().BeTrue();
    }

    [Fact]
    public async Task SectionSave_DeletesUnusedRows_AndTurnsOffRowsInUse()
    {
        var w = await SeedAsync();
        var (draft, svc, db) = await DraftAsync(w, b => b with { Removals = new CompanySetupRemovals(Grades: [w.UnusedGrade, w.UsedGrade]) });
        await using var _ = db;

        var applied = await svc.ApplySectionAsync(Caller(w), draft.Id, V.Sections.Grades, new CompanySetupSectionApplyRequest(draft.Version), default);

        applied.Status.Should().Be(200, JsonSerializer.Serialize(applied.Error));
        applied.Value!.Removed.Should().Be(1);
        applied.Value.TurnedOff.Should().Be(1);
        (await db.Grades.IgnoreQueryFilters().SingleAsync(g => g.Id == w.UnusedGrade)).IsDeleted.Should().BeTrue();
        var used = await db.Grades.IgnoreQueryFilters().SingleAsync(g => g.Id == w.UsedGrade);
        (used.IsDeleted, used.IsActive).Should().Be((false, false));
        // The rebase: the deleted row left the draft, the turned-off row stays (shown Off), the removal list is spent.
        applied.Value.Draft.Body.Grades!.Select(g => g.Code).Should().BeEquivalentTo(new[] { "G1" });
        applied.Value.Draft.Body.Removals.Should().BeNull();
        applied.Value.Draft.RowStates![w.UsedGrade].IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task RemovingARow_WhileAKeptRowStillLinksToIt_IsRefusedOnTheLink()
    {
        var w = await SeedAsync();
        // OPS (kept) is linked to cost centre CC_OPS; removing CC_OPS must be refused on departments[..].costCentreCode.
        var (draft, svc, db) = await DraftAsync(w, b => b with { Removals = new CompanySetupRemovals(CostCentres: [w.UsedCc]) });
        await using var _ = db;

        var applied = await svc.ApplySectionAsync(Caller(w), draft.Id, V.Sections.CostCentres, new CompanySetupSectionApplyRequest(draft.Version), default);

        applied.Status.Should().BeOneOf(409, 422);
        JsonSerializer.Serialize(applied.Error).Should().Contain("link_to_removed_row").And.Contain("costCentreCode");
        (await db.CostCenters.IgnoreQueryFilters().SingleAsync(c => c.Id == w.UsedCc)).IsActive.Should().BeTrue("nothing was written");
    }

    [Fact]
    public async Task ARemovedIdThatIsNotThisCompanys_IsRefused()
    {
        var w = await SeedAsync();
        var (draft, svc, db) = await DraftAsync(w, b => b with { Removals = new CompanySetupRemovals(Grades: [Guid.NewGuid()]) });
        await using var _ = db;

        var applied = await svc.ApplySectionAsync(Caller(w), draft.Id, V.Sections.Grades, new CompanySetupSectionApplyRequest(draft.Version), default);

        applied.Status.Should().BeOneOf(409, 422);
        JsonSerializer.Serialize(applied.Error).Should().Contain("removal_unknown");
    }

    [Fact]
    public async Task ACodeChange_IsAllowedOnAnUnusedRow_AndRefusedOnARowInUse()
    {
        var w = await SeedAsync();
        var (draft, svc, db) = await DraftAsync(w, b => b with
        {
            Grades = b.Grades!.Select(g => g.ExistingId == w.UnusedGrade ? g with { Code = "G9X" } : g).ToList(),
        });
        await using var _ = db;
        var ok = await svc.ApplySectionAsync(Caller(w), draft.Id, V.Sections.Grades, new CompanySetupSectionApplyRequest(draft.Version), default);
        ok.Status.Should().Be(200, JsonSerializer.Serialize(ok.Error));
        (await db.Grades.IgnoreQueryFilters().SingleAsync(g => g.Id == w.UnusedGrade)).Code.Should().Be("G9X");

        var w2 = await SeedAsync();
        var (locked, svc2, db2) = await DraftAsync(w2, b => b with
        {
            Grades = b.Grades!.Select(g => g.ExistingId == w2.UsedGrade ? g with { Code = "G1X" } : g).ToList(),
        });
        await using var __ = db2;
        var refused = await svc2.ApplySectionAsync(Caller(w2), locked.Id, V.Sections.Grades, new CompanySetupSectionApplyRequest(locked.Version), default);
        refused.Status.Should().BeOneOf(409, 422);
        JsonSerializer.Serialize(refused.Error).Should().Contain("existing_code_locked").And.Contain("Used by 1 employees");
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
