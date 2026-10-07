using System.IdentityModel.Tokens.Jwt;
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
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;
using Zayra.Api.Tests.Platform;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Employee access onboarding (employee-access contract §1–4 and Amendments 1–3) over real Postgres: the login is
/// staged with the profile on every creation path, HR hands out an 8-digit welcome code, the employee redeems it and
/// sets their own password, and then signs in for real.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class EmployeeAccessTests
{
    private readonly PostgresFixture _fx;
    public EmployeeAccessTests(PostgresFixture fx) => _fx = fx;

    private const string StaffPassword = "StaffOnly1!Password";
    private const string NewPassword = "MyOwnPass1!word";

    private static readonly string[] Keys =
    [
        "security.manage", "employees.read", "employees.write", "employees.access.issue", "employees.access.reset",
        "profile.read", "ess.read", "ess.write", "loans.self", "manager.read", "manager.approve", "approvals.decide", "payroll.approve",
    ];

    private sealed record World(Guid TenantId, string Slug, string Domain, Guid CompanyId, Guid OtherCompanyId,
        Guid AdminId, Guid HrOfficerId, Guid HrOfficer2Id, Guid HrManagerId);

    // ── Provisioning ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_StagesALogin_ThatCannotSignIn_AndUsesNoSeat()
    {
        var w = await SeedAsync();
        var email = $"noah.williams@{w.Domain}";
        await using var db = _fx.CreateDb();
        var created = await Employees(db).CreateAsync(w.TenantId, Hire("Noah Williams", email, w.CompanyId), Ctx(w.HrOfficerId, w.TenantId), default);

        await using var verify = _fx.CreateDb();
        var link = await verify.EmployeeUserAccounts.IgnoreQueryFilters().SingleAsync(x => x.EmployeeId == created.Id);
        var user = await verify.Users.IgnoreQueryFilters().Include(u => u.UserRoles).ThenInclude(r => r.Role).Include(u => u.EntityAccesses)
            .SingleAsync(u => u.Id == link.UserId);
        user.Email.Should().Be(email);
        user.IsActive.Should().BeFalse();
        user.AccessMode.Should().Be(AccessModes.NoLogin);
        user.UserRoles.Select(r => r.Role!.NormalizedName).Should().BeEquivalentTo(["EMPLOYEE"]);
        user.EntityAccesses.Should().ContainSingle(g => g.CompanyId == w.CompanyId && !g.IsActive);
        link.RequiresPasswordSetup.Should().BeTrue();
        new Pbkdf2PasswordHasher().Verify("", user.PasswordHash).Should().BeFalse();
        (await verify.Employees.IgnoreQueryFilters().SingleAsync(e => e.Id == created.Id)).UserAccountId.Should().Be(user.Id);
        var actions = await AuditActionsAsync(w, "User", user.Id.ToString());
        actions.Should().Contain(EmployeeLoginProvisioner.StagedAction).And.NotContain("access.user_created");
        (await StateAsync(w, created.Id)).Should().Be(EmployeeAccessStates.NotStarted);
        (await EmployeeAccessService.CountSeatsAsync(verify, w.TenantId, DateTime.UtcNow, null, default)).Should().Be(4, "a staged login takes no seat");
    }

    [Fact]
    public async Task Create_WithBlankWorkEmail_SavesNothing_AndSuggestsTheDerivedAddress()
    {
        var w = await SeedAsync();
        await using var db = _fx.CreateDb();
        var created = await Employees(db).CreateAsync(w.TenantId, Hire("Sara Ali", null, w.CompanyId), Ctx(w.HrOfficerId, w.TenantId), default);

        created.WorkEmail.Should().BeEmpty("a derived address is a suggestion only");
        created.SuggestedWorkEmail.Should().Be($"sara.ali@{w.Domain}");
        await using var verify = _fx.CreateDb();
        (await verify.EmployeeUserAccounts.IgnoreQueryFilters().AnyAsync(x => x.EmployeeId == created.Id)).Should().BeFalse();
        (await StateAsync(w, created.Id)).Should().Be(EmployeeAccessStates.WaitingForWorkEmail);
    }

    [Fact]
    public async Task Create_RefusesAWrongDomain_AndAPlusAddress()
    {
        var w = await SeedAsync();
        await using var db = _fx.CreateDb();
        var refused = await Assert.ThrowsAsync<WorkEmailRejectedException>(() => Employees(db).CreateAsync(
            w.TenantId, Hire("Noah Hr", "noah@gmail.com", w.CompanyId), Ctx(w.HrOfficerId, w.TenantId), default));
        refused.Code.Should().Be(WorkEmailRejectedException.WrongDomainCode);
        refused.Message.Should().Be($"Work email must end in @{w.Domain}.");
        await Assert.ThrowsAsync<WorkEmailPlusAddressException>(() => Employees(db).CreateAsync(
            w.TenantId, Hire("Noah Hr", $"noah+hr@{w.Domain}", w.CompanyId), Ctx(w.HrOfficerId, w.TenantId), default));
        await using var verify = _fx.CreateDb();
        (await verify.Employees.IgnoreQueryFilters().AnyAsync(e => e.TenantId == w.TenantId && e.FullName == "Noah Hr")).Should().BeFalse();
    }

    [Fact]
    public async Task Controller_MapsARefusedWorkEmail_To422WithItsCode()
    {
        var w = await SeedAsync();
        await using var db = _fx.CreateDb();
        var result = await EmployeesCtl(db, w, w.HrOfficerId).CreateEmployee(Hire("Wrong Domain", "x@elsewhere.test", w.CompanyId), Employees(db), default);
        var body = JsonSerializer.SerializeToElement(Assert.IsType<UnprocessableEntityObjectResult>(result.Result).Value);
        body.GetProperty("code").GetString().Should().Be("work_email_wrong_domain");
        body.GetProperty("suggestedWorkEmail").GetString().Should().Be($"x@{w.Domain}");
    }

    [Fact]
    public async Task Provisioner_IsIdempotent_AndAWorkEmailAddedLaterStagesTheLogin()
    {
        var w = await SeedAsync();
        var id = await AddEmployeeAsync(w, workEmail: "");
        (await StateAsync(w, id)).Should().Be(EmployeeAccessStates.WaitingForWorkEmail);

        // Email added later (the shared edit rule both edit paths run), then the provisioner — twice.
        for (var run = 0; run < 2; run++)
        {
            await using var db = _fx.CreateDb();
            var e = await db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == id);
            var prior = e.WorkEmail;
            e.WorkEmail = $"later.{id}@{w.Domain}";
            await WorkEmailLoginGuard.ApplyAsync(db, e, w.TenantId, prior, Ctx(w.HrOfficerId, w.TenantId), DateTime.UtcNow, default);
            var outcome = await new EmployeeLoginProvisioner(db).EnsureStagedLoginAsync(w.TenantId, e, Ctx(w.HrOfficerId, w.TenantId), default);
            outcome.Result.Should().Be(run == 0 ? EmployeeLoginProvisioner.Results.Staged : EmployeeLoginProvisioner.Results.Existing);
            await db.SaveChangesAsync();
        }
        await using var verify = _fx.CreateDb();
        (await verify.Users.IgnoreQueryFilters().CountAsync(u => u.TenantId == w.TenantId && u.NormalizedEmail == AuthService.Normalize($"later.{id}@{w.Domain}")))
            .Should().Be(1);
        (await verify.EmployeeUserAccounts.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == id)).Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentProvisioning_OfOneEmployee_StagesExactlyOneLogin()
    {
        var w = await SeedAsync();
        var id = await AddEmployeeAsync(w, $"race@{w.Domain}");
        async Task RunAsync()
        {
            await using var db = _fx.CreateDb();
            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync();
                var e = await db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == id);
                await new EmployeeLoginProvisioner(db).EnsureStagedLoginAsync(w.TenantId, e, Ctx(w.HrOfficerId, w.TenantId), default);
                await Task.Delay(50);
                await db.SaveChangesAsync();
                await tx.CommitAsync();
            });
        }
        await Task.WhenAll(RunAsync(), RunAsync(), RunAsync());
        await using var verify = _fx.CreateDb();
        (await verify.EmployeeUserAccounts.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == id && !x.IsDeleted)).Should().Be(1);
        (await verify.Users.IgnoreQueryFilters().CountAsync(u => u.TenantId == w.TenantId && u.NormalizedEmail == AuthService.Normalize($"race@{w.Domain}"))).Should().Be(1);
    }

    [Fact]
    public async Task Import_StagesLoginsForRowsWithAWorkEmail_AndLeavesBlankOnesWaiting()
    {
        var w = await SeedAsync();
        await using var db = _fx.CreateDb();
        var companyName = await db.Companies.IgnoreQueryFilters().Where(c => c.Id == w.CompanyId).Select(c => c.LegalNameEn).SingleAsync();
        var csv = "EmployeeCode,FullName,JoiningDate,CompanyLegalName,WorkEmail\n" +
                  $"IMP-A,Imported Alpha,2024-01-15,{companyName},alpha@{w.Domain}\n" +
                  $"IMP-B,Imported Beta,2024-01-15,{companyName},\n" +
                  $"IMP-D,Imported Delta,2024-01-15,{companyName},delta@elsewhere.test\n";
        var result = await EmployeesCtl(db, w, w.AdminId).Import(new EmployeesController.ImportEmployeesRequest(csv), default);
        Assert.IsType<OkObjectResult>(result);

        await using var verify = _fx.CreateDb();
        var ids = await verify.Employees.IgnoreQueryFilters().Where(e => e.TenantId == w.TenantId && e.EmployeeCode.StartsWith("IMP-"))
            .ToDictionaryAsync(e => e.EmployeeCode, e => e);
        (await verify.EmployeeUserAccounts.IgnoreQueryFilters().AnyAsync(x => x.EmployeeId == ids["IMP-A"].Id)).Should().BeTrue();
        ids["IMP-B"].WorkEmail.Should().BeEmpty("import no longer derives an address");
        (await StateAsync(w, ids["IMP-A"].Id)).Should().Be(EmployeeAccessStates.NotStarted);
        (await StateAsync(w, ids["IMP-B"].Id)).Should().Be(EmployeeAccessStates.WaitingForWorkEmail);
        // Import keeps a wrong-domain address (flagged), but no login is ever staged on it.
        (await verify.EmployeeUserAccounts.IgnoreQueryFilters().AnyAsync(x => x.EmployeeId == ids["IMP-D"].Id)).Should().BeFalse();
        (await GetAsync(w, ids["IMP-D"].Id, w.HrOfficerId))!.BlockedCode.Should().Be(EmployeeLoginProvisioner.BlockedCodes.WrongDomain);
    }

    [Fact]
    public async Task AnEmailThatIsAlreadySomeonesLogin_IsBlocked_NeverAdopted()
    {
        var w = await SeedAsync();
        var email = $"taken@{w.Domain}";
        var existing = await AddUserAsync(w, email, ["Employee"], active: true);
        var id = await AddEmployeeAsync(w, email);
        await using (var db = _fx.CreateDb())
        {
            var e = await db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == id);
            var outcome = await new EmployeeLoginProvisioner(db).EnsureStagedLoginAsync(w.TenantId, e, Ctx(w.HrOfficerId, w.TenantId), default);
            outcome.BlockedCode.Should().Be(EmployeeLoginProvisioner.BlockedCodes.EmailBelongsToExistingLogin);
            await db.SaveChangesAsync();
        }
        var access = await GetAsync(w, id, w.HrOfficerId);
        access!.State.Should().Be(EmployeeAccessStates.Blocked);
        access.BlockedCode.Should().Be(EmployeeLoginProvisioner.BlockedCodes.EmailBelongsToExistingLogin);
        await using var verify = _fx.CreateDb();
        (await verify.EmployeeUserAccounts.IgnoreQueryFilters().AnyAsync(x => x.UserId == existing)).Should().BeFalse();

        var response = await IssueAsync(w, w.HrOfficerId, [id]);
        response.Issued.Should().BeEmpty();
        response.Skipped.Single().ReasonCode.Should().Be(EmployeeAccessService.Skip.Blocked);
        response.Skipped.Single().Reason.Should().Be(EmployeeAccessStates.BlockedReason(EmployeeLoginProvisioner.BlockedCodes.EmailBelongsToExistingLogin));
    }

    [Fact]
    public async Task AFormerEmployeesLogin_BlocksTheNewHire_WithItsOwnCode()
    {
        var w = await SeedAsync();
        var email = $"rehire@{w.Domain}";
        var leaver = await AddEmployeeAsync(w, email, status: "Terminated");
        var login = await AddUserAsync(w, email, ["Employee"], active: false);
        await AddLinkAsync(w, leaver, login);
        var hire = await AddEmployeeAsync(w, email);
        (await GetAsync(w, hire, w.HrOfficerId))!.BlockedCode.Should().Be(EmployeeLoginProvisioner.BlockedCodes.EmailBelongsToFormerEmployee);
    }

    [Fact]
    public async Task ACompanyWithoutADomain_LeavesTheEmployeeBlocked()
    {
        var w = await SeedAsync();
        var id = await AddEmployeeAsync(w, "someone@nodomain.test", companyId: w.OtherCompanyId);
        var access = await GetAsync(w, id, w.HrOfficerId);
        access!.State.Should().Be(EmployeeAccessStates.Blocked);
        access.BlockedCode.Should().Be(EmployeeLoginProvisioner.BlockedCodes.CompanyEmailDomainMissing);
        access.CanIssue.Should().BeFalse();
    }

    // ── States and the list ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task States_FollowTheJourney_AndTheListFiltersByThem()
    {
        var w = await SeedAsync();
        var waiting = await AddEmployeeAsync(w, "", name: "Zed Waiting");
        var notStarted = await AddStagedAsync(w, $"ns@{w.Domain}");
        var given = await AddStagedAsync(w, $"given@{w.Domain}");
        var active = await AddStagedAsync(w, $"active@{w.Domain}");
        var stopped = await AddStagedAsync(w, $"stopped@{w.Domain}");
        var blocked = await AddEmployeeAsync(w, "x@nodomain.test", companyId: w.OtherCompanyId);

        var codes = await IssueAsync(w, w.HrOfficerId, [given, active, stopped]);
        codes.Issued.Should().HaveCount(3);
        await RedeemAsync(w, $"active@{w.Domain}", codes.Issued.Single(i => i.EmployeeId == active).Code!);

        // Leaving: the status change stops access and kills the live code.
        await using (var db = _fx.CreateDb())
            await Employees(db).ChangeStatusAsync(w.TenantId, stopped, new EmployeeStatusChangeRequest("Terminated", DateOnly.FromDateTime(DateTime.UtcNow), "Left", "Resignation"), Ctx(w.AdminId, w.TenantId), default);

        (await StateAsync(w, waiting)).Should().Be(EmployeeAccessStates.WaitingForWorkEmail);
        (await StateAsync(w, notStarted)).Should().Be(EmployeeAccessStates.NotStarted);
        (await StateAsync(w, given)).Should().Be(EmployeeAccessStates.CodeGiven);
        (await StateAsync(w, active)).Should().Be(EmployeeAccessStates.Active);
        (await StateAsync(w, stopped)).Should().Be(EmployeeAccessStates.Stopped);
        (await StateAsync(w, blocked)).Should().Be(EmployeeAccessStates.Blocked);
        await using (var db = _fx.CreateDb())
            (await db.EmployeeUserAccounts.IgnoreQueryFilters().SingleAsync(x => x.EmployeeId == stopped)).WelcomeCodeHash.Should().BeNull("access stopped");

        var givenDto = await GetAsync(w, given, w.HrOfficerId);
        givenDto!.CodeExpiresAtUtc.Should().NotBeNull();
        givenDto.CodeIssuedByName.Should().Be("HR Officer One");

        await using var list = _fx.CreateDb();
        var page = await Employees(list).SearchAsync(w.TenantId, null, null, null, null, null, null, "code_given,waiting_for_work_email", 1, 50, default);
        page.Items.Select(i => i.Id).Should().BeEquivalentTo([given, waiting]);
        page.Items.Single(i => i.Id == given).AccessState.Should().Be(EmployeeAccessStates.CodeGiven);
        page.Items.Single(i => i.Id == given).WorkEmail.Should().Be($"given@{w.Domain}");
        var all = await Employees(list).SearchAsync(w.TenantId, null, null, null, null, null, null, null, 1, 50, default);
        all.Items.Single(i => i.Id == active).AccessState.Should().Be(EmployeeAccessStates.Active);
    }

    // ── Issuing codes ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Issue_Single_StoresOnlyAKeyedHash_AuditsIssueAndDisclosure_AndBulkSkipsTheIneligible()
    {
        var w = await SeedAsync();
        var ok = await AddStagedAsync(w, $"slip@{w.Domain}");
        var waiting = await AddEmployeeAsync(w, "");
        var pending = await AddEmployeeAsync(w, $"lazy@{w.Domain}"); // no login yet: staged on issue

        var response = await IssueAsync(w, w.HrOfficerId, [ok, waiting, pending]);

        response.Emailed.Should().BeFalse();
        response.DeliveryMessage.Should().Be("No email delivery is configured, so print the sign-in slips.");
        response.Issued.Select(i => i.EmployeeId).Should().BeEquivalentTo([ok, pending]);
        var item = response.Issued.Single(i => i.EmployeeId == ok);
        item.Code.Should().MatchRegex("^[0-9]{8}$");
        item.Username.Should().Be($"slip@{w.Domain}");
        item.TenantSlug.Should().Be(w.Slug);
        item.ExpiresAtUtc.Should().BeCloseTo(DateTime.UtcNow.AddDays(7), TimeSpan.FromMinutes(2));
        response.Skipped.Should().ContainSingle(s => s.EmployeeId == waiting && s.ReasonCode == EmployeeAccessStates.WaitingForWorkEmail && s.Reason == "No work email yet.");

        await using var db = _fx.CreateDb();
        var link = await db.EmployeeUserAccounts.IgnoreQueryFilters().SingleAsync(x => x.EmployeeId == ok);
        link.WelcomeCodeHash.Should().NotContain(item.Code!).And.HaveLength(64);
        link.WelcomeCodeIssuedBy.Should().Be(w.HrOfficerId);
        (await AuditActionsAsync(w, "EmployeeUserAccount", link.Id.ToString())).Should().Contain(EmployeeAccessService.IssuedAction);
        var disclosed = await db.AuditLogs.IgnoreQueryFilters().SingleAsync(a => a.Action == EmployeeAccessService.DisclosedAction && a.EntityId == link.UserId.ToString());
        disclosed.EntityName.Should().Be("User");
        disclosed.UserId.Should().Be(w.HrOfficerId, "the issuer is now a credential handler for this login");
        AccessManagementService.CredentialHandlerActions.Should().Contain(EmployeeAccessService.DisclosedAction);
    }

    [Fact]
    public async Task Issue_WithEmailDelivery_SendsTheCode_AndOmitsItFromTheResponse()
    {
        var w = await SeedAsync();
        var id = await AddStagedAsync(w, $"mailed@{w.Domain}");
        var email = new RecordingEmail(configured: true);
        var response = await IssueAsync(w, w.HrOfficerId, [id], email: email);
        response.Emailed.Should().BeTrue();
        response.Issued.Single().Code.Should().BeNull();
        JsonSerializer.Serialize(response.Issued.Single(), new JsonSerializerOptions(JsonSerializerDefaults.Web)).Should().NotContain("\"code\"");
        email.Sent.Should().ContainSingle(m => m.To == $"mailed@{w.Domain}");
        await using var db = _fx.CreateDb();
        (await db.AuditLogs.IgnoreQueryFilters().AnyAsync(a => a.TenantId == w.TenantId && a.Action == EmployeeAccessService.DisclosedAction))
            .Should().BeFalse("nobody saw the code");
    }

    [Fact]
    public async Task Issue_WithDeliveryPrint_NeverEmails_EvenWhenEmailIsConfigured()
    {
        var w = await SeedAsync();
        var id = await AddStagedAsync(w, $"printed@{w.Domain}");
        var email = new RecordingEmail(configured: true);
        var response = await IssueAsync(w, w.HrOfficerId, [id], email: email, delivery: "print");
        response.Emailed.Should().BeFalse();
        response.Issued.Single().Code.Should().MatchRegex("^[0-9]{8}$");
        email.Sent.Should().BeEmpty();
        await using var db = _fx.CreateDb();
        (await db.AuditLogs.IgnoreQueryFilters().SingleAsync(a => a.TenantId == w.TenantId && a.Action == EmployeeAccessService.DisclosedAction))
            .UserId.Should().Be(w.HrOfficerId, "printing makes the issuer a credential handler");
    }

    [Fact]
    public async Task ForgotPassword_SaysWhetherTheWorkspaceCanEmail_TheSameForAnyAddress()
    {
        var w = await SeedAsync();
        var staff = await EmailOfAsync(w.HrOfficerId);
        await using var db = _fx.CreateDb();
        var known = await Auth(db).ForgotPasswordAsync(new ForgotPasswordRequest(staff, w.Slug), new RequestContext("1.1.1.1", "x"), default);
        var unknown = await Auth(db).ForgotPasswordAsync(new ForgotPasswordRequest($"ghost@{w.Domain}", w.Slug), new RequestContext("1.1.1.1", "x"), default);
        known.EmailDeliveryConfigured.Should().BeFalse();
        unknown.Should().BeEquivalentTo(known with { }, "never reveal whether the address exists");
        var withMail = new AuthService(db, new Pbkdf2PasswordHasher(), new JwtTokenService(Jwt), new AuditService(db), new RecordingEmail(configured: true), Jwt,
            new NullMfaService(), new TotpService(DataProtectionProvider.Create("ZayraTests")), NullLogger<AuthService>.Instance);
        (await withMail.ForgotPasswordAsync(new ForgotPasswordRequest($"ghost@{w.Domain}", w.Slug), new RequestContext("1.1.1.1", "x"), default))
            .EmailDeliveryConfigured.Should().BeTrue();
        (await withMail.ForgotPasswordAsync(new ForgotPasswordRequest(staff, "no-such-workspace"), new RequestContext("1.1.1.1", "x"), default))
            .EmailDeliveryConfigured.Should().BeNull();
    }

    [Fact]
    public async Task Reissue_SupersedesTheOldCode()
    {
        var w = await SeedAsync();
        var email = $"twice@{w.Domain}";
        var id = await AddStagedAsync(w, email);
        var first = (await IssueAsync(w, w.HrOfficerId, [id])).Issued.Single().Code!;
        var second = (await IssueAsync(w, w.HrOfficerId, [id])).Issued.Single().Code!;
        (await RedeemRefusedAsync(w, email, first)).Code.Should().Be(WelcomeCodeRedeemer.Codes.Invalid);
        await RedeemAsync(w, email, second);
    }

    [Fact]
    public async Task Issue_RefusesSelf_AboveCeiling_PrivilegedInBulk_TheWorkEmailSetter_AndResetWithoutPermission()
    {
        var w = await SeedAsync();
        // Self: the HR Officer's own employee record is linked to their own login.
        var self = await AddEmployeeAsync(w, await EmailOfAsync(w.HrOfficerId));
        await AddLinkAsync(w, self, w.HrOfficerId);
        // Above the ceiling: an employee whose login holds payroll.approve (HR Officer doesn't).
        var above = await AddEmployeeAsync(w, $"payroll.boss@{w.Domain}");
        await AddLinkAsync(w, above, await AddUserAsync(w, $"payroll.boss@{w.Domain}", ["Payroll Approver"], active: false));
        // Privileged but within the ceiling: a line manager (named as someone's manager).
        var manager = await AddStagedAsync(w, $"line.manager@{w.Domain}");
        var report = await AddStagedAsync(w, $"report@{w.Domain}");
        await UpdateEmployeeAsync(report, e => e.ManagerEmployeeId = manager);
        // The caller set this person's work email.
        var setBySelf = await AddStagedAsync(w, $"setbyme@{w.Domain}");
        await AddAuditAsync(w, AccessManagementService.WorkEmailChangedAction, "Employee", setBySelf.ToString(), w.HrOfficerId);
        // Already active.
        var active = await AddStagedAsync(w, $"already@{w.Domain}");
        await RedeemAsync(w, $"already@{w.Domain}", (await IssueAsync(w, w.HrOfficer2Id, [active])).Issued.Single().Code!);

        var bulk = await IssueAsync(w, w.HrOfficerId, [above, manager, report, setBySelf]);
        bulk.Issued.Select(i => i.EmployeeId).Should().BeEquivalentTo([report]);
        Reason(await IssueAsync(w, w.HrOfficerId, [self], canReset: true), self).Should().Be(EmployeeAccessService.Skip.SelfIssue);
        Reason(bulk, above).Should().Be(EmployeeAccessService.Skip.AboveCeiling);
        Reason(bulk, manager).Should().Be(EmployeeAccessService.Skip.Privileged);
        Reason(bulk, setBySelf).Should().Be(WorkEmailSetterRule.SetByCallerCode);
        bulk.Skipped.Single(s => s.EmployeeId == setBySelf).Reason.Should().Be("You set this person's work email, so another HR colleague must give access.");

        // An active login: never in bulk, and never without employees.access.reset.
        var refused = await Assert.ThrowsAsync<EmployeeAccessRequestException>(() => IssueAsync(w, w.HrManagerId, [active, report], canReset: true));
        refused.Code.Should().Be("reset_is_single");
        Reason(await IssueAsync(w, w.HrOfficerId, [active]), active).Should().Be(EmployeeAccessService.Skip.ResetNeedsPermission);
        // The privileged line manager: one at a time, by a reset holder.
        (await IssueAsync(w, w.HrManagerId, [manager], canReset: true)).Issued.Should().ContainSingle();
    }

    [Fact]
    public async Task ResetSignIn_KeepsTheOldPasswordUntilRedeem_ThenRotatesPasswordSessionsAndMfa()
    {
        var w = await SeedAsync();
        var email = $"reset.me@{w.Domain}";
        var id = await AddStagedAsync(w, email);
        await RedeemAsync(w, email, (await IssueAsync(w, w.HrOfficerId, [id])).Issued.Single().Code!);
        var login = (await SignInAsync(w.Slug, email, NewPassword)).Tokens!;
        Guid userId;
        await using (var db = _fx.CreateDb())
        {
            var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.NormalizedEmail == AuthService.Normalize(email) && u.TenantId == w.TenantId);
            userId = user.Id;
            user.MFAEnabled = true;
            user.MfaSecretEncrypted = "protected-secret";
            user.MfaConfiguredAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        var code = (await IssueAsync(w, w.HrManagerId, [id], canReset: true)).Issued.Single().Code!;
        // Until redeemed, the person keeps working: password, MFA and session are untouched, and they are told.
        await using (var db = _fx.CreateDb())
        {
            var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId);
            new Pbkdf2PasswordHasher().Verify(NewPassword, user.PasswordHash).Should().BeTrue();
            user.MFAEnabled.Should().BeTrue();
            (await db.RefreshTokens.AnyAsync(r => r.UserId == userId && r.RevokedAtUtc == null)).Should().BeTrue();
        }
        await using (var db = _fx.CreateDb())
            (await Auth(db).GetCurrentUserAsync(userId, default))!.PendingResetNotice.Should().NotBeNull();
        (await StateAsync(w, id)).Should().Be(EmployeeAccessStates.CodeGiven);

        await RedeemAsync(w, email, code, password: "Rotated2!Password");
        await using (var db = _fx.CreateDb())
        {
            var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId);
            user.MFAEnabled.Should().BeFalse();
            user.MfaSecretEncrypted.Should().BeNull();
            (await db.RefreshTokens.AnyAsync(r => r.UserId == userId && r.RevokedAtUtc == null)).Should().BeFalse("every session is revoked");
            (await Auth(db).GetCurrentUserAsync(userId, default))!.PendingResetNotice.Should().BeNull();
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SignInAsync(w.Slug, email, NewPassword));
        (await SignInAsync(w.Slug, email, "Rotated2!Password")).Tokens.Should().NotBeNull();
        _ = login;
    }

    // ── Redeeming ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Redeem_ActivatesTheLogin_NeverIssuesASession_AndTheTokenCarriesEmployeeId()
    {
        var w = await SeedAsync();
        var email = $"first.time@{w.Domain}";
        var id = await AddStagedAsync(w, email);
        var code = (await IssueAsync(w, w.HrOfficerId, [id])).Issued.Single().Code!;
        // Arabic-Indic digits, spaces and a dash are all accepted.
        var typed = ToArabicIndic(code[..4]) + " - " + code[4..];

        await using (var db = _fx.CreateDb())
        {
            var response = await Auth(db).RedeemWelcomeCodeAsync(new WelcomeRedeemRequest(email, typed, NewPassword),
                new RequestContext("203.0.113.9", "phone"), NoPresenter, default);
            response.TenantSlug.Should().Be(w.Slug);
        }
        await using (var db = _fx.CreateDb())
        {
            var link = await db.EmployeeUserAccounts.IgnoreQueryFilters().SingleAsync(x => x.EmployeeId == id);
            (await db.RefreshTokens.AnyAsync(r => r.UserId == link.UserId)).Should().BeFalse("redeeming never issues a session");
            link.AccessMode.Should().Be(AccessModes.EssOnly);
            link.InvitationAcceptedAtUtc.Should().NotBeNull();
            link.WelcomeCodeRedeemedAtUtc.Should().NotBeNull();
            (await db.UserEntityAccesses.IgnoreQueryFilters().SingleAsync(g => g.UserId == link.UserId)).IsActive.Should().BeTrue("the staged company grant is switched on");
            (await AuditActionsAsync(w, "User", link.UserId.ToString())).Should().Contain(WelcomeCodeRedeemer.RedeemedAction);
        }
        var signedIn = await SignInAsync(null, email, NewPassword); // no workspace: routed by the unique domain
        var token = new JwtSecurityTokenHandler().ReadJwtToken(signedIn.Tokens!.AccessToken);
        token.Claims.Single(c => c.Type == "employee_id").Value.Should().Be(id.ToString());
        (await StateAsync(w, id)).Should().Be(EmployeeAccessStates.Active);

        // Used: only someone holding the code learns that.
        (await RedeemRefusedAsync(w, email, code)).Code.Should().Be(WelcomeCodeRedeemer.Codes.Used);
        (await RedeemRefusedAsync(w, email, Wrong(code))).Code.Should().Be(WelcomeCodeRedeemer.Codes.Invalid);
    }

    [Fact]
    public async Task Redeem_WrongCodeAndUnknownEmail_AreTheSameGenericAnswer()
    {
        var w = await SeedAsync();
        var email = $"generic@{w.Domain}";
        var id = await AddStagedAsync(w, email);
        var code = (await IssueAsync(w, w.HrOfficerId, [id])).Issued.Single().Code!;
        (await RedeemRefusedAsync(w, email, Wrong(code))).Code.Should().Be(WelcomeCodeRedeemer.Codes.Invalid);
        (await RedeemRefusedAsync(w, $"nobody@{w.Domain}", code)).Code.Should().Be(WelcomeCodeRedeemer.Codes.Invalid);
        (await RedeemRefusedAsync(w, email, "123")).Code.Should().Be(WelcomeCodeRedeemer.Codes.Invalid);
    }

    [Fact]
    public async Task Redeem_FiveMissesLockTheCode_TwentyBurnIt_AndTheBurnAlertsHr()
    {
        var w = await SeedAsync();
        var email = $"guessed@{w.Domain}";
        var id = await AddStagedAsync(w, email);
        var code = (await IssueAsync(w, w.HrOfficerId, [id])).Issued.Single().Code!;
        for (var i = 0; i < WelcomeCodes.FirstLockAt; i++)
            (await RedeemRefusedAsync(w, email, Wrong(code))).Code.Should().Be(WelcomeCodeRedeemer.Codes.Invalid);
        // Locked: even the right code is not evaluated.
        var locked = await RedeemRefusedAsync(w, email, code);
        locked.Code.Should().Be(WelcomeCodeRedeemer.Codes.TryLater);
        locked.Status.Should().Be(429);

        // Fast-forward past the locks to the burn.
        await using (var db = _fx.CreateDb())
        {
            var link = await db.EmployeeUserAccounts.IgnoreQueryFilters().SingleAsync(x => x.EmployeeId == id);
            link.WelcomeCodeFailedAttempts = WelcomeCodes.BurnAt - 1;
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE employee_user_accounts SET updated_at_utc = now() - interval '2 hours' WHERE id = {link.Id}");
        }
        (await RedeemRefusedAsync(w, email, Wrong(code))).Code.Should().Be(WelcomeCodeRedeemer.Codes.Invalid);
        (await RedeemRefusedAsync(w, email, code)).Code.Should().Be(WelcomeCodeRedeemer.Codes.Used, "a burnt code never works again");
        await using (var db = _fx.CreateDb())
        {
            (await db.AuditLogs.IgnoreQueryFilters().AnyAsync(a => a.TenantId == w.TenantId && a.Action == WelcomeCodeRedeemer.BurnedAction)).Should().BeTrue();
            (await db.Notifications.IgnoreQueryFilters().AnyAsync(n => n.TenantId == w.TenantId && n.UserId == w.HrOfficerId)).Should().BeTrue();
        }
        (await StateAsync(w, id)).Should().Be(EmployeeAccessStates.NotStarted);
    }

    [Fact]
    public async Task Redeem_Expired_PasswordPolicy_AndSeatLimit()
    {
        var w = await SeedAsync(maxUsers: 6);
        var email = $"late@{w.Domain}";
        var id = await AddStagedAsync(w, email);
        var code = (await IssueAsync(w, w.HrOfficerId, [id])).Issued.Single().Code!;

        var policy = await RedeemRefusedAsync(w, email, code, password: "short");
        policy.Code.Should().Be(WelcomeCodeRedeemer.Codes.PasswordPolicy);
        policy.Detail.Should().NotBeNullOrEmpty();
        (await RedeemRefusedAsync(w, email, code, password: email)).Code.Should().Be(WelcomeCodeRedeemer.Codes.PasswordPolicy);

        await using (var db = _fx.CreateDb())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE employee_user_accounts SET welcome_code_expires_at_utc = now() - interval '1 minute' WHERE employee_id = {id}");
        (await RedeemRefusedAsync(w, email, code)).Code.Should().Be(WelcomeCodeRedeemer.Codes.Expired);
        (await RedeemRefusedAsync(w, email, Wrong(code))).Code.Should().Be(WelcomeCodeRedeemer.Codes.Invalid, "expiry is told only to the code's holder");
        (await StateAsync(w, id)).Should().Be(EmployeeAccessStates.NotStarted);
        (await GetAsync(w, id, w.HrOfficerId))!.LastCodeExpiredAtUtc.Should().NotBeNull();

        // Seats: 4 staff + this one fills 6 once a sixth login is active.
        var fresh = (await IssueAsync(w, w.HrOfficerId, [id])).Issued.Single().Code!;
        await AddUserAsync(w, $"staff5@{w.Domain}", ["Employee"], active: true);
        await AddUserAsync(w, $"staff6@{w.Domain}", ["Employee"], active: true);
        var seat = await RedeemRefusedAsync(w, email, fresh);
        seat.Code.Should().Be(WelcomeCodeRedeemer.Codes.SeatLimit);
        // And issuing another code skips with seat_limit (staging itself never consumed one).
        var another = await AddStagedAsync(w, $"another@{w.Domain}");
        Reason(await IssueAsync(w, w.HrOfficerId, [another]), another).Should().Be(EmployeeAccessService.Skip.SeatLimit);
    }

    [Fact]
    public async Task Redeem_FromTheIssuersLastAddress_IsFlagged_AndTheIssuerIsBarredFromDecidingForThatPerson()
    {
        var w = await SeedAsync();
        var email = $"same.desk@{w.Domain}";
        var id = await AddStagedAsync(w, email);
        await using (var db = _fx.CreateDb())
        {
            db.LoginActivities.Add(new LoginActivity { TenantId = w.TenantId, UserId = w.HrOfficerId, EventType = LoginEventTypes.LoginSuccess, IpAddress = "198.51.100.77" });
            await db.SaveChangesAsync();
        }
        var code = (await IssueAsync(w, w.HrOfficerId, [id])).Issued.Single().Code!;
        await using (var db = _fx.CreateDb())
            await Auth(db).RedeemWelcomeCodeAsync(new WelcomeRedeemRequest(email, code, NewPassword, w.Slug), new RequestContext("198.51.100.77", "same pc"), NoPresenter, default);

        await using var verify = _fx.CreateDb();
        (await verify.AuditLogs.IgnoreQueryFilters().AnyAsync(a => a.TenantId == w.TenantId && a.Action == WelcomeCodeRedeemer.IssuerDeviceAction)).Should().BeTrue();
        (await CredentialHandlerBar.IsBarredAsync(verify, w.TenantId, id, w.HrOfficerId, DateTime.UtcNow, default)).Should().BeTrue();
        (await CredentialHandlerBar.IsBarredAsync(verify, w.TenantId, id, w.HrOfficer2Id, DateTime.UtcNow, default)).Should().BeFalse();
        (await CredentialHandlerBar.IsBarredAsync(verify, w.TenantId, id, w.HrOfficerId, DateTime.UtcNow.AddDays(31), default)).Should().BeFalse();

        // The bar in use: the issuer may not approve that employee's self-service profile change; a colleague may.
        var change = new EmployeeProfileChangeRequest { TenantId = w.TenantId, EmployeeId = id, RequestedChangesJson = "{\"phone\":\"+966500000000\"}" };
        verify.EmployeeProfileChangeRequests.Add(change);
        await verify.SaveChangesAsync();
        var refused = await Ess(verify, w, w.HrOfficerId).ApproveProfileChange(change.Id, new ProfileChangeDecisionDto(null), default);
        JsonSerializer.SerializeToElement(Assert.IsType<ConflictObjectResult>(refused).Value).GetProperty("code").GetString()
            .Should().Be("credential_handler_cannot_decide");
        Assert.IsType<OkObjectResult>(await Ess(verify, w, w.HrOfficer2Id).ApproveProfileChange(change.Id, new ProfileChangeDecisionDto(null), default));
    }

    private static EmployeeSelfServiceController Ess(ZayraDbContext db, World w, Guid caller)
    {
        var letters = new NoLetters();
        var storage = new NoStorage();
        return new EmployeeSelfServiceController(db, letters, new Zayra.Api.Infrastructure.Documents.PdfRenderGate(1),
            new Zayra.Api.Infrastructure.Leave.LeaveService(db, new Zayra.Api.Infrastructure.Approvals.ApprovalRouter(db)),
            new Zayra.Api.Infrastructure.Attendance.AttendanceService(db, TestNotifications.For(db), new NoHttp()),
            new Zayra.Api.Infrastructure.Documents.Letters.HrLetterIssuer(db, letters, storage), storage)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", w.TenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, caller.ToString()),
                        new Claim(ClaimTypes.Role, "HR Officer"), new Claim("permission", "employees.write"),
                    }, "Test")),
                },
            },
        };
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Redeem_FromABrowserSignedInAsSomeoneElse_IsRefused()
    {
        var w = await SeedAsync();
        var email = $"shared.pc@{w.Domain}";
        var id = await AddStagedAsync(w, email);
        var code = (await IssueAsync(w, w.HrOfficerId, [id])).Issued.Single().Code!;
        await using var db = _fx.CreateDb();
        var refused = await Assert.ThrowsAsync<WelcomeRedeemRefusedException>(() => Auth(db).RedeemWelcomeCodeAsync(
            new WelcomeRedeemRequest(email, code, NewPassword, w.Slug), new RequestContext("1.2.3.4", "x"),
            new WelcomeCodeRedeemer.Presenter(w.HrOfficerId, w.TenantId, null), default));
        refused.Code.Should().Be(WelcomeCodeRedeemer.Codes.SignOutFirst);
        await RedeemAsync(w, email, code);
    }

    [Fact]
    public async Task ChangingTheWorkEmail_KillsTheLiveCode_AndTheOldCodeFailsForTheNewAddress()
    {
        var w = await SeedAsync();
        var id = await AddStagedAsync(w, $"before@{w.Domain}");
        var code = (await IssueAsync(w, w.HrOfficerId, [id])).Issued.Single().Code!;
        await using (var db = _fx.CreateDb())
        {
            var e = await db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == id);
            var prior = e.WorkEmail;
            e.WorkEmail = $"after@{w.Domain}";
            await WorkEmailLoginGuard.ApplyAsync(db, e, w.TenantId, prior, Ctx(w.HrOfficer2Id, w.TenantId), DateTime.UtcNow, default);
            await db.SaveChangesAsync();
        }
        (await StateAsync(w, id)).Should().Be(EmployeeAccessStates.NotStarted);
        (await RedeemRefusedAsync(w, $"after@{w.Domain}", code)).Code.Should().Be(WelcomeCodeRedeemer.Codes.Invalid);
        (await RedeemRefusedAsync(w, $"before@{w.Domain}", code)).Code.Should().Be(WelcomeCodeRedeemer.Codes.Invalid);
    }

    [Fact]
    public async Task ARoleChangeOnTheLogin_KillsTheLiveCode()
    {
        var w = await SeedAsync();
        var id = await AddStagedAsync(w, $"promoted@{w.Domain}");
        await IssueAsync(w, w.HrOfficerId, [id]);
        await using (var db = _fx.CreateDb())
        {
            var link = await db.EmployeeUserAccounts.IgnoreQueryFilters().SingleAsync(x => x.EmployeeId == id);
            var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == link.UserId);
            var role = await db.Roles.IgnoreQueryFilters().SingleAsync(r => r.TenantId == w.TenantId && r.NormalizedName == "MANAGER");
            db.UserRoles.Add(new UserRole { UserId = user.Id, User = user, RoleId = role.Id });
            await db.SaveChangesAsync();
        }
        (await StateAsync(w, id)).Should().Be(EmployeeAccessStates.NotStarted);
    }

    // ── Sign-in without a workspace ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Login_WithoutWorkspace_RoutesOnlyAUniqueDomain_AndNeverRevealsWhetherTheEmailExists()
    {
        var w = await SeedAsync();
        var staffEmail = await EmailOfAsync(w.HrOfficerId);
        (await SignInAsync(null, staffEmail, StaffPassword)).Tokens.Should().NotBeNull();

        var wrongPassword = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SignInAsync(null, staffEmail, "Nope1!nope"));
        var unknown = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SignInAsync(null, $"ghost@{w.Domain}", "Nope1!nope"));
        unknown.Message.Should().Be(wrongPassword.Message);

        // A second tenant claiming the same domain makes it ambiguous: the client must ask for the Company ID.
        var other = await SeedAsync(domain: w.Domain);
        await Assert.ThrowsAsync<WorkspaceRequiredException>(() => SignInAsync(null, staffEmail, StaffPassword));
        (await SignInAsync(w.Slug, staffEmail, StaffPassword)).Tokens.Should().NotBeNull();
        await Assert.ThrowsAsync<WorkspaceRequiredException>(() => SignInAsync(null, "someone@unknown-domain.test", "x"));
        _ = other;

        await using var db = _fx.CreateDb();
        await Assert.ThrowsAsync<WorkspaceRequiredException>(() => Auth(db).ForgotPasswordAsync(new ForgotPasswordRequest(staffEmail), new RequestContext("1.1.1.1", "x"), default));
        (await db.PasswordResetTokens.AnyAsync(t => t.UserId == w.HrOfficerId)).Should().BeFalse("no workspace, no mail");
    }

    [Fact]
    public async Task Login_AcceptsArabicIndicDigitsInThePassword()
    {
        var w = await SeedAsync();
        var email = $"digits@{w.Domain}";
        var id = await AddStagedAsync(w, email);
        await RedeemAsync(w, email, (await IssueAsync(w, w.HrOfficerId, [id])).Issued.Single().Code!, password: "Kynex!pass" + ToArabicIndic("2026"));
        (await SignInAsync(w.Slug, email, "Kynex!pass2026")).Tokens.Should().NotBeNull();
        (await SignInAsync(w.Slug, email, "Kynex!pass" + ToArabicIndic("2026"))).Tokens.Should().NotBeNull();
    }

    // ── Work-email backfill ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Backfill_DryRunWritesNothing_SaveStagesLogins_AndEveryRefusalIsReported()
    {
        var w = await SeedAsync();
        var a = await AddEmployeeAsync(w, "", code: "BF-A");
        var b = await AddEmployeeAsync(w, "", code: "BF-B");
        var c = await AddEmployeeAsync(w, "", code: "BF-C");
        var d = await AddEmployeeAsync(w, "", code: "BF-D");
        var activeOne = await AddStagedAsync(w, $"live@{w.Domain}", code: "BF-E");
        await RedeemAsync(w, $"live@{w.Domain}", (await IssueAsync(w, w.HrOfficerId, [activeOne])).Issued.Single().Code!);
        var rows = new List<WorkEmailBackfillRow>
        {
            new("BF-A", $"alpha@{w.Domain}"),
            new("BF-B", "beta@elsewhere.test"),
            new("BF-NOPE", $"ghost@{w.Domain}"),
            new("BF-C", $"same@{w.Domain}"),
            new("BF-D", $"SAME@{w.Domain}"),
            new("BF-E", $"renamed@{w.Domain}"),
        };

        var dry = await BackfillAsync(w, rows, dryRun: true);
        dry.Saved.Should().Be(0);
        dry.Matched.Select(m => m.EmployeeCode).Should().BeEquivalentTo(["BF-A"]);
        dry.NotFound.Should().BeEquivalentTo(["BF-NOPE"]);
        dry.WrongDomain.Should().ContainSingle(x => x.EmployeeCode == "BF-B" && x.ExpectedDomain == w.Domain);
        dry.Conflicts.Select(x => (x.EmployeeCode, x.Reason)).Should().BeEquivalentTo(new[]
        {
            ("BF-C", "duplicate_in_file"), ("BF-D", "duplicate_in_file"), ("BF-E", "username_differs"),
        });
        (await StateAsync(w, a)).Should().Be(EmployeeAccessStates.WaitingForWorkEmail);

        var saved = await BackfillAsync(w, rows, dryRun: false);
        saved.Saved.Should().Be(1);
        (await StateAsync(w, a)).Should().Be(EmployeeAccessStates.NotStarted);
        await using (var db = _fx.CreateDb())
        {
            (await db.Employees.IgnoreQueryFilters().SingleAsync(e => e.Id == b)).WorkEmail.Should().BeEmpty();
            (await db.Employees.IgnoreQueryFilters().SingleAsync(e => e.Id == activeOne)).WorkEmail.Should().Be($"live@{w.Domain}");
        }
        // Idempotent re-run.
        (await BackfillAsync(w, [new("BF-A", $"alpha@{w.Domain}")], dryRun: false)).Matched.Should().ContainSingle();
        await using var verify = _fx.CreateDb();
        (await verify.EmployeeUserAccounts.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == a)).Should().Be(1);
        _ = c; _ = d;
    }

    [Fact]
    public async Task Backfill_RefusesAPlusAddress_AndAnAddressAnotherEmployeeHolds()
    {
        var w = await SeedAsync();
        await AddEmployeeAsync(w, "", code: "PL-A");
        await AddEmployeeAsync(w, "", code: "PL-B");
        await AddEmployeeAsync(w, $"holder@{w.Domain}", code: "PL-C");
        var result = await BackfillAsync(w, [new("PL-A", $"a+b@{w.Domain}"), new("PL-B", $"holder@{w.Domain}")], dryRun: false);
        result.Saved.Should().Be(0);
        result.Conflicts.Select(c => c.Reason).Should().BeEquivalentTo([WorkEmailPlusAddressException.Code, "email_used_by_another_employee"]);
    }

    // ── Isolation ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnotherTenantsEmployee_IsInvisible_AndAnotherCompanysIsOutOfScope()
    {
        var w = await SeedAsync();
        var other = await SeedAsync();
        var foreign = await AddStagedAsync(other, $"foreign@{other.Domain}");
        (await GetAsync(w, foreign, w.HrOfficerId)).Should().BeNull();
        var issued = await IssueAsync(w, w.HrOfficerId, [foreign]);
        issued.Issued.Should().BeEmpty();
        Reason(issued, foreign).Should().Be(EmployeeAccessService.Skip.NotFound);

        var otherCompany = await AddEmployeeAsync(w, "", companyId: w.OtherCompanyId);
        var scoped = EntityScopeContext.ForCompanies(new[] { w.CompanyId });
        await using var db = _fx.CreateDb();
        (await Service(db).GetAsync(w.TenantId, otherCompany, scoped, Ctx(w.HrOfficerId, w.TenantId), true, false, default)).Should().BeNull();
        var scopedIssue = await Service(db).IssueCodesAsync(w.TenantId, new IssueCodesRequest([otherCompany]), scoped, Ctx(w.HrOfficerId, w.TenantId), true, false, default);
        Reason(scopedIssue, otherCompany).Should().Be(EmployeeAccessService.Skip.NotFound);
    }

    // ── Company email domain claims ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EmailDomain_RefusesPublicMail_AndADomainAnotherTenantHolds()
    {
        var w = await SeedAsync();
        var other = await SeedAsync();
        await using var db = _fx.CreateDb();
        var refused = await Assert.ThrowsAsync<EmailDomainRefusedException>(() => CompanyEmailDomainRules.EnsureClaimableAsync(db, other.TenantId, w.Domain, default));
        refused.Code.Should().Be("email_domain_claimed");
        refused.Status.Should().Be(409);
        (await Assert.ThrowsAsync<EmailDomainRefusedException>(() => CompanyEmailDomainRules.EnsureClaimableAsync(db, w.TenantId, "Gmail.com", default)))
            .Code.Should().Be("email_domain_public");
        await CompanyEmailDomainRules.EnsureClaimableAsync(db, w.TenantId, w.Domain, default); // its own domain is fine
    }

    // ── Harness ────────────────────────────────────────────────────────────────────────────────────────

    private static readonly IOptions<JwtOptions> Jwt = Options.Create(new JwtOptions
    {
        Issuer = "Zayra.Tests", TenantAudience = "kynexone-tenant-test", PlatformAudience = "kynexone-platform-test",
        SigningKey = "TEST_SIGNING_KEY_WITH_MORE_THAN_64_CHARACTERS_FOR_EMPLOYEE_ACCESS_TESTS_0123456789", AccessTokenMinutes = 30, RefreshTokenDays = 7,
    });

    private static readonly WelcomeCodeRedeemer.Presenter NoPresenter = new(null, null, null);

    private static RequestContext Ctx(Guid userId, Guid tenantId) => new("10.0.0.5", "tests", userId, tenantId);

    private static EmployeeAccessService Service(ZayraDbContext db, IEmailService? email = null) =>
        new(db, email ?? new RecordingEmail(configured: false), Jwt);

    private static EmployeeManagementService Employees(ZayraDbContext db) =>
        new(db, new AuditService(db), new NoStorage(), TestNotifications.For(db));

    private static AuthService Auth(ZayraDbContext db) =>
        new(db, new Pbkdf2PasswordHasher(), new JwtTokenService(Jwt), new AuditService(db), new RecordingEmail(configured: false), Jwt,
            new NullMfaService(), new TotpService(DataProtectionProvider.Create("ZayraTests")), NullLogger<AuthService>.Instance);

    private static string Reason(IssueCodesResponse response, int employeeId) => response.Skipped.Single(s => s.EmployeeId == employeeId).ReasonCode;

    private static string Wrong(string code) => code[..7] + (code[7] == '9' ? '0' : (char)(code[7] + 1));

    private static string ToArabicIndic(string digits) => new(digits.Select(c => c is >= '0' and <= '9' ? (char)('٠' + (c - '0')) : c).ToArray());

    private async Task<IssueCodesResponse> IssueAsync(World w, Guid caller, int[] ids, bool canReset = false, IEmailService? email = null, string? delivery = null)
    {
        await using var db = _fx.CreateDb();
        return await Service(db, email).IssueCodesAsync(w.TenantId, new IssueCodesRequest(ids, delivery), EntityScopeContext.GroupLevel, Ctx(caller, w.TenantId), true, canReset, default);
    }

    private async Task<WorkEmailBackfillResponse> BackfillAsync(World w, IReadOnlyList<WorkEmailBackfillRow> rows, bool dryRun)
    {
        await using var db = _fx.CreateDb();
        return await Service(db).BackfillWorkEmailsAsync(w.TenantId, new WorkEmailBackfillRequest(rows, dryRun), EntityScopeContext.GroupLevel, Ctx(w.HrOfficer2Id, w.TenantId), default);
    }

    private async Task<EmployeeAccessDto?> GetAsync(World w, int id, Guid caller)
    {
        await using var db = _fx.CreateDb();
        return await Service(db).GetAsync(w.TenantId, id, EntityScopeContext.GroupLevel, Ctx(caller, w.TenantId), true, true, default);
    }

    private async Task<string> StateAsync(World w, int id)
    {
        await using var db = _fx.CreateDb();
        return (await EmployeeAccessStates.EvaluateAsync(db, w.TenantId, new[] { id }, DateTime.UtcNow, default))[id].State.State;
    }

    private async Task RedeemAsync(World w, string email, string code, string password = NewPassword)
    {
        await using var db = _fx.CreateDb();
        await Auth(db).RedeemWelcomeCodeAsync(new WelcomeRedeemRequest(email, code, password, w.Slug), new RequestContext("203.0.113.1", "tests"), NoPresenter, default);
    }

    private async Task<WelcomeRedeemRefusedException> RedeemRefusedAsync(World w, string email, string code, string password = NewPassword)
    {
        await using var db = _fx.CreateDb();
        return await Assert.ThrowsAsync<WelcomeRedeemRefusedException>(() => Auth(db).RedeemWelcomeCodeAsync(
            new WelcomeRedeemRequest(email, code, password, w.Slug), new RequestContext("203.0.113.1", "tests"), NoPresenter, default));
    }

    private async Task<AuthLoginResult> SignInAsync(string? slug, string email, string password)
    {
        await using var db = _fx.CreateDb();
        return await Auth(db).LoginAsync(new LoginRequest(email, password, slug), new RequestContext("127.0.0.1", "tests"), default);
    }

    private async Task<World> SeedAsync(int? maxUsers = null, string? domain = null)
    {
        await using var db = _fx.CreateDb();
        var tenantId = Guid.NewGuid();
        var slug = $"acc-{tenantId:N}"[..24];
        domain ??= $"{tenantId:N}.test";
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Access Tenant", Slug = slug, IsActive = true });
        if (maxUsers is int max) db.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenantId, MaxUsers = max });
        var permissions = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var key in Keys)
        {
            var existing = await db.Permissions.SingleOrDefaultAsync(x => x.Key == key);
            if (existing is null)
            {
                existing = new Permission { Key = key, Module = "Test", Description = key };
                db.Permissions.Add(existing);
                await db.SaveChangesAsync();
            }
            permissions[key] = existing.Id;
        }
        void AddRole(string name, params string[] keys)
        {
            var role = new Role
            {
                Id = Guid.NewGuid(), TenantId = tenantId, Name = name, NormalizedName = AuthService.Normalize(name), Description = name,
                IsSystem = true, IsEditable = true, IsActive = true, AuthorityLevel = 10,
            };
            db.Roles.Add(role);
            foreach (var key in keys) db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permissions[key] });
        }
        AddRole("Admin", Keys);
        AddRole("HR Manager", "employees.read", "employees.write", "employees.access.issue", "employees.access.reset", "profile.read", "ess.read", "ess.write", "loans.self");
        AddRole("HR Officer", "employees.read", "employees.write", "employees.access.issue", "profile.read", "ess.read", "ess.write", "loans.self");
        AddRole("Employee", "profile.read", "ess.read", "ess.write", "loans.self");
        AddRole("Manager", "employees.read", "manager.read", "manager.approve", "approvals.decide");
        AddRole("Payroll Approver", "payroll.approve");
        var company = new Company { TenantId = tenantId, LegalNameEn = $"Access Co {tenantId:N}"[..20], TradeName = "Access", CountryCode = "SA", Jurisdiction = "SA", EmailDomain = domain };
        var noDomain = new Company { TenantId = tenantId, LegalNameEn = $"No Domain {tenantId:N}"[..20], TradeName = "NoDomain", CountryCode = "SA", Jurisdiction = "SA" };
        db.Companies.AddRange(company, noDomain);
        await db.SaveChangesAsync();
        var w = new World(tenantId, slug, domain, company.Id, noDomain.Id, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty);
        return w with
        {
            AdminId = await AddUserAsync(w, $"admin.{tenantId:N}@{domain}", ["Admin"], active: true, staff: true, name: "Admin"),
            HrOfficerId = await AddUserAsync(w, $"hr.one.{tenantId:N}@{domain}", ["HR Officer"], active: true, staff: true, name: "HR Officer One"),
            HrOfficer2Id = await AddUserAsync(w, $"hr.two.{tenantId:N}@{domain}", ["HR Officer"], active: true, staff: true, name: "HR Officer Two"),
            HrManagerId = await AddUserAsync(w, $"hr.manager.{tenantId:N}@{domain}", ["HR Manager"], active: true, staff: true, name: "HR Manager"),
        };
    }

    private async Task<Guid> AddUserAsync(World w, string email, string[] roles, bool active, bool staff = false, string name = "Someone")
    {
        await using var db = _fx.CreateDb();
        var id = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = id, TenantId = w.TenantId, Email = email.ToLowerInvariant(), NormalizedEmail = AuthService.Normalize(email), FullName = name,
            PasswordHash = new Pbkdf2PasswordHasher().Hash(StaffPassword),
            Status = active ? "Active" : "PendingPasswordSetup", AccessMode = staff ? AccessModes.FullPortal : AccessModes.EssOnly,
            IsActive = active, IsEmailConfirmed = active, IsGroupScope = staff, IdentityProvider = "Local", ProvisioningSource = "Local",
        });
        foreach (var roleName in roles)
        {
            var role = await db.Roles.IgnoreQueryFilters().SingleAsync(x => x.TenantId == w.TenantId && x.NormalizedName == AuthService.Normalize(roleName));
            db.UserRoles.Add(new UserRole { UserId = id, RoleId = role.Id });
        }
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<int> AddEmployeeAsync(World w, string workEmail, string status = "Active", Guid? companyId = null, string? name = null, string? code = null)
    {
        await using var db = _fx.CreateDb();
        var employee = new Employee
        {
            TenantId = w.TenantId, CompanyId = companyId ?? w.CompanyId,
            EmployeeCode = code ?? $"EA-{Guid.NewGuid():N}"[..16], FullName = name ?? $"Person {Guid.NewGuid():N}"[..20],
            WorkEmail = workEmail, Status = status, JoiningDate = DateTime.UtcNow.Date.AddDays(-30),
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return employee.Id;
    }

    private async Task<int> AddStagedAsync(World w, string workEmail, string? code = null)
    {
        var id = await AddEmployeeAsync(w, workEmail, code: code);
        await using var db = _fx.CreateDb();
        var e = await db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == id);
        (await new EmployeeLoginProvisioner(db).EnsureStagedLoginAsync(w.TenantId, e, Ctx(w.AdminId, w.TenantId), default)).Result
            .Should().Be(EmployeeLoginProvisioner.Results.Staged);
        await db.SaveChangesAsync();
        return id;
    }

    private async Task AddLinkAsync(World w, int employeeId, Guid userId)
    {
        await using var db = _fx.CreateDb();
        db.EmployeeUserAccounts.Add(new EmployeeUserAccount
        {
            TenantId = w.TenantId, EmployeeId = employeeId, UserId = userId, AccessMode = AccessModes.EssOnly, Status = "Active",
            RequiresPasswordSetup = false, IsPrimary = true,
        });
        (await db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == employeeId)).UserAccountId = userId;
        await db.SaveChangesAsync();
    }

    private async Task UpdateEmployeeAsync(int employeeId, Action<Employee> change)
    {
        await using var db = _fx.CreateDb();
        change(await db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == employeeId));
        await db.SaveChangesAsync();
    }

    private async Task AddAuditAsync(World w, string action, string entity, string entityId, Guid actor)
    {
        await using var db = _fx.CreateDb();
        db.AuditLogs.Add(new AuditLog { TenantId = w.TenantId, UserId = actor, Action = action, EntityName = entity, EntityId = entityId, CreatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    private async Task<List<string>> AuditActionsAsync(World w, string entity, string entityId)
    {
        await using var db = _fx.CreateDb();
        return await db.AuditLogs.IgnoreQueryFilters().Where(a => a.TenantId == w.TenantId && a.EntityName == entity && a.EntityId == entityId)
            .Select(a => a.Action).ToListAsync();
    }

    private async Task<string> EmailOfAsync(Guid userId)
    {
        await using var db = _fx.CreateDb();
        return (await db.Users.IgnoreQueryFilters().SingleAsync(x => x.Id == userId)).Email;
    }

    private static EmployeeCreateRequest Hire(string name, string? workEmail, Guid companyId) => new(
        EmployeeCode: null, ManualEmployeeCode: false, EnglishName: name, ArabicName: null, PreferredName: null, Gender: "Male",
        DateOfBirth: null, Nationality: "Saudi", MaritalStatus: null, PersonalEmail: null, WorkEmail: workEmail, MobileNumber: null,
        ProfilePhotoUrl: null, CompanyId: companyId, BranchId: null, DepartmentId: null, DesignationId: null, GradeId: null, CostCenterId: null,
        JobTitle: null, ReportingManagerEmployeeId: null, SecondLevelManagerEmployeeId: null, EmploymentType: "Full-Time", ContractType: "Unlimited",
        JoiningDate: DateTime.UtcNow.Date, ConfirmationDate: null, ProbationStartDate: null, ProbationEndDate: null, NoticePeriodDays: null,
        WorkLocation: null, PayrollGroup: null, ShiftPolicyCode: null, LeavePolicyCode: null, AttendancePolicyCode: null,
        PayrollProfile: null, SalaryBreakdown: null, ComplianceRecords: null);

    private static EmployeesController EmployeesCtl(ZayraDbContext db, World w, Guid callerId)
    {
        var audit = new AuditService(db);
        return new EmployeesController(db, new Pbkdf2PasswordHasher(), audit, new NoStorage(), TestNotifications.For(db), new NoHijri(),
            new Zayra.Api.Infrastructure.Common.DataScopeService(db), new NoLetters(), new Zayra.Api.Infrastructure.Approvals.ApprovalWorkflowService(db, audit))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", w.TenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, callerId.ToString()),
                        new Claim(ClaimTypes.Role, "Admin"), new Claim("permission", "employees.read"), new Claim("permission", "employees.write"),
                        new Claim("permission", "employees.bulk_import"),
                    }, "Test")),
                },
            },
        };
    }

    internal sealed class RecordingEmail(bool configured) : IEmailService
    {
        public List<(string To, string Subject)> Sent { get; } = new();
        public Task SendAsync(string toAddress, string toName, string subject, string htmlBody, IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
        {
            Sent.Add((toAddress, subject));
            return Task.CompletedTask;
        }
        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(configured);
    }

    private sealed class NoStorage : Zayra.Api.Infrastructure.Documents.IDocumentStorage
    {
        public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) => throw new NotSupportedException();
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => throw new NotSupportedException();
        public string ResolvePath(string storageUrl) => throw new NotSupportedException();
    }

    private sealed class NoHijri : Zayra.Api.Infrastructure.Localization.IHijriDateService
    {
        public Zayra.Api.Infrastructure.Localization.DateConversionDto FromGregorian(DateOnly date) => new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
    }

    private sealed class NoLetters : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
    {
        public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData data, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData data, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData data, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData data, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
