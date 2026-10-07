using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;
using Zayra.Api.Tests.Platform;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// P0 — editing an employee's work email must never take over their login.
///
/// <para>THE DEFECT. The work email is the login's username, and PUT /api/employees/{id} (open to Admin, HR Manager,
/// HR Officer and Payroll Officer) copied a new work email straight onto the linked login's Email/NormalizedEmail —
/// no ceiling, no session revoke, no notice. Forgot-password then mailed a reset link to the new address. A Payroll
/// Officer could point a colleague's (or an HR Director's) login at a mailbox they control and reset its password.</para>
///
/// <para>THE RULE. A STAGED login (never activated) follows the work email, and any invitation sent to the old
/// address is cancelled. An ACTIVATED login is never renamed by an employee edit: the employee keeps the new work
/// email, the login keeps its username, and the response says the two now differ. The login is found through the
/// live link as well as the legacy pointer.</para>
/// </summary>
public sealed class WorkEmailLoginTakeoverTests
{
    private const string OldEmail = "victim@acme.test";
    private const string NewEmail = "attacker@evil.test";

    public static TheoryData<string, bool> EditorRolesAndPointer()
    {
        var data = new TheoryData<string, bool>();
        foreach (var role in new[] { "Admin", "HR Manager", "HR Officer", "Payroll Officer" })
        {
            data.Add(role, true);
            data.Add(role, false);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(EditorRolesAndPointer))]
    public async Task AnActivatedLogin_KeepsItsUsername_WhenAnyEditorChangesTheWorkEmail(string role, bool legacyPointer)
    {
        await using var db = CreateDb();
        var world = await SeedAsync(db, staged: false, legacyPointer);

        var editor = Guid.NewGuid();
        var result = await Controller(db, world.TenantId, role, editor).UpdateEmployee(world.EmployeeId, WorkEmailEdit(NewEmail), default);

        var dto = Assert.IsType<EmployeeDetailDto>(Assert.IsType<OkObjectResult>(result).Value);
        dto.WorkEmail.Should().Be(NewEmail, "the employee record keeps what HR typed");
        dto.LoginUsernameDiffers.Should().BeTrue("the edit must say the login's username now differs from the work email");

        db.ChangeTracker.Clear();
        var login = await db.Users.IgnoreQueryFilters().SingleAsync(x => x.Id == world.UserId);
        login.Email.Should().Be(OldEmail, $"a {role} must not be able to repoint an activated login");
        login.NormalizedEmail.Should().Be(AuthService.Normalize(OldEmail));
        (await db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == world.EmployeeId)).WorkEmail.Should().Be(NewEmail);

        var actions = await db.AuditLogs.IgnoreQueryFilters().Where(x => x.EntityId == world.EmployeeId.ToString()).Select(x => x.Action).ToListAsync();
        actions.Should().Contain("employee.work_email_login_held");
        actions.Should().NotContain("employee.work_email_renamed");
        // Who changed the work email, and from what to what — the two-person rule for linking reads it.
        var change = await db.AuditLogs.IgnoreQueryFilters().SingleAsync(x => x.Action == AccessManagementService.WorkEmailChangedAction);
        change.UserId.Should().Be(editor);
        change.EntityId.Should().Be(world.EmployeeId.ToString());
        change.Metadata.Should().Contain(OldEmail).And.Contain(NewEmail);

        // The attacker's address reaches nothing; the owner's own address still does.
        await ForgotPasswordAsync(db, world, NewEmail);
        (await db.PasswordResetTokens.AsNoTracking().AnyAsync(x => x.UserId == world.UserId)).Should().BeFalse(
            "forgot-password for the new work email must not mint a reset link for the existing login");
        await ForgotPasswordAsync(db, world, OldEmail);
        (await db.PasswordResetTokens.AsNoTracking().CountAsync(x => x.UserId == world.UserId)).Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AStagedLogin_FollowsTheWorkEmail_AndItsOldInvitationIsCancelled(bool legacyPointer)
    {
        await using var db = CreateDb();
        var world = await SeedAsync(db, staged: true, legacyPointer);

        var result = await Controller(db, world.TenantId, "HR Officer").UpdateEmployee(world.EmployeeId, WorkEmailEdit(NewEmail), default);

        var dto = Assert.IsType<EmployeeDetailDto>(Assert.IsType<OkObjectResult>(result).Value);
        dto.WorkEmail.Should().Be(NewEmail);
        dto.LoginUsernameDiffers.Should().BeFalse("a staged login follows the work email");

        db.ChangeTracker.Clear();
        var login = await db.Users.IgnoreQueryFilters().SingleAsync(x => x.Id == world.UserId);
        login.Email.Should().Be(NewEmail);
        login.NormalizedEmail.Should().Be(AuthService.Normalize(NewEmail));
        var link = await db.EmployeeUserAccounts.IgnoreQueryFilters().SingleAsync(x => x.UserId == world.UserId);
        link.InvitationTokenHash.Should().BeEmpty("the invitation went to the old address and must not be redeemable for the new one");
        link.InvitationExpiresAtUtc.Should().BeNull();

        var renamed = await db.AuditLogs.IgnoreQueryFilters().SingleAsync(x => x.Action == "employee.work_email_renamed");
        renamed.Metadata.Should().Contain("\"invitationsCancelled\":1");
    }

    [Fact]
    public async Task AStagedLoginRename_ThatWouldCollideWithAnotherLogin_IsRefused()
    {
        await using var db = CreateDb();
        var world = await SeedAsync(db, staged: true, legacyPointer: true);
        db.Users.Add(new User
        {
            TenantId = world.TenantId, Email = NewEmail, NormalizedEmail = AuthService.Normalize(NewEmail),
            FullName = "Someone Else", PasswordHash = "x", Status = "Active", IsActive = true,
        });
        await db.SaveChangesAsync();

        var result = await Controller(db, world.TenantId, "HR Officer").UpdateEmployee(world.EmployeeId, WorkEmailEdit(NewEmail), default);

        Assert.IsType<UnprocessableEntityObjectResult>(result);
        db.ChangeTracker.Clear();
        (await db.Users.IgnoreQueryFilters().SingleAsync(x => x.Id == world.UserId)).Email.Should().Be(OldEmail);
    }

    /// <summary>
    /// THE REVIEWER'S SEQUENCE. A login that was activated (invitation accepted) but never signed in, then had its
    /// access revoked by offboarding — which puts it back in PendingPasswordSetup and clears its link — and even
    /// re-invited afterwards, so its link once again awaits a password. It was activated once: an employee edit
    /// must never repoint it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnActivatedLogin_RevokedByOffboarding_NeverLooksStaged(bool reinvitedAfterRevocation)
    {
        await using var db = CreateDb();
        var world = await SeedAsync(db, staged: false, legacyPointer: true);
        var acceptedAt = DateTime.UtcNow.AddDays(-3);
        var user = await db.Users.IgnoreQueryFilters().SingleAsync(x => x.Id == world.UserId);
        var link = await db.EmployeeUserAccounts.IgnoreQueryFilters().SingleAsync(x => x.UserId == world.UserId);
        user.LastLoginAtUtc = null; // activated, never signed in
        db.AuditLogs.Add(new AuditLog
        {
            TenantId = world.TenantId, UserId = user.Id, Action = "auth.invitation_accepted", EntityName = "User",
            EntityId = user.Id.ToString(), CreatedAtUtc = acceptedAt,
        });
        // OffboardingController.StageAccessRevocation, field for field.
        user.IsActive = false;
        user.IsEmailConfirmed = false;
        user.Status = "PendingPasswordSetup";
        user.AccessMode = AccessModes.NoLogin;
        user.PasswordHash = $"OFFBOARDED${user.Id:N}";
        link.AccessMode = AccessModes.NoLogin;
        link.Status = "NoLogin";
        link.RequiresPasswordSetup = false;
        link.InvitationTokenHash = string.Empty;
        link.InvitationExpiresAtUtc = null;
        link.InvitationAcceptedAtUtc = null;
        if (reinvitedAfterRevocation)
        {
            // InviteEmployeeLoginAsync on the same login: the link awaits a password again.
            user.Status = "Invited";
            link.Status = "Invited";
            link.RequiresPasswordSetup = true;
            link.InvitationTokenHash = "reissued-invitation-hash";
            link.InvitationExpiresAtUtc = DateTime.UtcNow.AddDays(3);
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await Controller(db, world.TenantId, "HR Officer").UpdateEmployee(world.EmployeeId, WorkEmailEdit(NewEmail), default);

        Assert.IsType<EmployeeDetailDto>(Assert.IsType<OkObjectResult>(result).Value).LoginUsernameDiffers.Should().BeTrue();
        db.ChangeTracker.Clear();
        (await db.Users.IgnoreQueryFilters().SingleAsync(x => x.Id == world.UserId)).Email.Should().Be(OldEmail,
            "a login that was ever activated never follows a work-email edit");
    }

    [Fact]
    public void Staged_RequiresALinkAwaitingItsFirstPassword_AndNoSignOfActivation()
    {
        static User Fresh() => new() { Status = "Invited", IsActive = false, IsEmailConfirmed = false };
        static EmployeeUserAccount Awaiting() => new() { RequiresPasswordSetup = true };
        WorkEmailLoginGuard.IsStaged(Fresh(), Awaiting(), hasActivationEvidence: false).Should().BeTrue();
        WorkEmailLoginGuard.IsStaged(new User { Status = "PendingPasswordSetup", IsActive = false, IsEmailConfirmed = false }, Awaiting(), false)
            .Should().BeTrue();

        WorkEmailLoginGuard.IsStaged(Fresh(), null, false).Should().BeFalse("no live link");
        WorkEmailLoginGuard.IsStaged(Fresh(), new EmployeeUserAccount { RequiresPasswordSetup = false }, false).Should().BeFalse("not awaiting a password");
        WorkEmailLoginGuard.IsStaged(Fresh(), new EmployeeUserAccount { RequiresPasswordSetup = true, InvitationAcceptedAtUtc = DateTime.UtcNow }, false).Should().BeFalse("accepted");
        WorkEmailLoginGuard.IsStaged(Fresh(), new EmployeeUserAccount { RequiresPasswordSetup = true, IsDeleted = true }, false).Should().BeFalse("deleted link");
        WorkEmailLoginGuard.IsStaged(Fresh(), Awaiting(), hasActivationEvidence: true).Should().BeFalse("was activated once");
        WorkEmailLoginGuard.IsStaged(new User { Status = "Invited", IsActive = false, IsEmailConfirmed = false, LastLoginAtUtc = DateTime.UtcNow }, Awaiting(), false).Should().BeFalse("signed in");
        WorkEmailLoginGuard.IsStaged(new User { Status = "Active", IsActive = true, IsEmailConfirmed = true }, Awaiting(), false).Should().BeFalse("active");
        WorkEmailLoginGuard.IsStaged(new User { Status = "Invited", IsActive = false, IsEmailConfirmed = true }, Awaiting(), false).Should().BeFalse("email confirmed");
        WorkEmailLoginGuard.IsStaged(new User { Status = "Suspended", IsActive = false, IsEmailConfirmed = false }, Awaiting(), false).Should().BeFalse("suspended");
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    private sealed record World(Guid TenantId, string Slug, int EmployeeId, Guid UserId);

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<World> SeedAsync(ZayraDbContext db, bool staged, bool legacyPointer)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Takeover Tenant", Slug = $"takeover-{Guid.NewGuid():N}", IsActive = true };
        db.Tenants.Add(tenant);
        var employee = new Employee
        {
            TenantId = tenant.Id, EmployeeCode = "EMP-VICTIM", FullName = "Hana Director", Status = "Active",
            JoiningDate = DateTime.UtcNow.Date.AddYears(-2), WorkEmail = OldEmail,
        };
        db.Employees.Add(employee);
        var user = new User
        {
            TenantId = tenant.Id, Email = OldEmail, NormalizedEmail = AuthService.Normalize(OldEmail), FullName = "Hana Director",
            PasswordHash = new Pbkdf2PasswordHasher().Hash("Original1!Password"),
            Status = staged ? "Invited" : "Active",
            AccessMode = staged ? AccessModes.NoLogin : AccessModes.EssOnly,
            IsActive = !staged,
            IsEmailConfirmed = !staged,
            LastLoginAtUtc = staged ? null : DateTime.UtcNow.AddDays(-1),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        db.EmployeeUserAccounts.Add(new EmployeeUserAccount
        {
            TenantId = tenant.Id, EmployeeId = employee.Id, UserId = user.Id, IsPrimary = true,
            AccessMode = AccessModes.EssOnly,
            Status = staged ? "Invited" : "Active",
            RequiresPasswordSetup = staged,
            InvitationTokenHash = staged ? "pending-invitation-hash" : string.Empty,
            InvitationExpiresAtUtc = staged ? DateTime.UtcNow.AddDays(3) : null,
        });
        if (legacyPointer) employee.UserAccountId = user.Id;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new World(tenant.Id, tenant.Slug, employee.Id, user.Id);
    }

    private static EmployeeUpdateRequest WorkEmailEdit(string email) =>
        new(DateOnly.FromDateTime(DateTime.UtcNow.Date), new() { ["workEmail"] = JsonSerializer.SerializeToElement(email) });

    private static EmployeesController Controller(ZayraDbContext db, Guid tenantId, string role) => Controller(db, tenantId, role, Guid.NewGuid());

    private static EmployeesController Controller(ZayraDbContext db, Guid tenantId, string role, Guid callerId)
    {
        var audit = new AuditService(db);
        return new EmployeesController(
            db, new Pbkdf2PasswordHasher(), audit, new NoStorage(), TestNotifications.For(db), new NoHijri(),
            new Zayra.Api.Infrastructure.Common.DataScopeService(db), new NoLetters(), new ApprovalWorkflowService(db, audit))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", tenantId.ToString()),
                        new Claim(ClaimTypes.NameIdentifier, callerId.ToString()),
                        new Claim(ClaimTypes.Role, role),
                        new Claim("permission", "employees.read"),
                        new Claim("permission", "employees.write"),
                    }, "Test")),
                },
            },
        };
    }

    private static async Task ForgotPasswordAsync(ZayraDbContext db, World world, string email)
    {
        var jwt = Options.Create(new JwtOptions
        {
            Issuer = "Zayra.Tests", TenantAudience = "kynexone-tenant-test", PlatformAudience = "kynexone-platform-test",
            SigningKey = "TEST_SIGNING_KEY_WITH_MORE_THAN_64_CHARACTERS_FOR_WORK_EMAIL_TAKEOVER", AccessTokenMinutes = 30, RefreshTokenDays = 7,
        });
        var auth = new AuthService(db, new Pbkdf2PasswordHasher(), new JwtTokenService(jwt), new AuditService(db), new FakeEmailService(), jwt,
            new NullMfaService(), new TotpService(DataProtectionProvider.Create("ZayraTests")), NullLogger<AuthService>.Instance);
        await auth.ForgotPasswordAsync(new ForgotPasswordRequest(email, world.Slug), new RequestContext("127.0.0.1", "tests"), default);
        db.ChangeTracker.Clear();
    }

    private sealed class NoStorage : IDocumentStorage
    {
        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) => throw new NotSupportedException();
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => throw new NotSupportedException();
        public string ResolvePath(string storageUrl) => throw new NotSupportedException();
    }

    private sealed class NoHijri : Zayra.Api.Infrastructure.Localization.IHijriDateService
    {
        public Zayra.Api.Infrastructure.Localization.DateConversionDto FromGregorian(DateOnly date) =>
            new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
    }

    private sealed class NoLetters : ILetterService
    {
        public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
