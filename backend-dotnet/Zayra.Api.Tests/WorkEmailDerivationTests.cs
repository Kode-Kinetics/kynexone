using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Organization;
using Zayra.Api.Controllers;
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
/// Auto-derive employee work email from the company domain. Covers: the pure deriver (patterns, accented
/// Latin, Arabic-only/mixed, suffixing, domain validate), company domain/pattern config + validation, the
/// single-create/edit service path (derive-when-blank, auto-suffix, user-supplied conflict, edge-7 no-domain
/// never-block, case-insensitive/login-normalized collision, foreign-domain coercion, login rename guard),
/// and the bulk importer (derive-when-blank, email:domain-mismatch flag, email:needs-info, preview==commit).
/// </summary>
public class WorkEmailDerivationTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Guid> SeedTenant(ZayraDbContext db)
    {
        var id = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = id, Name = "Zayra", Slug = $"z-{id:N}" });
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = id, MaxEmployees = 1000, Plan = "Enterprise", Status = "Active" });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<Company> SeedCompany(ZayraDbContext db, Guid tenantId, string name,
        string emailDomain = "", string pattern = WorkEmailPatterns.FirstLast)
    {
        var c = new Company
        {
            TenantId = tenantId, LegalNameEn = name, CountryCode = "SA", Jurisdiction = "test",
            RegistrationNumber = $"RC-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true,
            EmailDomain = emailDomain, WorkEmailPattern = pattern, CreatedAtUtc = DateTime.UtcNow,
        };
        db.Companies.Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    private static EmployeeManagementService Svc(ZayraDbContext db) =>
        new(db, new AuditService(db), new WeDocs(), new WeNotifications());

    private static RequestContext Ctx(Guid tenantId) => new(null, "test", Guid.NewGuid(), tenantId);

    private static EmployeeCreateRequest Req(string englishName, string? workEmail, Guid? companyId, string? arabicName = null) =>
        new(
            EmployeeCode: null, ManualEmployeeCode: false, EnglishName: englishName, ArabicName: arabicName,
            PreferredName: null, Gender: "Male", DateOfBirth: null, Nationality: "Saudi", MaritalStatus: null,
            PersonalEmail: null, WorkEmail: workEmail, MobileNumber: null, ProfilePhotoUrl: null,
            CompanyId: companyId, BranchId: null, DepartmentId: null, DesignationId: null, GradeId: null,
            CostCenterId: null, JobTitle: null, ReportingManagerEmployeeId: null, SecondLevelManagerEmployeeId: null,
            EmploymentType: "Full-time", ContractType: "Unlimited", JoiningDate: DateTime.UtcNow.Date,
            ConfirmationDate: null, ProbationStartDate: null, ProbationEndDate: null, NoticePeriodDays: null,
            WorkLocation: null, PayrollGroup: null, ShiftPolicyCode: null, LeavePolicyCode: null,
            AttendancePolicyCode: null, PayrollProfile: null, SalaryBreakdown: null, ComplianceRecords: null);

    private static EmployeesController ImportController(ZayraDbContext db, Guid tenantId) =>
        HrmHierarchyTests.BuildImportControllerInternal(db, tenantId);

    // ── Pure deriver: patterns ────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(WorkEmailPatterns.FirstLast, "john.smith")]
    [InlineData(WorkEmailPatterns.Flast, "jsmith")]
    [InlineData(WorkEmailPatterns.First, "john")]
    [InlineData(WorkEmailPatterns.FirstUnderscoreLast, "john_smith")]
    public void BuildLocalPart_AppliesEachPattern(string pattern, string expected) =>
        WorkEmailDeriver.BuildLocalPart("John Smith", null, pattern).Should().Be(expected);

    [Fact]
    public void BuildLocalPart_AccentedLatin_FoldsToAscii() =>
        WorkEmailDeriver.BuildLocalPart("José García", null, WorkEmailPatterns.FirstLast).Should().Be("jose.garcia");

    [Fact]
    public void BuildLocalPart_ArabicOnly_ReturnsEmpty_ForManualFallback() =>
        WorkEmailDeriver.BuildLocalPart("محمد علي", "محمد علي", WorkEmailPatterns.FirstLast).Should().BeEmpty();

    [Fact]
    public void BuildLocalPart_MixedArabicLatin_KeepsOnlyAsciiTokens()
    {
        // Q1: the Arabic token must not win "first" and silently drop the real name — only ASCII survives.
        WorkEmailDeriver.BuildLocalPart("محمد Ali", null, WorkEmailPatterns.FirstLast).Should().Be("ali");
    }

    [Fact]
    public void BuildLocalPart_SingleToken_DoesNotProduceTrailingSeparator() =>
        WorkEmailDeriver.BuildLocalPart("Cher", null, WorkEmailPatterns.FirstLast).Should().Be("cher");

    [Fact]
    public void Uniqueify_AutoSuffixesOnCollision()
    {
        var taken = new HashSet<string> { "john.smith@acme.sa" };
        WorkEmailDeriver.Uniqueify("john.smith", "acme.sa", a => taken.Contains(a)).Should().Be("john.smith2@acme.sa");
    }

    [Fact]
    public void ValidateAgainstDomain_DetectsMatchMismatchAndMissing()
    {
        WorkEmailDeriver.ValidateAgainstDomain("a@acme.sa", "acme.sa").Matches.Should().BeTrue();
        WorkEmailDeriver.ValidateAgainstDomain("a@evil.com", "acme.sa").Should().Be((false, "evil.com"));
        WorkEmailDeriver.ValidateAgainstDomain("noat", "acme.sa").Should().Be((false, (string?)null));
    }

    [Fact]
    public void Resolve_CoercesForeignDomain_AndThrowsOnUserSuppliedCollision()
    {
        var taken = new HashSet<string> { "taken@acme.sa" };
        bool IsTaken(string a) => taken.Contains(a);

        // Foreign domain coerced onto the company domain.
        var coerced = WorkEmailDeriver.Resolve("john@evil.com", "John Smith", null, "acme.sa", WorkEmailPatterns.FirstLast, IsTaken, out var outcome, out var from);
        coerced.Should().Be("john@acme.sa");
        outcome.Should().Be("coerced");
        from.Should().Be("john@evil.com");

        // User-supplied collision → deliberate stop.
        var act = () => WorkEmailDeriver.Resolve("taken@acme.sa", "X", null, "acme.sa", WorkEmailPatterns.FirstLast, IsTaken, out _, out _);
        act.Should().Throw<WorkEmailConflictException>().Which.Suggestion.Should().Be("taken2@acme.sa");
    }

    // ── Company config + validation ─────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("acme.sa", true)]
    [InlineData("mail.acme.co.uk", true)]
    [InlineData("", true)]                  // empty allowed (manual-entry fallback)
    [InlineData("not a domain", false)]
    [InlineData("acme", false)]             // no TLD
    [InlineData("@acme.sa", false)]
    public void EmailDomainValidation_AcceptsHostDomainsAndEmpty(string domain, bool valid) =>
        OrganizationSetupService.IsValidEmailDomainOrEmpty(domain).Should().Be(valid);

    [Fact]
    public async Task CreateCompany_PersistsLowercasedDomain_AndRejectsInvalid()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenant(db);
        var svc = new OrganizationSetupService(db, new AuditService(db));
        var ctx = Ctx(tenantId);

        var ok = await svc.CreateCompanyAsync(tenantId,
            new CompanyRequest("Acme", null, null, "SA", null, "REG-1", null, null, null, null, "SAR",
                EmailDomain: "ACME.SA", WorkEmailPattern: "flast"), ctx, CancellationToken.None);
        ok.EmailDomain.Should().Be("acme.sa");
        ok.WorkEmailPattern.Should().Be("flast");

        var bad = () => svc.CreateCompanyAsync(tenantId,
            new CompanyRequest("Bad", null, null, "SA", null, "REG-2", null, null, null, null, "SAR",
                EmailDomain: "not a domain"), ctx, CancellationToken.None);
        await bad.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void WorkEmailPatterns_Normalize_CoercesUnknownToDefault()
    {
        WorkEmailPatterns.Normalize("bogus").Should().Be(WorkEmailPatterns.FirstLast);
        WorkEmailPatterns.Normalize("FLAST").Should().Be(WorkEmailPatterns.Flast);
        WorkEmailPatterns.Normalize(null).Should().Be(WorkEmailPatterns.FirstLast);
    }

    // ── Single create: derive, suffix, edge-7, conflict, coercion, case-insensitive ─────────────
    [Fact]
    public async Task Create_DerivesWorkEmail_FromNameAndCompanyDomain()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenant(db);
        var acme = await SeedCompany(db, tenantId, "Acme", "acme.sa");

        var created = await Svc(db).CreateAsync(tenantId, Req("John Smith", null, acme.Id), Ctx(tenantId), CancellationToken.None);
        created.WorkEmail.Should().Be("john.smith@acme.sa");
    }

    [Fact]
    public async Task Create_AutoSuffixes_OnDerivedCollision()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenant(db);
        var acme = await SeedCompany(db, tenantId, "Acme", "acme.sa");
        var svc = Svc(db);

        (await svc.CreateAsync(tenantId, Req("John Smith", null, acme.Id), Ctx(tenantId), CancellationToken.None))
            .WorkEmail.Should().Be("john.smith@acme.sa");
        (await svc.CreateAsync(tenantId, Req("John Smith", null, acme.Id), Ctx(tenantId), CancellationToken.None))
            .WorkEmail.Should().Be("john.smith2@acme.sa");
    }

    [Fact]
    public async Task Create_UserSuppliedDuplicate_ThrowsWorkEmailConflict()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenant(db);
        var acme = await SeedCompany(db, tenantId, "Acme", "acme.sa");
        var svc = Svc(db);
        await svc.CreateAsync(tenantId, Req("John Smith", null, acme.Id), Ctx(tenantId), CancellationToken.None);

        // A DIFFERENT person who supplies the SAME local part must not silently duplicate the login identity.
        var act = () => svc.CreateAsync(tenantId, Req("Jane Doe", "john.smith", acme.Id), Ctx(tenantId), CancellationToken.None);
        (await act.Should().ThrowAsync<WorkEmailConflictException>()).Which.Suggestion.Should().Be("john.smith2@acme.sa");
    }

    [Fact]
    public async Task Create_CaseInsensitiveCollision_UsesLoginNormalization()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenant(db);
        var acme = await SeedCompany(db, tenantId, "Acme", "acme.sa");
        var svc = Svc(db);
        await svc.CreateAsync(tenantId, Req("John Smith", null, acme.Id), Ctx(tenantId), CancellationToken.None); // john.smith@acme.sa

        // "John.Smith" differs only by case; normalized it is the SAME login → must conflict, not create a twin.
        var act = () => svc.CreateAsync(tenantId, Req("Jane Doe", "John.Smith", acme.Id), Ctx(tenantId), CancellationToken.None);
        await act.Should().ThrowAsync<WorkEmailConflictException>();
    }

    [Fact]
    public async Task Create_ForeignDomain_IsCoercedToCompanyDomain()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenant(db);
        var acme = await SeedCompany(db, tenantId, "Acme", "acme.sa");

        var created = await Svc(db).CreateAsync(tenantId, Req("John Smith", "john.smith@evil.com", acme.Id), Ctx(tenantId), CancellationToken.None);
        created.WorkEmail.Should().Be("john.smith@acme.sa");
    }

    [Fact]
    public async Task Create_NoCompanyDomain_FallsBackToManual_NeverBlocks()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenant(db);
        var acme = await SeedCompany(db, tenantId, "Acme"); // no EmailDomain

        // Blank stays blank (needs-info surfaces via completeness); a manual value is kept verbatim — never blocked.
        (await Svc(db).CreateAsync(tenantId, Req("John Smith", null, acme.Id), Ctx(tenantId), CancellationToken.None))
            .WorkEmail.Should().BeEmpty();
        (await Svc(db).CreateAsync(tenantId, Req("Jane Doe", "jane@personal.com", acme.Id), Ctx(tenantId), CancellationToken.None))
            .WorkEmail.Should().Be("jane@personal.com");
    }

    [Fact]
    public async Task Create_ArabicOnlyName_WithDomain_LeavesBlankForManualEntry()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenant(db);
        var acme = await SeedCompany(db, tenantId, "Acme", "acme.sa");

        (await Svc(db).CreateAsync(tenantId, Req("محمد علي", null, acme.Id, arabicName: "محمد علي"), Ctx(tenantId), CancellationToken.None))
            .WorkEmail.Should().BeEmpty();
    }

    // ── Update: login-identity rename guard ─────────────────────────────────────────────────────
    [Fact]
    public async Task Update_RenamesLinkedLogin_KeepsUserInSync()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenant(db);
        var acme = await SeedCompany(db, tenantId, "Acme", "acme.sa");
        var svc = Svc(db);
        var created = await svc.CreateAsync(tenantId, Req("John Smith", null, acme.Id), Ctx(tenantId), CancellationToken.None);

        // Provision a linked login on the derived address.
        var user = new User { TenantId = tenantId, Email = "john.smith@acme.sa", NormalizedEmail = "JOHN.SMITH@ACME.SA", FullName = "John Smith", PasswordHash = "x" };
        db.Users.Add(user);
        var emp = await db.Employees.FirstAsync(e => e.Id == created.Id);
        emp.UserAccountId = user.Id;
        await db.SaveChangesAsync();

        await svc.UpdateAsync(tenantId, created.Id, Req("John Smithers", null, acme.Id), Ctx(tenantId), CancellationToken.None);

        (await db.Employees.FirstAsync(e => e.Id == created.Id)).WorkEmail.Should().Be("john.smithers@acme.sa");
        (await db.Users.FirstAsync(u => u.Id == user.Id)).NormalizedEmail.Should().Be("JOHN.SMITHERS@ACME.SA");
    }

    [Fact]
    public async Task Update_RenameCollidingWithAnotherLogin_IsBlocked()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenant(db);
        var acme = await SeedCompany(db, tenantId, "Acme", "acme.sa");
        var svc = Svc(db);
        var created = await svc.CreateAsync(tenantId, Req("John Smith", null, acme.Id), Ctx(tenantId), CancellationToken.None);

        var user = new User { TenantId = tenantId, Email = "john.smith@acme.sa", NormalizedEmail = "JOHN.SMITH@ACME.SA", FullName = "John Smith", PasswordHash = "x" };
        var other = new User { TenantId = tenantId, Email = "john.smithers@acme.sa", NormalizedEmail = "JOHN.SMITHERS@ACME.SA", FullName = "Other", PasswordHash = "x" };
        db.Users.AddRange(user, other);
        var emp = await db.Employees.FirstAsync(e => e.Id == created.Id);
        emp.UserAccountId = user.Id;
        await db.SaveChangesAsync();

        // Renaming to john.smithers@acme.sa would collide with `other`'s login → blocked (no desync).
        var act = () => svc.UpdateAsync(tenantId, created.Id, Req("John Smithers", null, acme.Id), Ctx(tenantId), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Bulk import ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Import_DerivesWorkEmail_WhenBlank_AndSuffixesCollisions()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenant(db);
        await SeedCompany(db, tenantId, "Acme", "acme.sa");
        var ctrl = ImportController(db, tenantId);

        var csv =
            "EmployeeCode,FullName,CompanyLegalName,JoiningDate\n" +
            "E1,John Smith,Acme,2024-01-01\n" +
            "E2,John Smith,Acme,2024-01-01\n";
        await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);

        (await db.Employees.SingleAsync(e => e.EmployeeCode == "E1")).WorkEmail.Should().Be("john.smith@acme.sa");
        (await db.Employees.SingleAsync(e => e.EmployeeCode == "E2")).WorkEmail.Should().Be("john.smith2@acme.sa");
    }

    [Fact]
    public async Task Import_ProvidedEmailDomainMismatch_IsFlagged_RowStillImports()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenant(db);
        await SeedCompany(db, tenantId, "Acme", "acme.sa");
        var ctrl = ImportController(db, tenantId);

        var csv =
            "EmployeeCode,FullName,CompanyLegalName,WorkEmail,JoiningDate\n" +
            "E1,John Smith,Acme,john@wrong.com,2024-01-01\n";
        await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);

        var e1 = await db.Employees.SingleAsync(e => e.EmployeeCode == "E1");
        e1.WorkEmail.Should().Be("john@wrong.com"); // provided value kept as-is (accept-never-block)
        (await db.EmployeeImportGaps.Where(g => g.EmployeeId == e1.Id).Select(g => g.GapType).ToListAsync())
            .Should().Contain("email:domain-mismatch");
    }

    [Fact]
    public async Task Import_NoCompanyDomain_ImportsWithoutEmailGap_NeverBlocks()
    {
        // Edge-7: a company with no email domain isn't using derivation — the row imports with a blank work
        // email (surfaced by the normal completeness signal) and NO per-row email gap is added (no flooding).
        await using var db = CreateDb();
        var tenantId = await SeedTenant(db);
        await SeedCompany(db, tenantId, "Acme"); // no EmailDomain
        var ctrl = ImportController(db, tenantId);

        var csv =
            "EmployeeCode,FullName,CompanyLegalName,JoiningDate\n" +
            "E1,John Smith,Acme,2024-01-01\n";
        await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);

        var e1 = await db.Employees.SingleAsync(e => e.EmployeeCode == "E1");
        e1.WorkEmail.Should().BeEmpty();
        (await db.EmployeeImportGaps.Where(g => g.EmployeeId == e1.Id).Select(g => g.GapType).ToListAsync())
            .Should().NotContain(t => t.StartsWith("email:"));
    }

    [Fact]
    public async Task Import_DomainPresentButArabicOnlyName_FlagsNeedsInfo()
    {
        // Derivation was expected (company has a domain) but the name has no romanizable form → email:needs-info.
        await using var db = CreateDb();
        var tenantId = await SeedTenant(db);
        await SeedCompany(db, tenantId, "Acme", "acme.sa");
        var ctrl = ImportController(db, tenantId);

        var csv =
            "EmployeeCode,FullName,CompanyLegalName,JoiningDate\n" +
            "E1,محمد علي,Acme,2024-01-01\n";
        await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);

        var e1 = await db.Employees.SingleAsync(e => e.EmployeeCode == "E1");
        e1.WorkEmail.Should().BeEmpty();
        (await db.EmployeeImportGaps.Where(g => g.EmployeeId == e1.Id).Select(g => g.GapType).ToListAsync())
            .Should().Contain("email:needs-info");
    }

    [Fact]
    public async Task Import_DerivedEmail_IsUniqueAgainstExistingDbRow()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenant(db);
        await SeedCompany(db, tenantId, "Acme", "acme.sa");
        // An existing employee already holds the base derived address.
        db.Employees.Add(new Employee
        {
            TenantId = tenantId, EmployeeCode = "OLD", FullName = "John Smith", EnglishName = "John Smith",
            WorkEmail = "john.smith@acme.sa", Status = "Active", JoiningDate = DateTime.UtcNow, Designation = string.Empty,
        });
        await db.SaveChangesAsync();
        var ctrl = ImportController(db, tenantId);

        var csv =
            "EmployeeCode,FullName,CompanyLegalName,JoiningDate\n" +
            "E1,John Smith,Acme,2024-01-01\n";
        await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);

        (await db.Employees.SingleAsync(e => e.EmployeeCode == "E1")).WorkEmail.Should().Be("john.smith2@acme.sa");
    }
}

file sealed class WeDocs : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) =>
        Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument("f", "t", "u", "p"));
    public string ResolvePath(string storageUrl) => "/tmp";
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class WeNotifications : INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
}
