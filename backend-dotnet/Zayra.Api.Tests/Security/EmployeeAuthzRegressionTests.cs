using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Localization;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Regression cover for three confirmed P0 employee-authorization defects.
///
/// <list type="number">
/// <item>GET /api/employees/ai/insights was a bare [Authorize] over the whole tenant roster — no role
/// gate, no data scope, no company boundary. Any authenticated principal, an ESS-only employee
/// included, could page colleagues out of it, and <c>?query=bank</c> enumerated who has no IBAN.</item>
/// <item>Create / Update / ChangeStatus returned <c>GetAsync(..., includeSensitive: true, ...)</c>
/// regardless of the caller, so a PATCH {id}/status was a working salary + IBAN read primitive for a
/// caller whose own GET {id} masks those fields — and it stamped a false
/// <c>employee.sensitive_viewed</c> audit row on every routine status flip.</item>
/// <item>DELETE /api/employees/{id} set Status = "Inactive" by hand and closed no credential edge, so
/// the person disappeared from every list while their self-service login still authenticated and
/// their live refresh tokens kept minting access tokens.</item>
/// </list>
/// </summary>
public sealed class EmployeeAuthzRegressionTests
{
    // ── Fixtures ─────────────────────────────────────────────────────────────────

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static EmployeeManagementService CreateService(ZayraDbContext db) =>
        new(db, new AuditService(db), new NullDocumentStorage(), TestNotifications.For(db));

    private static Employee SeedEmployee(
        ZayraDbContext db, Guid tenantId, string code, string name,
        int? managerEmployeeId = null, string status = "Active", Guid? userAccountId = null)
    {
        var employee = new Employee
        {
            TenantId            = tenantId,
            UserAccountId       = userAccountId,
            EmployeeCode        = code,
            FullName            = name,
            EnglishName         = name,
            Department          = "HR",
            Designation         = "Officer",
            Status              = status,
            ManagerEmployeeId   = managerEmployeeId,
            JoiningDate         = DateTime.UtcNow.Date.AddYears(-1),
            ProfileCompletenessScore = 10m,   // < 80 so the default insight branch matches everyone
            Salary              = 50_000m,
            BankName            = "Test Bank",
            BankIban            = "SA0000000000000000001234",
            WpsBankDetails      = "WPS-REF-001",
            PassportNumber      = "P99999999",
            IqamaNumber         = "2000000001",
        };
        db.Employees.Add(employee);
        db.SaveChanges();
        return employee;
    }

    /// <param name="permissions">Effective permission claims. Null gives an ESS-only principal.</param>
    private static EmployeesController CreateController(
        ZayraDbContext db, Guid tenantId, string role, string[]? permissions = null, int? callerEmployeeId = null)
    {
        var controller = new EmployeesController(
            db,
            new Pbkdf2PasswordHasher(),
            new AuditService(db),
            new NullDocumentStorage(),
            TestNotifications.For(db),
            new StubHijriDateService(),
            new DataScopeService(db),
            new StubLetterService());

        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, role),
        };
        if (callerEmployeeId is int empId) claims.Add(new Claim("employee_id", empId.ToString()));
        claims.AddRange((permissions ?? []).Select(p => new Claim("permission", p)));

        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        return controller;
    }

    private static EmployeeAiResponseDto AiInsights(EmployeesController controller, string query)
    {
        var result = controller.AiInsights(query, CancellationToken.None).GetAwaiter().GetResult();
        return Assert.IsType<EmployeeAiResponseDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    // ── FIX 1 — ai/insights carries the People-list authorization ─────────────────

    [Fact]
    public void AiInsights_CarriesTheExactRoleGate_OfThePeopleList()
    {
        // The role gate is enforced by the framework from the attribute, so the attribute IS the
        // behaviour here. It must be the People list's gate, not a weaker hand-picked one: ai/insights
        // returns the same EmployeeListItemDto projection over the same table.
        static string[] RolesOf(string action) =>
            typeof(EmployeesController).GetMethod(action, BindingFlags.Public | BindingFlags.Instance)!
                .GetCustomAttribute<AuthorizeAttribute>()?.Roles
                ?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                ?? [];

        var insightsRoles = RolesOf(nameof(EmployeesController.AiInsights));

        insightsRoles.Should().NotBeEmpty(
            "a bare [Authorize] let any authenticated principal — including an ESS-only employee — read the roster");
        insightsRoles.Should().BeEquivalentTo(RolesOf(nameof(EmployeesController.Search)),
            "ai/insights is the People list asked as a question and must carry the identical role gate");
    }

    [Fact]
    public async Task AiInsights_EssOnlyPrincipal_ReadsNoColleague()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        SeedEmployee(db, tenantId, "EMP-0001", "Alice Roster");
        SeedEmployee(db, tenantId, "EMP-0002", "Bob Roster");

        // No employees.read, no manager.read and no resolvable employee identity → Own scope with an
        // EMPTY allowed set. Defence in depth behind the role attribute: even if the gate were bypassed,
        // the scope filter must return nothing rather than the whole tenant.
        var ess = CreateController(db, tenantId, "Employee", permissions: []);

        AiInsights(ess, "profiles").Employees.Should().BeEmpty(
            "an ESS-only principal has no roster scope at all");
        AiInsights(ess, "bank").Employees.Should().BeEmpty(
            "?query=bank must not enumerate who has no IBAN on file");
        AiInsights(ess, "visa expiry").Employees.Should().BeEmpty();
    }

    [Fact]
    public async Task AiInsights_ManagerPrincipal_SeesOnlyTheirOwnReportingTree()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var manager = SeedEmployee(db, tenantId, "MGR-0001", "Manager One");
        var report = SeedEmployee(db, tenantId, "EMP-0002", "Direct Report", managerEmployeeId: manager.Id);
        var stranger = SeedEmployee(db, tenantId, "EMP-0003", "Other Department");

        var controller = CreateController(db, tenantId, "Manager",
            permissions: ["employees.read", "manager.read"], callerEmployeeId: manager.Id);

        var codes = AiInsights(controller, "profiles").Employees.Select(e => e.EmployeeCode).ToList();

        codes.Should().Contain(report.EmployeeCode, "an in-scope direct report is still returned");
        codes.Should().Contain(manager.EmployeeCode, "the caller's own record stays reachable");
        codes.Should().NotContain(stranger.EmployeeCode,
            "an employee outside the caller's data scope must never appear in an insight answer");
    }

    [Fact]
    public async Task AiInsights_HrRole_StillAnswersOverTheFullRoster()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        SeedEmployee(db, tenantId, "EMP-0001", "Alice Roster");
        SeedEmployee(db, tenantId, "EMP-0002", "Bob Roster");

        var hr = CreateController(db, tenantId, "HR Manager",
            permissions: ["employees.read", "employees.write", "employees.sensitive"]);

        AiInsights(hr, "profiles").Employees.Should().HaveCount(2,
            "the scope gate must not break the legitimate org-wide HR answer");
    }

    // ── FIX 2 — mutation responses obey the caller's own mask gate ────────────────

    [Fact]
    public async Task ChangeStatus_CallerWithoutSensitiveEntitlement_ReceivesMaskedBody()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = SeedEmployee(db, tenantId, "EMP-0001", "Masked Person");

        // HR Officer may PUT/PATCH (employees.write) but is NOT CanViewSensitive() — GET {id} masks
        // salary and IBAN for them, so PATCH {id}/status must too.
        var hrOfficer = CreateController(db, tenantId, "HR Officer", permissions: ["employees.read", "employees.write"]);

        var result = await hrOfficer.ChangeStatus(
            employee.Id,
            new EmployeeStatusChangeRequest(EmployeeStatuses.Suspended, DateOnly.FromDateTime(DateTime.UtcNow.Date), "Security hold"),
            CreateService(db), CancellationToken.None);

        var dto = Assert.IsType<EmployeeDetailDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        dto.Status.Should().Be(EmployeeStatuses.Suspended, "the status change itself still happened");
        dto.Salary.Should().BeNull("a status flip must not be a salary read primitive");
        dto.BankIban.Should().BeEmpty("a status flip must not be an IBAN read primitive");
        dto.WpsBankDetails.Should().BeEmpty();
        dto.PassportNumber.Should().BeEmpty();
        dto.IqamaNumber.Should().BeEmpty();
    }

    [Fact]
    public async Task ChangeStatus_CallerWithSensitiveEntitlement_StillReceivesTheFullRecord()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = SeedEmployee(db, tenantId, "EMP-0001", "Visible Person");

        var admin = CreateController(db, tenantId, "Admin",
            permissions: ["employees.read", "employees.write", "employees.sensitive"]);

        var result = await admin.ChangeStatus(
            employee.Id,
            new EmployeeStatusChangeRequest(EmployeeStatuses.Suspended, DateOnly.FromDateTime(DateTime.UtcNow.Date), "Security hold"),
            CreateService(db), CancellationToken.None);

        var dto = Assert.IsType<EmployeeDetailDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        dto.Salary.Should().Be(50_000m, "the fix must mask by entitlement, not blanket-mask every write response");
        dto.BankIban.Should().Be("SA0000000000000000001234");
    }

    [Fact]
    public async Task ChangeStatus_ByNonSensitiveCaller_WritesNoSensitiveViewedAuditRow()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = SeedEmployee(db, tenantId, "EMP-0001", "Audit Person");

        var hrOfficer = CreateController(db, tenantId, "HR Officer", permissions: ["employees.read", "employees.write"]);

        await hrOfficer.ChangeStatus(
            employee.Id,
            new EmployeeStatusChangeRequest(EmployeeStatuses.Suspended, DateOnly.FromDateTime(DateTime.UtcNow.Date), "Security hold"),
            CreateService(db), CancellationToken.None);

        db.ChangeTracker.Clear();
        (await db.AuditLogs.AsNoTracking().Where(x => x.Action == "employee.sensitive_viewed").ToListAsync())
            .Should().BeEmpty(
                "a routine status flip was being logged as a deliberate salary/bank view, poisoning the audit signal");
        (await db.AuditLogs.AsNoTracking().Where(x => x.Action == "employee.status_changed").AnyAsync())
            .Should().BeTrue("the transition itself is still audited");
    }

    // ── FIX 3 — a soft delete closes the employee's credential edges ──────────────

    [Fact]
    public async Task SoftDelete_InvalidatesLoginLinkUserAndLiveTokens()
    {
        await using var db = CreateDb();
        var tenant = new Tenant { Name = "Delete tenant", Slug = "delete-tenant", IsActive = true };
        var user = new User
        {
            TenantId        = tenant.Id,
            Email           = "leaver@delete.test",
            NormalizedEmail = "LEAVER@DELETE.TEST",
            FullName        = "Deleted Person",
            PasswordHash    = "unimportant",
            Status          = "Active",
            AccessMode      = AccessModes.EssOnly,
            IsActive        = true,
            IsEmailConfirmed = true,
        };
        db.AddRange(tenant, user);
        await db.SaveChangesAsync();

        var employee = SeedEmployee(db, tenant.Id, "EMP-0001", "Deleted Person", userAccountId: user.Id);
        var link = new EmployeeUserAccount
        {
            TenantId               = tenant.Id,
            EmployeeId             = employee.Id,
            UserId                 = user.Id,
            AccessMode             = AccessModes.EssOnly,
            Status                 = "Active",
            InvitationTokenHash    = "live-invitation",
            InvitationExpiresAtUtc = DateTime.UtcNow.AddDays(1),
        };
        var reset = new PasswordResetToken { UserId = user.Id, TokenHash = "live-reset", ExpiresAtUtc = DateTime.UtcNow.AddHours(1) };
        var challenge = new MfaChallengeToken { TenantId = tenant.Id, UserId = user.Id, TokenHash = "live-mfa", ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5) };
        var refresh = new RefreshToken { UserId = user.Id, TokenHash = "live-refresh", ExpiresAtUtc = DateTime.UtcNow.AddDays(7) };
        db.AddRange(link, reset, challenge, refresh);
        await db.SaveChangesAsync();
        var originalStamp = TenantSessionSecurity.StampValue(user);

        var controller = CreateController(db, tenant.Id, "Admin",
            permissions: ["employees.read", "employees.write", "employees.delete", "employees.sensitive"]);

        var deletion = await controller.Delete(employee.Id, CancellationToken.None);
        Assert.IsType<NoContentResult>(deletion);

        db.ChangeTracker.Clear();
        (await db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == employee.Id)).IsDeleted
            .Should().BeTrue("the delete itself still happened");

        var blockedLink = await db.EmployeeUserAccounts.SingleAsync(x => x.Id == link.Id);
        blockedLink.AccessMode.Should().Be(AccessModes.NoLogin, "the deleted employee's self-service login must not authenticate");
        blockedLink.Status.Should().Be("NoLogin");
        blockedLink.InvitationTokenHash.Should().BeEmpty("a live invitation would let them set a password after deletion");
        blockedLink.InvitationExpiresAtUtc.Should().BeNull();

        var blockedUser = await db.Users.SingleAsync(x => x.Id == user.Id);
        blockedUser.IsActive.Should().BeFalse();
        blockedUser.Status.Should().Be("Deactivated");
        blockedUser.AccessMode.Should().Be(AccessModes.NoLogin);
        TenantSessionSecurity.StampValue(blockedUser).Should().NotBe(originalStamp,
            "rotating the session stamp is what kills already-issued access tokens");

        (await db.RefreshTokens.SingleAsync(x => x.Id == refresh.Id)).RevokedAtUtc
            .Should().NotBeNull("a live refresh token kept minting access tokens after the delete");
        (await db.PasswordResetTokens.SingleAsync(x => x.Id == reset.Id)).UsedAtUtc.Should().NotBeNull();
        (await db.MfaChallengeTokens.SingleAsync(x => x.Id == challenge.Id)).UsedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task SoftDelete_RevocationAndDelete_CommitTogether()
    {
        // The revocation is staged into the SAME SaveChanges as the delete, so the two can never be
        // observed apart. Asserted by evidence: the employees.deleted audit row carries the exact
        // credential counts that were closed in that unit of work.
        await using var db = CreateDb();
        var tenant = new Tenant { Name = "Atomic tenant", Slug = "atomic-tenant", IsActive = true };
        var user = new User
        {
            TenantId = tenant.Id, Email = "atomic@delete.test", NormalizedEmail = "ATOMIC@DELETE.TEST",
            FullName = "Atomic Person", PasswordHash = "unimportant", Status = "Active",
            AccessMode = AccessModes.EssOnly, IsActive = true, IsEmailConfirmed = true,
        };
        db.AddRange(tenant, user);
        await db.SaveChangesAsync();

        var employee = SeedEmployee(db, tenant.Id, "EMP-0001", "Atomic Person", userAccountId: user.Id);
        db.AddRange(
            new EmployeeUserAccount { TenantId = tenant.Id, EmployeeId = employee.Id, UserId = user.Id, AccessMode = AccessModes.EssOnly, Status = "Active" },
            new RefreshToken { UserId = user.Id, TokenHash = "live-refresh", ExpiresAtUtc = DateTime.UtcNow.AddDays(7) });
        await db.SaveChangesAsync();

        var controller = CreateController(db, tenant.Id, "Admin",
            permissions: ["employees.read", "employees.write", "employees.delete"]);
        await controller.Delete(employee.Id, CancellationToken.None);

        db.ChangeTracker.Clear();
        var audit = await db.AuditLogs.AsNoTracking()
            .Where(x => x.Action == "employees.deleted" && x.EntityId == employee.Id.ToString())
            .SingleAsync();
        audit.Metadata.Should().NotBeNull();
        audit.Metadata!.Should().Contain("\"refreshTokens\":1",
            "the delete's own audit evidence must record the credential edges it closed");
        audit.Metadata!.Should().Contain("\"links\":1");
        audit.Metadata!.Should().Contain("\"users\":1");
    }

    // ── Local stubs (the suite's fakes are file/class-private per test file) ──────

    private sealed class StubHijriDateService : IHijriDateService
    {
        public DateConversionDto FromGregorian(DateOnly date) =>
            new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
    }

    private sealed class StubLetterService : ILetterService
    {
        public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }
}
