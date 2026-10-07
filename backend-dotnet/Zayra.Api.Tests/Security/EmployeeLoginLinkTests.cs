using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Models;
using Zayra.Api.Tests.Platform;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Linking an existing login to its employee record (POST /api/access/employee-logins/link-existing) and the
/// status that drives the User Management dialog (GET /api/access/employee-logins/{id}).
///
/// <para>THE DEFECT. A tenant admin made a login for noah.williams@… in User Management → Create User and gave it
/// the Employee role; the employee record carried that work email. Self-Service resolves the caller ONLY from
/// the employee_id claim, minted only from a live EmployeeUserAccounts row, and nothing could create that row
/// for an existing login: the invitation refuses an email an active login already uses. These tests drive the
/// real controller over Postgres and then sign Noah in for real, so "linked" means "Self-Service answers".</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class EmployeeLoginLinkTests
{
    private readonly PostgresFixture _fixture;
    public EmployeeLoginLinkTests(PostgresFixture fixture) => _fixture = fixture;

    private const string Password = "CorrectHorse1!Battery";

    private static readonly string[] AllKeys = ["security.manage", "employees.read", "employees.write", "payroll.approve", "profile.read",
        "ess.read", "ess.write", "loans.self", "approvals.decide"];

    private sealed record World(Guid TenantId, string Slug, Guid CompanyA, Guid CompanyB, Guid AdminId, Guid ConsoleId);

    // ── The defect, end to end ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LinkingNoahsExistingLogin_MakesHisNextSignInCarryEmployeeId_AndSelfServiceAnswers()
    {
        var w = await SeedAsync();
        var noahEmail = Email("noah.williams");
        var noah = await AddUserAsync(w, noahEmail, ["Reporting"], groupScope: false, password: Password);
        var employeeId = await AddEmployeeAsync(w, w.CompanyA, noahEmail);
        var refreshTokenId = await AddRefreshTokenAsync(noah);
        var stampBefore = await StampAsync(noah);

        // Before: Noah signs in fine, but his token has no employee_id and Self-Service refuses him.
        var before = await SignInAsync(w, noahEmail);
        before.Claims.Should().NotContain(c => c.Type == "employee_id");

        // The dialog's status: Noah's login carries the work email, so the next step is to link it.
        await using (var db = _fixture.CreateRetryingDb())
        {
            var status = Ok<EmployeeLoginStatusDto>((await Controller(db, w, w.AdminId).EmployeeLoginStatus(employeeId, default)).Result);
            status.NextAction.Should().Be(EmployeeLoginNextActions.LinkExisting);
            status.WillResetCredential.Should().BeFalse("nobody but Noah has held a credential for his login");
            status.LinkedLogin.Should().BeNull();
            status.MatchingLogin!.UserId.Should().Be(noah);
            status.WorkEmail.Should().Be(noahEmail);
        }

        await using (var db = _fixture.CreateRetryingDb())
        {
            var linked = Ok<EmployeeLoginLinkResultDto>((await Controller(db, w, w.AdminId).LinkExistingLogin(
                new LinkExistingLoginRequest(employeeId, noah, "Created in User Management before his employee record"), default)).Result);
            linked.UserId.Should().Be(noah);
            linked.EmployeeId.Should().Be(employeeId);
            linked.AlreadyLinked.Should().BeFalse();
            linked.AccessMode.Should().Be(AccessModes.FullPortal, "linking keeps the login's own access mode; it never downgrades it to ESSOnly");
        }

        await using (var verify = _fixture.CreateRetryingDb())
        {
            var link = await verify.EmployeeUserAccounts.IgnoreQueryFilters().SingleAsync(x => x.UserId == noah);
            link.EmployeeId.Should().Be(employeeId);
            link.IsDeleted.Should().BeFalse();
            link.IsPrimary.Should().BeTrue();
            link.Status.Should().Be("Active");
            link.RequiresPasswordSetup.Should().BeFalse();
            link.AccessMode.Should().Be(AccessModes.FullPortal);
            link.CreatedBy.Should().Be(w.AdminId);
            (await verify.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == employeeId)).UserAccountId.Should().Be(noah);

            // Existing roles preserved; the Employee role added.
            (await RoleNamesAsync(noah)).Should().Equal("Employee", "Reporting");

            // Company access for his own company, active.
            var grant = await verify.UserEntityAccesses.IgnoreQueryFilters().SingleAsync(x => x.UserId == noah);
            grant.CompanyId.Should().Be(w.CompanyA);
            grant.IsActive.Should().BeTrue();
            grant.GrantMode.Should().Be(EntityGrantModes.SelectedCompanies);

            // Every session minted without employee_id is retired.
            (await verify.RefreshTokens.SingleAsync(x => x.Id == refreshTokenId)).RevokedAtUtc.Should().NotBeNull();
            (await StampAsync(noah)).Should().BeAfter(stampBefore);

            var audit = await verify.AuditLogs.IgnoreQueryFilters().SingleAsync(x => x.TenantId == w.TenantId && x.Action == "access.employee_login_linked");
            audit.UserId.Should().Be(w.AdminId);
            audit.EntityName.Should().Be("EmployeeUserAccount");
            audit.EntityId.Should().Be(link.Id.ToString());
            using var meta = JsonDocument.Parse(audit.Metadata!);
            meta.RootElement.GetProperty("employeeId").GetInt32().Should().Be(employeeId);
            meta.RootElement.GetProperty("userId").GetGuid().Should().Be(noah);
            meta.RootElement.GetProperty("reason").GetString().Should().Contain("User Management");
            meta.RootElement.GetProperty("rolesAdded").EnumerateArray().Select(x => x.GetString()).Should().Equal("Employee");
            audit.Metadata.Should().NotContain(noahEmail, "the audit row carries ids, not personal data");
        }

        // After: a fresh sign-in carries employee_id, and Self-Service answers under Noah's real scope.
        var after = await SignInAsync(w, noahEmail);
        after.FindFirstValue("employee_id").Should().Be(employeeId.ToString());
        after.FindFirstValue("access_mode").Should().Be(AccessModes.FullPortal);
        var dashboard = await EssDashboardAsync(after);
        dashboard.Should().BeOfType<OkObjectResult>();

        // And the status now reads "linked".
        await using (var db = _fixture.CreateRetryingDb())
        {
            var status = Ok<EmployeeLoginStatusDto>((await Controller(db, w, w.AdminId).EmployeeLoginStatus(employeeId, default)).Result);
            status.NextAction.Should().Be(EmployeeLoginNextActions.Linked);
            status.LinkedLogin!.UserId.Should().Be(noah);
            status.MatchingLogin.Should().BeNull();
        }

        // And the user list names the employee on Noah's row (one projection, no per-row lookup).
        await using (var db = _fixture.CreateRetryingDb())
        {
            var list = Ok<PagedResult<UserListDto>>((await Controller(db, w, w.AdminId).ListUsers("noah.williams", null, null, 1, 30, default)).Result);
            var row = list.Items.Single(x => x.Id == noah);
            row.EmployeeId.Should().Be(employeeId);
            row.EmployeeName.Should().Be("Noah Williams");
            row.EmployeeCode.Should().StartWith("EMP-");
        }
    }

    [Fact]
    public async Task LinkingTwice_IsIdempotent_OneRowOneAudit()
    {
        var w = await SeedAsync();
        var email = Email("twice");
        var user = await AddUserAsync(w, email, ["Employee"], groupScope: false);
        var employeeId = await AddEmployeeAsync(w, w.CompanyA, email);

        for (var i = 0; i < 2; i++)
        {
            await using var db = _fixture.CreateRetryingDb();
            var result = Ok<EmployeeLoginLinkResultDto>((await Controller(db, w, w.AdminId).LinkExistingLogin(
                new LinkExistingLoginRequest(employeeId, user, "link"), default)).Result);
            result.AlreadyLinked.Should().Be(i == 1);
        }

        await using var verify = _fixture.CreateRetryingDb();
        (await verify.EmployeeUserAccounts.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == employeeId && x.TenantId == w.TenantId)).Should().Be(1);
        (await verify.AuditLogs.IgnoreQueryFilters().CountAsync(x => x.TenantId == w.TenantId && x.Action == "access.employee_login_linked")).Should().Be(1);
        (await RoleNamesAsync(user)).Should().Equal("Employee");
    }

    [Fact]
    public async Task ConcurrentDoubleLink_ProducesExactlyOneRow()
    {
        var w = await SeedAsync();
        var email = Email("race");
        var user = await AddUserAsync(w, email, ["Employee"], groupScope: false);
        var employeeId = await AddEmployeeAsync(w, w.CompanyA, email);

        async Task<IActionResult?> LinkAsync()
        {
            await using var db = _fixture.CreateRetryingDb();
            return (await Controller(db, w, w.AdminId).LinkExistingLogin(new LinkExistingLoginRequest(employeeId, user, "race"), default)).Result;
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(LinkAsync)));
        results.Should().AllBeOfType<OkObjectResult>();

        await using var verify = _fixture.CreateRetryingDb();
        (await verify.EmployeeUserAccounts.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == employeeId && x.TenantId == w.TenantId && !x.IsDeleted)).Should().Be(1);
        (await verify.AuditLogs.IgnoreQueryFilters().CountAsync(x => x.TenantId == w.TenantId && x.Action == "access.employee_login_linked")).Should().Be(1);
        (await verify.UserRoles.CountAsync(x => x.UserId == user)).Should().Be(1);
        (await verify.UserEntityAccesses.IgnoreQueryFilters().CountAsync(x => x.UserId == user)).Should().Be(1);
    }

    // ── Identity evidence ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EmailMismatch_IsRefused_AndPersonalEmailIsNeverEvidence()
    {
        var w = await SeedAsync();
        var email = Email("mismatch");
        var user = await AddUserAsync(w, email, ["Employee"], groupScope: false);
        // The login's email is the employee's PERSONAL email (employee-editable via ESS), not the work email.
        var employeeId = await AddEmployeeAsync(w, w.CompanyA, Email("someone-else"), personalEmail: email);

        await using (var db = _fixture.CreateRetryingDb())
        {
            var status = Ok<EmployeeLoginStatusDto>((await Controller(db, w, w.AdminId).EmployeeLoginStatus(employeeId, default)).Result);
            status.NextAction.Should().Be(EmployeeLoginNextActions.Invite, "no login carries the work email");
            status.MatchingLogin.Should().BeNull();
        }
        await using (var db = _fixture.CreateRetryingDb())
        {
            var bad = Assert.IsType<BadRequestObjectResult>((await Controller(db, w, w.AdminId).LinkExistingLogin(
                new LinkExistingLoginRequest(employeeId, user, "link"), default)).Result);
            Message(bad).Should().Contain("work email").And.Contain("invitation");
        }
        await AssertNotLinkedAsync(user, employeeId);
    }

    [Fact]
    public async Task EmployeeWithoutWorkEmail_NeedsOne()
    {
        var w = await SeedAsync();
        var employeeId = await AddEmployeeAsync(w, w.CompanyA, "");
        await using var db = _fixture.CreateRetryingDb();
        var status = Ok<EmployeeLoginStatusDto>((await Controller(db, w, w.AdminId).EmployeeLoginStatus(employeeId, default)).Result);
        status.NextAction.Should().Be(EmployeeLoginNextActions.NeedsWorkEmail);
        status.Reason.Should().Contain("work email");
    }

    // ── Separation of duties and the privilege ceiling ──────────────────────────────────────────────

    [Fact]
    public async Task AnAdministrator_CannotLinkTheirOwnLogin()
    {
        var w = await SeedAsync();
        var adminEmail = await EmailOfAsync(w.AdminId);
        var employeeId = await AddEmployeeAsync(w, w.CompanyA, adminEmail);

        await using (var db = _fixture.CreateRetryingDb())
        {
            var status = Ok<EmployeeLoginStatusDto>((await Controller(db, w, w.AdminId).EmployeeLoginStatus(employeeId, default)).Result);
            status.NextAction.Should().Be(EmployeeLoginNextActions.Blocked);
            status.Reason.Should().Contain("Another administrator must link your login");
        }
        await using (var db = _fixture.CreateRetryingDb())
        {
            var body = AssertRefused((await Controller(db, w, w.AdminId).LinkExistingLogin(
                new LinkExistingLoginRequest(employeeId, w.AdminId, "self"), default)).Result, PrivilegeCeiling.Codes.SelfChange);
            body.GetProperty("message").GetString().Should().Contain("Another administrator must link your login");
        }
        await AssertNotLinkedAsync(w.AdminId, employeeId);

        // Another administrator can.
        await using (var db = _fixture.CreateRetryingDb())
            (await Controller(db, w, w.ConsoleId).LinkExistingLogin(new LinkExistingLoginRequest(employeeId, w.AdminId, "by another admin"), default))
                .Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden,
                    "…but not a Console Admin reaching up to an Admin");
    }

    [Fact]
    public async Task ALoginAboveTheCaller_IsRefused()
    {
        var w = await SeedAsync();
        var email = Email("payroll");
        var payroll = await AddUserAsync(w, email, ["Payroll Manager"], groupScope: false);
        var employeeId = await AddEmployeeAsync(w, w.CompanyA, email);

        await using (var db = _fixture.CreateRetryingDb())
            AssertRefused((await Controller(db, w, w.ConsoleId).LinkExistingLogin(
                new LinkExistingLoginRequest(employeeId, payroll, "link"), default)).Result, PrivilegeCeiling.Codes.TargetAboveCeiling);
        await AssertNotLinkedAsync(payroll, employeeId);

        await using (var verify = _fixture.CreateRetryingDb())
        {
            var refused = await verify.AuditLogs.IgnoreQueryFilters().SingleAsync(x => x.TenantId == w.TenantId && x.Action == "access.change_refused");
            refused.Metadata.Should().Contain("access.employee_login_linked");
        }

        // An Admin may.
        await using (var db = _fixture.CreateRetryingDb())
            (await Controller(db, w, w.AdminId).LinkExistingLogin(new LinkExistingLoginRequest(employeeId, payroll, "link"), default))
                .Result.Should().BeOfType<OkObjectResult>();
        (await RoleNamesAsync(payroll)).Should().Equal("Employee", "Payroll Manager");
    }

    // ── One login, one employee ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ALoginAlreadyLinkedToAnotherEmployee_IsRefused()
    {
        var w = await SeedAsync();
        var email = Email("taken-login");
        var user = await AddUserAsync(w, email, ["Employee"], groupScope: false);
        var other = await AddEmployeeAsync(w, w.CompanyA, Email("other-record"));
        await AddLinkAsync(w, other, user);
        var employeeId = await AddEmployeeAsync(w, w.CompanyA, email);

        await using (var db = _fixture.CreateRetryingDb())
        {
            var status = Ok<EmployeeLoginStatusDto>((await Controller(db, w, w.AdminId).EmployeeLoginStatus(employeeId, default)).Result);
            status.NextAction.Should().Be(EmployeeLoginNextActions.Blocked);
            status.Reason.Should().Contain("already linked to another employee");
        }
        await using (var db = _fixture.CreateRetryingDb())
            Message(Assert.IsType<BadRequestObjectResult>((await Controller(db, w, w.AdminId).LinkExistingLogin(
                new LinkExistingLoginRequest(employeeId, user, "link"), default)).Result)).Should().Contain("already linked to another employee");
        await AssertNotLinkedAsync(user, employeeId);
    }

    [Fact]
    public async Task AnEmployeeAlreadyLinkedToAnotherLogin_IsRefused()
    {
        var w = await SeedAsync();
        var email = Email("taken-record");
        var user = await AddUserAsync(w, email, ["Employee"], groupScope: false);
        var employeeId = await AddEmployeeAsync(w, w.CompanyA, email);
        var holder = await AddUserAsync(w, Email("holder"), ["Employee"], groupScope: false);
        await AddLinkAsync(w, employeeId, holder);

        await using (var db = _fixture.CreateRetryingDb())
            Message(Assert.IsType<BadRequestObjectResult>((await Controller(db, w, w.AdminId).LinkExistingLogin(
                new LinkExistingLoginRequest(employeeId, user, "link"), default)).Result)).Should().Contain("already linked to a login");
        await AssertNotLinkedAsync(user, employeeId);
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("locked")]
    [InlineData("nologin")]
    [InlineData("suspended")]
    public async Task ALoginThatCannotSignIn_IsRefused(string state)
    {
        var w = await SeedAsync();
        var email = Email($"state-{state}");
        var user = await AddUserAsync(w, email, ["Employee"], groupScope: false);
        var employeeId = await AddEmployeeAsync(w, w.CompanyA, email);
        await using (var seed = _fixture.CreateRetryingDb())
        {
            var u = await seed.Users.IgnoreQueryFilters().SingleAsync(x => x.Id == user);
            switch (state)
            {
                case "deleted": u.IsDeleted = true; u.IsActive = false; break;
                case "locked": u.IsLocked = true; u.Status = "Locked"; break;
                case "nologin": u.AccessMode = AccessModes.NoLogin; u.IsActive = false; break;
                case "suspended": u.IsActive = false; u.Status = "Suspended"; break;
            }
            await seed.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateRetryingDb())
        {
            var status = Ok<EmployeeLoginStatusDto>((await Controller(db, w, w.AdminId).EmployeeLoginStatus(employeeId, default)).Result);
            status.NextAction.Should().Be(EmployeeLoginNextActions.Blocked);
            status.Reason.Should().NotBeNullOrWhiteSpace();
        }
        await using (var db = _fixture.CreateRetryingDb())
            Assert.IsType<BadRequestObjectResult>((await Controller(db, w, w.AdminId).LinkExistingLogin(
                new LinkExistingLoginRequest(employeeId, user, "link"), default)).Result);
        await AssertNotLinkedAsync(user, employeeId);
    }

    // ── Scope and tenancy ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnEmployeeOutsideTheCallersCompanies_IsNotFound()
    {
        var w = await SeedAsync();
        var email = Email("company-b");
        var user = await AddUserAsync(w, email, ["Employee"], groupScope: false);
        var employeeId = await AddEmployeeAsync(w, w.CompanyB, email);

        await using (var db = _fixture.CreateRetryingDb())
            (await Controller(db, w, w.AdminId, scopedTo: w.CompanyA).EmployeeLoginStatus(employeeId, default))
                .Result.Should().BeOfType<NotFoundObjectResult>();
        await using (var db = _fixture.CreateRetryingDb())
            (await Controller(db, w, w.AdminId, scopedTo: w.CompanyA).LinkExistingLogin(new LinkExistingLoginRequest(employeeId, user, "link"), default))
                .Result.Should().BeOfType<NotFoundObjectResult>();
        await AssertNotLinkedAsync(user, employeeId);

        // A group-level administrator can link the same login (it has no company access yet).
        await using (var db = _fixture.CreateRetryingDb())
            (await Controller(db, w, w.AdminId).LinkExistingLogin(new LinkExistingLoginRequest(employeeId, user, "link"), default))
                .Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task ALoginScopedToAnotherCompany_IsNotFoundForACompanyScopedCaller()
    {
        var w = await SeedAsync();
        var email = Email("scoped-elsewhere");
        var user = await AddUserAsync(w, email, ["Employee"], groupScope: false, grantCompany: w.CompanyB);
        var employeeId = await AddEmployeeAsync(w, w.CompanyA, email);

        await using (var db = _fixture.CreateRetryingDb())
            (await Controller(db, w, w.AdminId, scopedTo: w.CompanyA).LinkExistingLogin(new LinkExistingLoginRequest(employeeId, user, "link"), default))
                .Result.Should().BeOfType<NotFoundObjectResult>();
        await AssertNotLinkedAsync(user, employeeId);
    }

    [Fact]
    public async Task ALoginFromAnotherTenant_IsNotFound()
    {
        var w = await SeedAsync();
        var other = await SeedAsync();
        var email = Email("cross-tenant");
        var foreignUser = await AddUserAsync(other, email, ["Employee"], groupScope: false);
        var employeeId = await AddEmployeeAsync(w, w.CompanyA, email);

        await using (var db = _fixture.CreateRetryingDb())
            (await Controller(db, w, w.AdminId).LinkExistingLogin(new LinkExistingLoginRequest(employeeId, foreignUser, "link"), default))
                .Result.Should().BeOfType<NotFoundObjectResult>();
        await using (var db = _fixture.CreateRetryingDb())
        {
            // The status never surfaces another tenant's login, even with the same email.
            var status = Ok<EmployeeLoginStatusDto>((await Controller(db, w, w.AdminId).EmployeeLoginStatus(employeeId, default)).Result);
            status.NextAction.Should().Be(EmployeeLoginNextActions.Invite);
            status.MatchingLogin.Should().BeNull();
        }
        await AssertNotLinkedAsync(foreignUser, employeeId);
    }

    // ── Linking never widens a login's company scope ────────────────────────────────────────────────

    [Fact]
    public async Task ALoginWorkingInCompanyA_LinkedToAnEmployeeInCompanyB_IsRefused_AndWritesNothing()
    {
        var w = await SeedAsync();
        var email = Email("scoped-a");
        var user = await AddUserAsync(w, email, ["Employee"], groupScope: false, grantCompany: w.CompanyA);
        var employeeId = await AddEmployeeAsync(w, w.CompanyB, email);
        var stampBefore = await StampAsync(user);
        var auditBefore = await AuditIdsAsync(w);

        await using (var db = _fixture.CreateRetryingDb())
        {
            var status = Ok<EmployeeLoginStatusDto>((await Controller(db, w, w.AdminId).EmployeeLoginStatus(employeeId, default)).Result);
            status.NextAction.Should().Be(EmployeeLoginNextActions.Blocked);
            status.ReasonCode.Should().Be(EmployeeLinkRefusals.LoginOtherCompany);
            status.ReasonSubject.Should().Be("B");
            status.Reason.Should().Be("This login works in a different company. Give it access to B first, or link it from that company.");
        }
        await using (var db = _fixture.CreateRetryingDb())
        {
            var bad = Assert.IsType<BadRequestObjectResult>((await Controller(db, w, w.AdminId).LinkExistingLogin(
                new LinkExistingLoginRequest(employeeId, user, "link"), default)).Result);
            var body = JsonSerializer.SerializeToElement(bad.Value);
            body.GetProperty("message").GetString().Should().Be("This login works in a different company. Give it access to B first, or link it from that company.");
            body.GetProperty("code").GetString().Should().Be(EmployeeLinkRefusals.LoginOtherCompany);
            body.GetProperty("subject").GetString().Should().Be("B");
        }

        await AssertNotLinkedAsync(user, employeeId);
        await using var verify = _fixture.CreateRetryingDb();
        var grants = await verify.UserEntityAccesses.IgnoreQueryFilters().Where(x => x.UserId == user).ToListAsync();
        grants.Should().ContainSingle().Which.CompanyId.Should().Be(w.CompanyA, "the login's scope is never widened");
        (await RoleNamesAsync(user)).Should().Equal("Employee");
        (await StampAsync(user)).Should().Be(stampBefore, "nothing about the login changed");
        // Every audit row written since the attempt began: exactly the refusal.
        (await verify.AuditLogs.IgnoreQueryFilters().Where(x => x.TenantId == w.TenantId && !auditBefore.Contains(x.Id))
                .Select(x => x.Action).ToListAsync())
            .Should().Equal(["access.employee_login_link_refused"], "a refused link writes nothing but its refusal");
    }

    [Fact]
    public async Task AFreshLoginWithNoCompanyAccess_GetsExactlyOneGrant_ForTheEmployeesCompany()
    {
        var w = await SeedAsync();
        var email = Email("fresh");
        var user = await AddUserAsync(w, email, ["Employee"], groupScope: false);
        var employeeId = await AddEmployeeAsync(w, w.CompanyB, email);

        await using (var db = _fixture.CreateRetryingDb())
            (await Controller(db, w, w.AdminId).LinkExistingLogin(new LinkExistingLoginRequest(employeeId, user, "link"), default))
                .Result.Should().BeOfType<OkObjectResult>();

        await using var verify = _fixture.CreateRetryingDb();
        var grant = (await verify.UserEntityAccesses.IgnoreQueryFilters().Where(x => x.UserId == user).ToListAsync()).Should().ContainSingle().Subject;
        grant.CompanyId.Should().Be(w.CompanyB);
        grant.IsActive.Should().BeTrue();
        grant.GrantMode.Should().Be(EntityGrantModes.SelectedCompanies);
    }

    [Fact]
    public async Task AGroupScopeLogin_IsLinkedWithNoGrant()
    {
        var w = await SeedAsync();
        var email = Email("group");
        var user = await AddUserAsync(w, email, ["Employee"], groupScope: true);
        var employeeId = await AddEmployeeAsync(w, w.CompanyB, email);

        await using (var db = _fixture.CreateRetryingDb())
            (await Controller(db, w, w.AdminId).LinkExistingLogin(new LinkExistingLoginRequest(employeeId, user, "link"), default))
                .Result.Should().BeOfType<OkObjectResult>();

        await using var verify = _fixture.CreateRetryingDb();
        (await verify.UserEntityAccesses.IgnoreQueryFilters().AnyAsync(x => x.UserId == user)).Should().BeFalse();
        (await verify.EmployeeUserAccounts.IgnoreQueryFilters().CountAsync(x => x.UserId == user && !x.IsDeleted)).Should().Be(1);
    }

    [Fact]
    public async Task ALoginAlreadyInTheEmployeesCompany_IsLinkedWithNoNewGrant()
    {
        var w = await SeedAsync();
        var email = Email("same-company");
        var user = await AddUserAsync(w, email, ["Employee"], groupScope: false, grantCompany: w.CompanyA);
        var employeeId = await AddEmployeeAsync(w, w.CompanyA, email);

        await using (var db = _fixture.CreateRetryingDb())
            (await Controller(db, w, w.AdminId).LinkExistingLogin(new LinkExistingLoginRequest(employeeId, user, "link"), default))
                .Result.Should().BeOfType<OkObjectResult>();

        await using var verify = _fixture.CreateRetryingDb();
        (await verify.UserEntityAccesses.IgnoreQueryFilters().Where(x => x.UserId == user).ToListAsync())
            .Should().ContainSingle().Which.Role.Should().Be("HR", "the existing grant is the only one");
    }

    // ── Stranded and dormant links, legacy pointers ─────────────────────────────────────────────────

    [Theory]
    [InlineData("deleted")]
    [InlineData("merged")]
    public async Task ALiveLinkStrandedOnADeadEmployee_IsReused_AndTheDeadPointerCleared(string fate)
    {
        var w = await SeedAsync();
        var email = Email($"stranded-{fate}");
        var user = await AddUserAsync(w, email, ["Employee"], groupScope: false, grantCompany: w.CompanyA);
        var dead = await AddEmployeeAsync(w, w.CompanyA, Email("old-record"));
        var oldLinkId = await AddLinkAsync(w, dead, user);
        var survivor = await AddEmployeeAsync(w, w.CompanyA, email);
        await UpdateEmployeeAsync(dead, e =>
        {
            if (fate == "deleted") { e.IsDeleted = true; e.DeletedAtUtc = DateTime.UtcNow; }
            else e.DuplicateOfEmployeeId = survivor; // merged into the survivor, not (yet) soft-removed
        });

        await using (var db = _fixture.CreateRetryingDb())
        {
            var status = Ok<EmployeeLoginStatusDto>((await Controller(db, w, w.AdminId).EmployeeLoginStatus(survivor, default)).Result);
            status.NextAction.Should().Be(EmployeeLoginNextActions.LinkExisting, "a link stranded on a {0} employee is dormant", fate);
            status.MatchingLogin!.UserId.Should().Be(user);
        }
        await using (var db = _fixture.CreateRetryingDb())
            (await Controller(db, w, w.AdminId).LinkExistingLogin(new LinkExistingLoginRequest(survivor, user, "re-home"), default))
                .Result.Should().BeOfType<OkObjectResult>();

        await using var verify = _fixture.CreateRetryingDb();
        var rows = await verify.EmployeeUserAccounts.IgnoreQueryFilters().Where(x => x.UserId == user).ToListAsync();
        var row = rows.Should().ContainSingle().Subject;
        row.Id.Should().Be(oldLinkId, "the login's one row is reused");
        row.EmployeeId.Should().Be(survivor);
        row.IsDeleted.Should().BeFalse();
        (await verify.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == dead)).UserAccountId.Should().BeNull();
        (await verify.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == survivor)).UserAccountId.Should().Be(user);
        var audit = await verify.AuditLogs.IgnoreQueryFilters().SingleAsync(x => x.TenantId == w.TenantId && x.Action == "access.employee_login_linked");
        using var meta = JsonDocument.Parse(audit.Metadata!);
        meta.RootElement.GetProperty("previousEmployeeId").GetInt32().Should().Be(dead);
        meta.RootElement.GetProperty("previousPointerCleared").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ASoftDeletedLinkRow_IsReused_AndALivingPreviousEmployeeIsNotTouched()
    {
        var w = await SeedAsync();
        var email = Email("dormant");
        var user = await AddUserAsync(w, email, ["Employee"], groupScope: false, grantCompany: w.CompanyA);
        var previous = await AddEmployeeAsync(w, w.CompanyA, Email("previous"));
        var oldLinkId = await AddLinkAsync(w, previous, user, deleted: true); // unlinked earlier; previous no longer names the login
        var employeeId = await AddEmployeeAsync(w, w.CompanyA, email);

        await using (var db = _fixture.CreateRetryingDb())
            (await Controller(db, w, w.AdminId).LinkExistingLogin(new LinkExistingLoginRequest(employeeId, user, "link"), default))
                .Result.Should().BeOfType<OkObjectResult>();

        await using var verify = _fixture.CreateRetryingDb();
        var row = (await verify.EmployeeUserAccounts.IgnoreQueryFilters().Where(x => x.UserId == user).ToListAsync()).Should().ContainSingle().Subject;
        row.Id.Should().Be(oldLinkId);
        row.EmployeeId.Should().Be(employeeId);
        row.IsDeleted.Should().BeFalse();
        var prev = await verify.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == previous);
        prev.IsDeleted.Should().BeFalse();
        prev.UserAccountId.Should().BeNull();
        var audit = await verify.AuditLogs.IgnoreQueryFilters().SingleAsync(x => x.TenantId == w.TenantId && x.Action == "access.employee_login_linked");
        using var meta = JsonDocument.Parse(audit.Metadata!);
        meta.RootElement.GetProperty("previousEmployeeId").GetInt32().Should().Be(previous);
        meta.RootElement.GetProperty("previousPointerCleared").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task ALivingEmployeeStillNamingTheLogin_IsNeverClearedSilently()
    {
        var w = await SeedAsync();
        var email = Email("legacy-pointer");
        var user = await AddUserAsync(w, email, ["Employee"], groupScope: false, grantCompany: w.CompanyA);
        // A legacy pointer with no link row (and a soft-deleted link row to it, the shape the old flows left).
        var holder = await AddEmployeeAsync(w, w.CompanyA, Email("holder"), fullName: "Sara Holder", code: "EMP-HOLD");
        await AddLinkAsync(w, holder, user, deleted: true, keepPointer: true);
        var employeeId = await AddEmployeeAsync(w, w.CompanyA, email);

        const string expected = "This login is still recorded on Sara Holder (EMP-HOLD)'s employee record. Contact support to resolve it.";
        await using (var db = _fixture.CreateRetryingDb())
        {
            var status = Ok<EmployeeLoginStatusDto>((await Controller(db, w, w.AdminId).EmployeeLoginStatus(employeeId, default)).Result);
            status.NextAction.Should().Be(EmployeeLoginNextActions.Blocked);
            status.ReasonCode.Should().Be(EmployeeLinkRefusals.Pointer);
            status.ReasonSubject.Should().Be("Sara Holder (EMP-HOLD)");
            status.Reason.Should().Be(expected);
        }
        await using (var db = _fixture.CreateRetryingDb())
        {
            var bad = Assert.IsType<BadRequestObjectResult>((await Controller(db, w, w.AdminId).LinkExistingLogin(
                new LinkExistingLoginRequest(employeeId, user, "link"), default)).Result);
            Message(bad).Should().Be(expected);
        }
        await AssertNotLinkedAsync(user, employeeId);
        await using var verify = _fixture.CreateRetryingDb();
        (await verify.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == holder)).UserAccountId.Should().Be(user, "a living record's pointer is never cleared silently");
        (await verify.EmployeeUserAccounts.IgnoreQueryFilters().SingleAsync(x => x.UserId == user)).IsDeleted.Should().BeTrue();
    }

    // ── Group-level decisions, and what a scoped administrator may see ──────────────────────────────

    [Fact]
    public async Task ACompanyScopedAdmin_CannotTakeALoginWithNoCompanyAccessIntoTheirCompany()
    {
        var w = await SeedAsync();
        var email = Email("zero-grant");
        var user = await AddUserAsync(w, email, ["Employee"], groupScope: false);
        var employeeId = await AddEmployeeAsync(w, w.CompanyB, email);

        await using (var db = _fixture.CreateRetryingDb())
        {
            var status = Ok<EmployeeLoginStatusDto>((await Controller(db, w, w.AdminId, scopedTo: w.CompanyB).EmployeeLoginStatus(employeeId, default)).Result);
            status.NextAction.Should().Be(EmployeeLoginNextActions.Blocked);
            status.ReasonCode.Should().Be(EmployeeLinkRefusals.NeedsGroupAdmin);
            status.MatchingLogin.Should().BeNull();
        }
        await using (var db = _fixture.CreateRetryingDb())
        {
            var refused = Assert.IsType<ObjectResult>((await Controller(db, w, w.AdminId, scopedTo: w.CompanyB).LinkExistingLogin(
                new LinkExistingLoginRequest(employeeId, user, "link"), default)).Result);
            refused.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
            Message(refused).Should().Be("Only a group-level administrator can link a login that has no company access yet.");
        }
        await AssertNotLinkedAsync(user, employeeId);
        await using var verify = _fixture.CreateRetryingDb();
        (await verify.UserEntityAccesses.IgnoreQueryFilters().AnyAsync(x => x.UserId == user)).Should().BeFalse();
        (await RoleNamesAsync(user)).Should().Equal("Employee");
    }

    [Fact]
    public async Task TheStatus_DescribesNothingAboutALoginOutsideTheCallersAccess()
    {
        var w = await SeedAsync();
        var email = Email("outside");
        var user = await AddUserAsync(w, email, ["Employee"], groupScope: false, grantCompany: w.CompanyB);
        var other = await AddEmployeeAsync(w, w.CompanyB, Email("its-record"));
        await AddLinkAsync(w, other, user);
        var employeeId = await AddEmployeeAsync(w, w.CompanyA, email);

        await using var db = _fixture.CreateRetryingDb();
        var status = Ok<EmployeeLoginStatusDto>((await Controller(db, w, w.AdminId, scopedTo: w.CompanyA).EmployeeLoginStatus(employeeId, default)).Result);
        status.NextAction.Should().Be(EmployeeLoginNextActions.Blocked);
        status.MatchingLogin.Should().BeNull();
        status.ReasonCode.Should().Be(EmployeeLinkRefusals.NotManageableCode);
        status.Reason.Should().NotContain("another employee").And.NotContain(user.ToString());
        JsonSerializer.Serialize(status).Should().NotContain(user.ToString());
    }

    [Fact]
    public async Task TheStatus_ShowsAnInScopeEmployeesLinkedLogin_ExactlyWhenUserManagementListsIt()
    {
        // A login linked to an employee in the caller's company is inside the caller's user scope
        // (ApplyEntityScope), so the status may name it: it reveals nothing the user list does not.
        var w = await SeedAsync();
        var email = Email("group-linked");
        var user = await AddUserAsync(w, email, ["Employee"], groupScope: true);
        var employeeId = await AddEmployeeAsync(w, w.CompanyA, email);
        await AddLinkAsync(w, employeeId, user);
        var scope = EntityScopeContext.ForCompanies(new[] { w.CompanyA });

        await using var db = _fixture.CreateRetryingDb();
        (await db.Users.AsNoTracking().ApplyEntityScope(db, w.TenantId, scope).AnyAsync(x => x.Id == user))
            .Should().BeTrue("the user list shows a login linked to an in-scope employee");
        var status = Ok<EmployeeLoginStatusDto>((await Controller(db, w, w.AdminId, scopedTo: w.CompanyA).EmployeeLoginStatus(employeeId, default)).Result);
        status.NextAction.Should().Be(EmployeeLoginNextActions.Linked);
        status.LinkedLogin!.UserId.Should().Be(user);
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("sso")]
    public async Task AScopedAdmin_LearnsNothingAboutALoginWithNoCompanyAccess_WhateverItsState(string state)
    {
        var w = await SeedAsync();
        var email = Email($"zero-{state}");
        var user = await AddUserAsync(w, email, ["Employee"], groupScope: false);
        var employeeId = await AddEmployeeAsync(w, w.CompanyB, email);
        await using (var seed = _fixture.CreateRetryingDb())
        {
            var u = await seed.Users.IgnoreQueryFilters().SingleAsync(x => x.Id == user);
            if (state == "locked") { u.IsLocked = true; u.Status = "Locked"; }
            else u.IdentityProvider = "AzureAD";
            await seed.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateRetryingDb())
        {
            var status = Ok<EmployeeLoginStatusDto>((await Controller(db, w, w.AdminId, scopedTo: w.CompanyB).EmployeeLoginStatus(employeeId, default)).Result);
            status.ReasonCode.Should().Be(EmployeeLinkRefusals.NeedsGroupAdmin, "the group-level rule answers before any check that describes the login");
            status.MatchingLogin.Should().BeNull();
            var json = JsonSerializer.Serialize(status);
            json.Should().NotContain(user.ToString()).And.NotContain("Locked").And.NotContain("single sign-on");
        }
        await using (var db = _fixture.CreateRetryingDb())
        {
            var refused = Assert.IsType<ObjectResult>((await Controller(db, w, w.AdminId, scopedTo: w.CompanyB).LinkExistingLogin(
                new LinkExistingLoginRequest(employeeId, user, "link"), default)).Result);
            refused.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
            JsonSerializer.SerializeToElement(refused.Value).GetProperty("code").GetString().Should().Be(EmployeeLinkRefusals.NeedsGroupAdmin);
        }
        await AssertNotLinkedAsync(user, employeeId);
    }

    [Fact]
    public async Task APointerHolderOutsideTheCallersCompanies_IsNotNamed()
    {
        var w = await SeedAsync();
        var email = Email("holder-elsewhere");
        var user = await AddUserAsync(w, email, ["Employee"], groupScope: false, grantCompany: w.CompanyA);
        var holder = await AddEmployeeAsync(w, w.CompanyB, Email("holder-b"), fullName: "Hidden Holder", code: "EMP-HIDDEN");
        await AddLinkAsync(w, holder, user, deleted: true, keepPointer: true);
        var employeeId = await AddEmployeeAsync(w, w.CompanyA, email);

        const string generic = "This login is still recorded on another employee record. Contact support to resolve it.";
        await using (var db = _fixture.CreateRetryingDb())
        {
            var status = Ok<EmployeeLoginStatusDto>((await Controller(db, w, w.AdminId, scopedTo: w.CompanyA).EmployeeLoginStatus(employeeId, default)).Result);
            status.ReasonCode.Should().Be(EmployeeLinkRefusals.Pointer);
            status.ReasonSubject.Should().BeNull();
            status.Reason.Should().Be(generic);
            JsonSerializer.Serialize(status).Should().NotContain("Hidden Holder").And.NotContain("EMP-HIDDEN");
        }
        await using (var db = _fixture.CreateRetryingDb())
        {
            var bad = Assert.IsType<BadRequestObjectResult>((await Controller(db, w, w.AdminId, scopedTo: w.CompanyA).LinkExistingLogin(
                new LinkExistingLoginRequest(employeeId, user, "link"), default)).Result);
            Message(bad).Should().Be(generic);
            JsonSerializer.Serialize(bad.Value).Should().NotContain("Hidden Holder");
        }
        await AssertNotLinkedAsync(user, employeeId);
    }

    [Fact]
    public async Task AFailingRefusalAudit_NeverMasksTheRefusal()
    {
        var w = await SeedAsync();
        var email = Email("audit-fails");
        var user = await AddUserAsync(w, email, ["Employee"], groupScope: false);
        var mismatched = await AddEmployeeAsync(w, w.CompanyA, Email("not-the-login"));

        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fixture.ConnectionString, PostgresFixture.ProductionProviderOptions)
            .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance)
            .AddInterceptors(Zayra.Api.Infrastructure.Data.AdvisoryXactLockGuardInterceptor.Instance)
            .AddInterceptors(new FailRefusalAuditInterceptor())
            .Options;
        await using (var db = new ZayraDbContext(options))
        {
            var bad = Assert.IsType<BadRequestObjectResult>((await Controller(db, w, w.AdminId).LinkExistingLogin(
                new LinkExistingLoginRequest(mismatched, user, "link"), default)).Result);
            JsonSerializer.SerializeToElement(bad.Value).GetProperty("code").GetString().Should().Be(EmployeeLinkRefusals.Email);
        }

        await using var verify = _fixture.CreateRetryingDb();
        (await verify.AuditLogs.IgnoreQueryFilters().AnyAsync(x => x.TenantId == w.TenantId && x.Action == "access.employee_login_link_refused"))
            .Should().BeFalse("the injected failure really stopped the audit save");
        await AssertNotLinkedAsync(user, mismatched);
    }

    private sealed class FailRefusalAuditInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<AuditLog>()
                .Any(e => e.State == EntityState.Added && e.Entity.Action == "access.employee_login_link_refused"))
                throw new DbUpdateException("injected audit failure");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    // ── Every refusal is audited, with ids only ─────────────────────────────────────────────────────

    [Fact]
    public async Task RefusedLinks_AreAudited_WithTheirCode()
    {
        var w = await SeedAsync();
        var email = Email("audited");
        var user = await AddUserAsync(w, email, ["Employee"], groupScope: false);
        var mismatched = await AddEmployeeAsync(w, w.CompanyA, Email("not-the-login"));

        await using (var db = _fixture.CreateRetryingDb())
            Assert.IsType<BadRequestObjectResult>((await Controller(db, w, w.AdminId).LinkExistingLogin(
                new LinkExistingLoginRequest(mismatched, user, "link"), default)).Result);
        await using (var db = _fixture.CreateRetryingDb())
            Assert.IsType<NotFoundObjectResult>((await Controller(db, w, w.AdminId).LinkExistingLogin(
                new LinkExistingLoginRequest(int.MaxValue, user, "link"), default)).Result);

        await using var verify = _fixture.CreateRetryingDb();
        var rows = await verify.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == w.TenantId && x.Action == "access.employee_login_link_refused")
            .OrderBy(x => x.CreatedAtUtc).ToListAsync();
        rows.Should().HaveCount(2);
        rows.Select(x => x.UserId).Should().AllBeEquivalentTo(w.AdminId);
        var codes = rows.Select(x => JsonDocument.Parse(x.Metadata!).RootElement.GetProperty("code").GetString()).ToList();
        codes.Should().BeEquivalentTo([EmployeeLinkRefusals.Email, AccessTargetNotFoundException.EmployeeNotFound]);
        rows.Should().OnlyContain(x => !x.Metadata!.Contains(email), "ids and the code only");
        (await verify.EmployeeUserAccounts.IgnoreQueryFilters().AnyAsync(x => x.UserId == user)).Should().BeFalse();
    }

    // ── One login racing two employees ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task OneLoginRacingTwoEmployees_ExactlyOneLinkSurvives()
    {
        var w = await SeedAsync();
        var email = Email("two-records");
        var user = await AddUserAsync(w, email, ["Employee"], groupScope: false, grantCompany: w.CompanyA);
        var first = await AddEmployeeAsync(w, w.CompanyA, email);
        var second = await AddEmployeeAsync(w, w.CompanyA, email); // work email is free text and may repeat

        async Task<IActionResult?> LinkAsync(int employeeId)
        {
            await using var db = _fixture.CreateRetryingDb();
            return (await Controller(db, w, w.AdminId).LinkExistingLogin(new LinkExistingLoginRequest(employeeId, user, "race"), default)).Result;
        }

        var results = await Task.WhenAll(new[] { first, second, first, second }.Select(id => Task.Run(() => LinkAsync(id))));
        results.Should().Contain(r => r is OkObjectResult);
        results.Should().OnlyContain(r => r is OkObjectResult || r is BadRequestObjectResult);

        await using var verify = _fixture.CreateRetryingDb();
        var live = await verify.EmployeeUserAccounts.IgnoreQueryFilters().Where(x => x.UserId == user && !x.IsDeleted).ToListAsync();
        var winner = live.Should().ContainSingle().Subject.EmployeeId;
        winner.Should().BeOneOf(first, second);
        var pointers = await verify.Employees.IgnoreQueryFilters().Where(x => x.UserAccountId == user).Select(x => x.Id).ToListAsync();
        pointers.Should().Equal(winner);
        (await verify.AuditLogs.IgnoreQueryFilters().CountAsync(x => x.TenantId == w.TenantId && x.Action == "access.employee_login_linked")).Should().Be(1);
    }

    [Fact]
    public async Task AReasonIsRequired()
    {
        var w = await SeedAsync();
        var email = Email("no-reason");
        var user = await AddUserAsync(w, email, ["Employee"], groupScope: false);
        var employeeId = await AddEmployeeAsync(w, w.CompanyA, email);
        await using var db = _fixture.CreateRetryingDb();
        Message(Assert.IsType<BadRequestObjectResult>((await Controller(db, w, w.AdminId).LinkExistingLogin(
            new LinkExistingLoginRequest(employeeId, user, "   "), default)).Result)).Should().Contain("reason");
        await AssertNotLinkedAsync(user, employeeId);
    }

    // ── P0: the two-person rule — whoever held a credential, or last changed the work email, never links ──

    private const string OwnersOwnPassword = "OwnersOwn1!Password";
    private const string PersonsNewPassword = "PersonsNew2!Password";

    /// <summary>
    /// THE TAKEOVER. Admin X makes new.hire@ in Create User with a password X knows, and links it to the new hire's
    /// employee record; X now signs in as that employee (payslips, IBAN, leave, loans, approvals). The creator is a
    /// credential handler and never links it; a different administrator may.
    /// </summary>
    [Fact]
    public async Task ACreateUserLogin_IsNeverLinkedByItsCreator_ButAnotherAdministratorMayLinkIt()
    {
        var w = await SeedAsync();
        var creator = await AddUserAsync(w, Email("creator"), ["Admin"], groupScope: true);
        var email = Email("new.hire");
        Guid login;
        await using (var db = _fixture.CreateRetryingDb())
        {
            var created = (await Controller(db, w, creator).CreateUser(new CreateUserRequest(email, "New Hire", Password, ["Reporting"]), default)).Result;
            login = Assert.IsType<AuthUserDto>(Assert.IsType<CreatedAtActionResult>(created).Value).Id;
        }
        var employeeId = await AddEmployeeAsync(w, w.CompanyA, email);

        await AssertCredentialHandlerRefusedAsync(w, creator, login, employeeId);
        (await SignInAsync(w, email)).Should().NotBeNull("the creator knows the password they chose");
        var refreshTokenId = await AddRefreshTokenAsync(login);

        // ...and, signed in as it, the creator enrolled their own authenticator on it.
        Guid challengeId;
        await using (var db = _fixture.CreateRetryingDb())
        {
            var u = await db.Users.IgnoreQueryFilters().SingleAsync(x => x.Id == login);
            u.MFAEnabled = true;
            u.MfaSecretEncrypted = "creator-enrolled-secret";
            u.MfaConfiguredAtUtc = DateTime.UtcNow.AddMinutes(-10);
            u.MfaLastVerifiedAtUtc = DateTime.UtcNow.AddMinutes(-5);
            u.MfaLastTotpStep = 123456;
            u.MfaFailedCount = 1;
            var challenge = new MfaChallengeToken
            {
                UserId = login, TenantId = w.TenantId, TokenHash = Guid.NewGuid().ToString("N"), ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5),
            };
            db.MfaChallengeTokens.Add(challenge);
            await db.SaveChangesAsync();
            challengeId = challenge.Id;
        }

        EmployeeLoginLinkResultDto linked;
        await using (var db = _fixture.CreateRetryingDb())
        {
            var status = Ok<EmployeeLoginStatusDto>((await Controller(db, w, w.AdminId).EmployeeLoginStatus(employeeId, default)).Result);
            status.NextAction.Should().Be(EmployeeLoginNextActions.LinkExisting);
            status.WillResetCredential.Should().BeTrue("the screen says the password will be reset before anyone links");
            linked = Ok<EmployeeLoginLinkResultDto>((await Controller(db, w, w.AdminId).LinkExistingLogin(
                new LinkExistingLoginRequest(employeeId, login, "Linked by a second administrator"), default)).Result);
        }
        await using (var verify = _fixture.CreateRetryingDb())
        {
            // The creator's authenticator went with the password.
            var u = await verify.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == login);
            u.MFAEnabled.Should().BeFalse();
            u.MfaSecretEncrypted.Should().BeNull();
            u.MfaConfiguredAtUtc.Should().BeNull();
            u.MfaLastVerifiedAtUtc.Should().BeNull();
            u.MfaLastTotpStep.Should().BeNull();
            u.MfaFailedCount.Should().Be(0);
            (await verify.MfaChallengeTokens.IgnoreQueryFilters().SingleAsync(x => x.Id == challengeId)).UsedAtUtc.Should().NotBeNull();
            (await verify.AuditLogs.IgnoreQueryFilters().SingleAsync(x => x.Action == AccessManagementService.LinkCredentialResetAction
                && x.EntityId == login.ToString())).Metadata.Should().Contain("\"mfaCleared\":true");
        }
        // Accepting the invitation, the person signs straight in with their own password: no prompt for the old TOTP.
        await AssertCredentialRotatedAsync(w, linked, login, email, refreshTokenId, oldPassword: Password, linker: w.AdminId, accessMode: AccessModes.FullPortal);
    }

    /// <summary>
    /// After a link that rotated the credential: the old password is dead everywhere, sessions are gone, the person
    /// sets their own from the fresh invitation (here handed to the linker, no mail transport), and then signs in
    /// with their own password, employee_id and original access mode intact.
    /// </summary>
    private async Task AssertCredentialRotatedAsync(World w, EmployeeLoginLinkResultDto linked, Guid login, string email, Guid refreshTokenId,
        string oldPassword, Guid linker, string accessMode)
    {
        linked.UserId.Should().Be(login);
        linked.CredentialReset.Should().BeTrue();
        linked.IsActive.Should().BeFalse();
        linked.Status.Should().Be("Invited");
        linked.EmailSent.Should().BeFalse();
        linked.InvitationUrl.Should().Contain("/accept-invitation").And.Contain("#token=");
        linked.DeliveryMessage.Should().Contain("No email delivery is configured");

        await SignInFailsAsync(w, email, oldPassword);
        await using (var verify = _fixture.CreateRetryingDb())
        {
            (await verify.RefreshTokens.SingleAsync(x => x.Id == refreshTokenId)).RevokedAtUtc.Should().NotBeNull();
            var link = await verify.EmployeeUserAccounts.IgnoreQueryFilters().SingleAsync(x => x.UserId == login && !x.IsDeleted);
            link.RequiresPasswordSetup.Should().BeTrue();
            link.InvitationExpiresAtUtc.Should().BeCloseTo(DateTime.UtcNow.AddHours(72), TimeSpan.FromMinutes(5));
            link.AccessMode.Should().Be(accessMode, "the access mode is kept for when the person accepts");
            // Not merely switched off: the old password no longer matches the stored credential at all, so no later
            // reactivation path can bring it back.
            var rotated = await verify.Users.IgnoreQueryFilters().SingleAsync(x => x.Id == login);
            new Pbkdf2PasswordHasher().Verify(oldPassword, rotated.PasswordHash).Should().BeFalse();
            rotated.IsActive.Should().BeFalse();
            (await verify.AuditLogs.IgnoreQueryFilters().CountAsync(x => x.Action == AccessManagementService.LinkCredentialResetAction
                && x.EntityId == login.ToString())).Should().Be(1);
            (await verify.AuditLogs.IgnoreQueryFilters().AnyAsync(x => x.Action == AccessManagementService.InvitationLinkDisclosedAction
                && x.EntityId == login.ToString() && x.UserId == linker)).Should().BeTrue("the linker was handed a credential");
        }

        var token = Uri.UnescapeDataString(linked.InvitationUrl![(linked.InvitationUrl.IndexOf("#token=", StringComparison.Ordinal) + "#token=".Length)..]);
        await using (var db = _fixture.CreateRetryingDb())
            await Auth(db).AcceptInvitationAsync(new AcceptInvitationRequest(token, PersonsNewPassword, w.Slug), new RequestContext("127.0.0.1", "tests"), default);
        await SignInFailsAsync(w, email, oldPassword);
        var principal = await SignInAsync(w, email, PersonsNewPassword);
        principal.FindFirstValue("employee_id").Should().Be(linked.EmployeeId.ToString());
        principal.FindFirstValue("access_mode").Should().Be(accessMode);
    }

    private async Task SignInFailsAsync(World w, string email, string password)
    {
        await using var db = _fixture.CreateRetryingDb();
        AuthLoginResult? response = null;
        try { response = await Auth(db).LoginAsync(new LoginRequest(email, password, w.Slug), new RequestContext("127.0.0.1", "tests"), default); }
        catch (UnauthorizedAccessException) { return; }
        catch (InvalidOperationException) { return; }
        response!.Tokens.Should().BeNull("a password someone else knew must not sign in after the link");
    }

    /// <summary>
    /// With no mail transport the reset link comes back to the administrator — they held a live credential. Even
    /// after the person redeemed it, that administrator never links the login; a different one may.
    /// </summary>
    [Fact]
    public async Task AnAdministratorShownAResetLink_NeverLinksTheLogin_EvenAfterItIsRedeemed()
    {
        var w = await SeedAsync();
        var peer = await AddUserAsync(w, Email("peer"), ["Admin"], groupScope: true);
        var email = Email("reset-seen");
        var login = await AddUserAsync(w, email, ["Reporting"], groupScope: false, password: Password);
        var employeeId = await AddEmployeeAsync(w, w.CompanyA, email);

        string resetUrl;
        await using (var db = _fixture.CreateRetryingDb())
            resetUrl = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(
                await Controller(db, w, w.AdminId).IssuePasswordResetLink(login, default)).Value).GetProperty("resetUrl").GetString()!;
        var token = Uri.UnescapeDataString(resetUrl[(resetUrl.IndexOf("#token=", StringComparison.Ordinal) + "#token=".Length)..]);
        await using (var db = _fixture.CreateRetryingDb())
            await Auth(db).ResetPasswordAsync(new ResetPasswordRequest(token, OwnersOwnPassword, w.Slug), new RequestContext("127.0.0.1", "tests"), default);

        await AssertCredentialHandlerRefusedAsync(w, w.AdminId, login, employeeId);
        var refreshTokenId = await AddRefreshTokenAsync(login);
        EmployeeLoginLinkResultDto linked;
        await using (var db = _fixture.CreateRetryingDb())
            linked = Ok<EmployeeLoginLinkResultDto>((await Controller(db, w, peer).LinkExistingLogin(
                new LinkExistingLoginRequest(employeeId, login, "Linked by an administrator who never saw the link"), default)).Result);
        // The redeemed password is rotated too: the administrator who saw the link was a handler.
        await AssertCredentialRotatedAsync(w, linked, login, email, refreshTokenId, oldPassword: OwnersOwnPassword, linker: peer, accessMode: AccessModes.FullPortal);
    }

    [Fact]
    public async Task AnAdministratorWhoSetThePassword_NeverLinksTheLogin()
    {
        var w = await SeedAsync();
        var email = Email("admin-reset");
        var login = await AddUserAsync(w, email, ["Reporting"], groupScope: false);
        var employeeId = await AddEmployeeAsync(w, w.CompanyA, email);
        await using (var db = _fixture.CreateRetryingDb())
            Ok<EmployeeLoginStatusDto>((await Controller(db, w, w.AdminId).EmployeeLoginStatus(employeeId, default)).Result)
                .NextAction.Should().Be(EmployeeLoginNextActions.LinkExisting);

        // The (retired, still callable) administrator reset: the administrator chose the password.
        await using (var db = _fixture.CreateRetryingDb())
        {
            var controller = Controller(db, w, w.AdminId);
            var service = new AccessManagementService(db, new Pbkdf2PasswordHasher(), new AuditService(db), new JwtTokenService(Jwt));
            await service.AdminResetPasswordAsync(w.TenantId, login, new AdminResetPasswordRequest(OwnersOwnPassword, MustChangePassword: false),
                EntityScopeContext.FromClaims(controller.User), new RequestContext("127.0.0.1", "tests", w.AdminId, w.TenantId), default);
        }

        await AssertCredentialHandlerRefusedAsync(w, w.AdminId, login, employeeId);
    }

    [Fact]
    public async Task AnAdministratorShownAnInvitationLink_IsACredentialHandler()
    {
        var w = await SeedAsync();
        var email = Email("invite-seen");
        var login = await AddUserAsync(w, email, ["Reporting"], groupScope: false);
        var employeeId = await AddEmployeeAsync(w, w.CompanyA, email);
        await using (var db = _fixture.CreateRetryingDb())
        {
            db.AuditLogs.Add(new AuditLog
            {
                TenantId = w.TenantId, UserId = w.AdminId, Action = AccessManagementService.InvitationLinkDisclosedAction,
                EntityName = "User", EntityId = login.ToString(), CreatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        await AssertCredentialHandlerRefusedAsync(w, w.AdminId, login, employeeId);
    }

    /// <summary>
    /// The work email is the identity evidence a link rests on. Whoever last changed it never links the login it now
    /// matches — and no login may be linked on a work email it changed itself.
    /// </summary>
    [Theory]
    [InlineData("caller")]
    [InlineData("login")]
    public async Task WhoeverLastChangedTheWorkEmail_CannotBeAPartyToTheLink(string changedBy)
    {
        var w = await SeedAsync();
        var peer = await AddUserAsync(w, Email("peer"), ["Admin"], groupScope: true);
        var email = Email("repointed");
        var login = await AddUserAsync(w, email, ["Reporting"], groupScope: false);
        var employeeId = await AddEmployeeAsync(w, w.CompanyA, email);
        var actor = changedBy == "caller" ? w.AdminId : login;
        await using (var db = _fixture.CreateRetryingDb())
        {
            db.AuditLogs.Add(new AuditLog
            {
                TenantId = w.TenantId, UserId = peer, Action = AccessManagementService.WorkEmailChangedAction,
                EntityName = "Employee", EntityId = employeeId.ToString(), CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            });
            db.AuditLogs.Add(new AuditLog
            {
                TenantId = w.TenantId, UserId = actor, Action = AccessManagementService.WorkEmailChangedAction,
                EntityName = "Employee", EntityId = employeeId.ToString(), CreatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateRetryingDb())
        {
            var status = Ok<EmployeeLoginStatusDto>((await Controller(db, w, w.AdminId).EmployeeLoginStatus(employeeId, default)).Result);
            status.NextAction.Should().Be(EmployeeLoginNextActions.Blocked);
            status.ReasonCode.Should().Be(EmployeeLinkRefusals.WorkEmailParty);
            var refused = Assert.IsType<ObjectResult>((await Controller(db, w, w.AdminId).LinkExistingLogin(
                new LinkExistingLoginRequest(employeeId, login, "link"), default)).Result);
            refused.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
            JsonSerializer.SerializeToElement(refused.Value).GetProperty("code").GetString().Should().Be(EmployeeLinkRefusals.WorkEmailParty);
        }
        await AssertNotLinkedAsync(login, employeeId);
        await AssertRefusalAuditedAsync(w, employeeId, EmployeeLinkRefusals.WorkEmailParty);

        // Only the LAST change counts: the peer changed it earlier, which is not a bar to them...
        await using (var db = _fixture.CreateRetryingDb())
        {
            var status = Ok<EmployeeLoginStatusDto>((await Controller(db, w, peer).EmployeeLoginStatus(employeeId, default)).Result);
            if (changedBy == "caller")
                status.NextAction.Should().Be(EmployeeLoginNextActions.LinkExisting);
            else
                // ...but a login that repointed its own employee's work email is never linked, by anyone.
                status.ReasonCode.Should().Be(EmployeeLinkRefusals.WorkEmailParty);
        }
    }

    /// <summary>Status and write agree: blocked with the coded refusal, a 403 on the write, nothing linked, the refusal audited.</summary>
    private async Task AssertCredentialHandlerRefusedAsync(World w, Guid caller, Guid login, int employeeId)
    {
        await using (var db = _fixture.CreateRetryingDb())
        {
            var status = Ok<EmployeeLoginStatusDto>((await Controller(db, w, caller).EmployeeLoginStatus(employeeId, default)).Result);
            status.NextAction.Should().Be(EmployeeLoginNextActions.Blocked);
            status.ReasonCode.Should().Be(EmployeeLinkRefusals.CredentialHandled);
        }
        await using (var db = _fixture.CreateRetryingDb())
        {
            var refused = Assert.IsType<ObjectResult>((await Controller(db, w, caller).LinkExistingLogin(
                new LinkExistingLoginRequest(employeeId, login, "I handled it"), default)).Result);
            refused.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
            JsonSerializer.SerializeToElement(refused.Value).GetProperty("code").GetString().Should().Be(EmployeeLinkRefusals.CredentialHandled);
        }
        await AssertNotLinkedAsync(login, employeeId);
        await AssertRefusalAuditedAsync(w, employeeId, EmployeeLinkRefusals.CredentialHandled);
    }

    private async Task<HashSet<Guid>> AuditIdsAsync(World w)
    {
        await using var db = _fixture.CreateRetryingDb();
        return (await db.AuditLogs.IgnoreQueryFilters().AsNoTracking().Where(x => x.TenantId == w.TenantId).Select(x => x.Id).ToListAsync()).ToHashSet();
    }

    private async Task AssertRefusalAuditedAsync(World w, int employeeId, string code)
    {
        await using var verify = _fixture.CreateRetryingDb();
        (await verify.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.TenantId == w.TenantId && x.Action == "access.employee_login_link_refused" && x.EntityId == employeeId.ToString())
                .Select(x => x.Metadata)
                .ToListAsync())
            .Should().Contain(m => m != null && m.Contains($"\"code\":\"{code}\""));
    }

    private static AuthService Auth(ZayraDbContext db) =>
        new(db, new Pbkdf2PasswordHasher(), new JwtTokenService(Jwt), new AuditService(db), new FakeEmailService(), Jwt,
            new NullMfaService(), new TotpService(DataProtectionProvider.Create("ZayraTests")), NullLogger<AuthService>.Instance);

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────

    private static T Ok<T>(IActionResult? result) => (T)Assert.IsType<OkObjectResult>(result).Value!;

    private static string Message(ObjectResult result) =>
        JsonSerializer.SerializeToElement(result.Value).GetProperty("message").GetString() ?? string.Empty;

    private static JsonElement AssertRefused(IActionResult? result, string code)
    {
        var refused = Assert.IsType<ObjectResult>(result);
        refused.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        var body = JsonSerializer.SerializeToElement(refused.Value);
        body.GetProperty("error").GetString().Should().Be(code);
        body.GetProperty("messageAr").GetString().Should().NotBeNullOrWhiteSpace();
        return body;
    }

    private async Task AssertNotLinkedAsync(Guid userId, int employeeId)
    {
        await using var verify = _fixture.CreateRetryingDb();
        (await verify.EmployeeUserAccounts.IgnoreQueryFilters().AnyAsync(x => x.UserId == userId && x.EmployeeId == employeeId && !x.IsDeleted))
            .Should().BeFalse();
        (await verify.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == employeeId)).UserAccountId.Should().NotBe(userId);
        // Metadata is a json column; compare in memory.
        (await verify.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Action == "access.employee_login_linked")
                .Select(x => x.Metadata)
                .ToListAsync())
            .Should().NotContain(m => m != null && m.Contains(userId.ToString()));
    }

    private static string Email(string local) => $"{local}-{Guid.NewGuid():N}@kkdemo.test";

    private static AccessController Controller(ZayraDbContext db, World w, Guid callerId, Guid? scopedTo = null)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", w.TenantId.ToString()),
            new(ClaimTypes.NameIdentifier, callerId.ToString()),
            new("permission", "security.manage"),
            scopedTo is Guid company
                ? new(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { company } }))
                : new(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
        };
        var service = new AccessManagementService(db, new Pbkdf2PasswordHasher(), new AuditService(db), new JwtTokenService(Jwt));
        return new AccessController(service, db, new FakeEmailService())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
            },
        };
    }

    private static readonly IOptions<JwtOptions> Jwt = Options.Create(new JwtOptions
    {
        Issuer = "Zayra.Tests",
        TenantAudience = "kynexone-tenant-test",
        PlatformAudience = "kynexone-platform-test",
        SigningKey = "TEST_SIGNING_KEY_WITH_MORE_THAN_64_CHARACTERS_FOR_EMPLOYEE_LINK_TESTS",
        AccessTokenMinutes = 30,
        RefreshTokenDays = 7,
    });

    /// <summary>A real password sign-in through AuthService; returns the access token's claims.</summary>
    private Task<ClaimsPrincipal> SignInAsync(World w, string email) => SignInAsync(w, email, Password);

    private async Task<ClaimsPrincipal> SignInAsync(World w, string email, string password)
    {
        await using var db = _fixture.CreateRetryingDb();
        var auth = new AuthService(db, new Pbkdf2PasswordHasher(), new JwtTokenService(Jwt), new AuditService(db), new FakeEmailService(), Jwt,
            new NullMfaService(), new TotpService(DataProtectionProvider.Create("ZayraTests")), NullLogger<AuthService>.Instance);
        var login = await auth.LoginAsync(new LoginRequest(email, password, w.Slug), new RequestContext("127.0.0.1", "tests"), default);
        login.Tokens.Should().NotBeNull("the login is active and has a password");
        var token = new JwtSecurityTokenHandler().ReadJwtToken(login.Tokens!.AccessToken);
        return new ClaimsPrincipal(new ClaimsIdentity(token.Claims, "Bearer"));
    }

    /// <summary>GET /api/ess/dashboard as <paramref name="principal"/>, through a DbContext carrying that principal's company scope.</summary>
    private async Task<IActionResult?> EssDashboardAsync(ClaimsPrincipal principal)
    {
        var http = new DefaultHttpContext { User = principal };
        await using var db = _fixture.CreateDbWithAccessor(new HttpContextAccessor { HttpContext = http });
        var letters = new StubLetters();
        var storage = new UnusedStorage();
        var controller = new EmployeeSelfServiceController(
            db, letters, new PdfRenderGate(1), new LeaveService(db, new ApprovalRouter(db)),
            new AttendanceService(db, TestNotifications.For(db), new UnusedHttpClientFactory()),
            new HrLetterIssuer(db, letters, storage), storage)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
        return (await controller.Dashboard(default)).Result;
    }

    private async Task<World> SeedAsync()
    {
        await using var db = _fixture.CreateRetryingDb();
        var tenantId = Guid.NewGuid();
        var slug = $"link-{tenantId:N}";
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Employee Link Tenant", Slug = slug, IsActive = true });
        var permissions = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var key in AllKeys)
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

        void AddRole(string name, bool isSystem, bool isEditable, params string[] keys)
        {
            var role = new Role
            {
                Id = Guid.NewGuid(), TenantId = tenantId, Name = name, NormalizedName = AuthService.Normalize(name),
                Description = name, IsSystem = isSystem, IsEditable = isEditable, IsActive = true, AuthorityLevel = 10,
            };
            db.Roles.Add(role);
            foreach (var key in keys) db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permissions[key] });
        }

        AddRole("Admin", isSystem: true, isEditable: false, AllKeys);
        AddRole("Console Admin", isSystem: false, isEditable: true, "security.manage", "employees.read", "profile.read");
        AddRole("Payroll Manager", isSystem: true, isEditable: true, "employees.read", "payroll.approve");
        AddRole("Employee", isSystem: true, isEditable: true, "profile.read", "ess.read", "ess.write", "loans.self");
        AddRole("Reporting", isSystem: false, isEditable: true, "employees.read");
        var companyA = new Company { TenantId = tenantId, LegalNameEn = "Link Company A", TradeName = "A", CountryCode = "AE", Jurisdiction = "AE" };
        var companyB = new Company { TenantId = tenantId, LegalNameEn = "Link Company B", TradeName = "B", CountryCode = "AE", Jurisdiction = "AE" };
        db.Companies.AddRange(companyA, companyB);
        await db.SaveChangesAsync();

        var w = new World(tenantId, slug, companyA.Id, companyB.Id, Guid.Empty, Guid.Empty);
        var adminId = await AddUserAsync(w, Email("admin"), ["Admin"], groupScope: true);
        var consoleId = await AddUserAsync(w, Email("console"), ["Console Admin"], groupScope: true);
        return w with { AdminId = adminId, ConsoleId = consoleId };
    }

    private async Task<Guid> AddUserAsync(World w, string email, string[] roles, bool groupScope, string? password = null, Guid? grantCompany = null)
    {
        await using var db = _fixture.CreateRetryingDb();
        var id = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = id, TenantId = w.TenantId, Email = email, NormalizedEmail = AuthService.Normalize(email), FullName = "Noah Williams",
            PasswordHash = password is null ? "test-only-hash" : new Pbkdf2PasswordHasher().Hash(password),
            Status = "Active", AccessMode = AccessModes.FullPortal, IsActive = true, IsEmailConfirmed = true, IsGroupScope = groupScope,
            IdentityProvider = "Local", ProvisioningSource = "Local",
        });
        foreach (var name in roles)
        {
            var role = await db.Roles.IgnoreQueryFilters().SingleAsync(x => x.TenantId == w.TenantId && x.NormalizedName == AuthService.Normalize(name));
            db.UserRoles.Add(new UserRole { UserId = id, RoleId = role.Id });
        }
        if (grantCompany is Guid company)
            db.UserEntityAccesses.Add(new UserEntityAccess
            {
                TenantId = w.TenantId, UserId = id, CompanyId = company, GrantMode = EntityGrantModes.SelectedCompanies, Role = "HR", IsActive = true,
            });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<int> AddEmployeeAsync(World w, Guid companyId, string workEmail, string? personalEmail = null,
        string fullName = "Noah Williams", string? code = null)
    {
        await using var db = _fixture.CreateRetryingDb();
        var employee = new Employee
        {
            TenantId = w.TenantId,
            CompanyId = companyId,
            EmployeeCode = code ?? $"EMP-{Guid.NewGuid():N}"[..20],
            FullName = fullName,
            WorkEmail = workEmail,
            PersonalEmail = personalEmail ?? string.Empty,
            Status = EmployeeStatuses.Active,
            JoiningDate = DateTime.UtcNow.AddDays(-30),
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return employee.Id;
    }

    private async Task<Guid> AddLinkAsync(World w, int employeeId, Guid userId, bool deleted = false, bool keepPointer = false)
    {
        await using var db = _fixture.CreateRetryingDb();
        var link = new EmployeeUserAccount
        {
            TenantId = w.TenantId, EmployeeId = employeeId, UserId = userId, AccessMode = AccessModes.EssOnly,
            Status = "Active", RequiresPasswordSetup = false, IsPrimary = true,
            IsDeleted = deleted, DeletedAtUtc = deleted ? DateTime.UtcNow : null,
        };
        db.EmployeeUserAccounts.Add(link);
        if (!deleted || keepPointer)
        {
            var employee = await db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == employeeId);
            employee.UserAccountId = userId;
        }
        await db.SaveChangesAsync();
        return link.Id;
    }

    private async Task UpdateEmployeeAsync(int employeeId, Action<Employee> change)
    {
        await using var db = _fixture.CreateRetryingDb();
        change(await db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == employeeId));
        await db.SaveChangesAsync();
    }

    private async Task<Guid> AddRefreshTokenAsync(Guid userId)
    {
        await using var db = _fixture.CreateRetryingDb();
        var token = new RefreshToken
        {
            UserId = userId,
            TokenHash = Guid.NewGuid().ToString("N"),
            ExpiresAtUtc = DateTime.UtcNow.AddDays(7),
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.RefreshTokens.Add(token);
        await db.SaveChangesAsync();
        return token.Id;
    }

    private async Task<DateTime> StampAsync(Guid userId)
    {
        await using var db = _fixture.CreateRetryingDb();
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == userId);
        return user.UpdatedAtUtc ?? user.CreatedAtUtc;
    }

    private async Task<string> EmailOfAsync(Guid userId)
    {
        await using var db = _fixture.CreateRetryingDb();
        return (await db.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == userId)).Email;
    }

    private async Task<string[]> RoleNamesAsync(Guid userId)
    {
        await using var db = _fixture.CreateRetryingDb();
        return await db.UserRoles.AsNoTracking()
            .Where(x => x.UserId == userId && x.Role != null)
            .Select(x => x.Role!.Name)
            .OrderBy(x => x)
            .ToArrayAsync();
    }

    private sealed class StubLetters : ILetterService
    {
        public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class UnusedStorage : IDocumentStorage
    {
        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) => throw new NotSupportedException();
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => throw new NotSupportedException();
        public string ResolvePath(string storageUrl) => throw new NotSupportedException();
    }

    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException();
    }
}

/// <summary>
/// The two endpoints through the REAL Program.cs pipeline: the class-level [HasPermission("security.manage")]
/// on AccessController is what stops an authenticated user without it. A hand-built controller never runs it.
/// </summary>
[Collection("AuthorizationPipeline")]
public sealed class EmployeeLoginLinkHttpTests
{
    private readonly AuthorizationPipelineFixture _fixture;
    public EmployeeLoginLinkHttpTests(AuthorizationPipelineFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task WithoutSecurityManage_BothEndpointsAreForbidden_AndAnonymousIsUnauthorized()
    {
        async Task<HttpStatusCode> SendAsync(HttpMethod method, string path, string? bearer, object? body = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await _fixture.Client.SendAsync(request);
            return response.StatusCode;
        }

        var link = new { employeeId = 1, userId = Guid.NewGuid(), reason = "probe" };
        (await SendAsync(HttpMethod.Get, "/api/access/employee-logins/1", null)).Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Post, "/api/access/employee-logins/link-existing", null, link)).Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Get, "/api/access/employee-logins/1", _fixture.TenantTokenWithoutPermission)).Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, "/api/access/employee-logins/link-existing", _fixture.TenantTokenWithoutPermission, link))
            .Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, "/api/access/employee-logins/link-existing", _fixture.TenantTokenWithPermission, link))
            .Should().Be(HttpStatusCode.Forbidden, "notifications.manage is not security.manage");
    }
}

/// <summary>
/// RATCHET. Self-Service used to tell an unlinked employee to ask HR for "User Management → Invite Employee", a
/// screen that never existed. The way in is User Management → the user → Link to employee record.
/// </summary>
public sealed class EmployeeLinkGuidanceRatchetTests
{
    private static readonly string[] Retired = ["Invite Employee flow", "User Management → Invite Employee", "User Management -> Invite Employee"];

    [Fact]
    public void NoBackendOrFrontendString_PointsAtTheNonexistentInviteEmployeeScreen()
    {
        var root = RepoRoot();
        var scanned = new[] { Path.Combine(root, "backend-dotnet", "Zayra.Api"), Path.Combine(root, "frontend", "src"), Path.Combine(root, "frontend", "app"), Path.Combine(root, "mobile", "src") }
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".cs") || f.EndsWith(".ts") || f.EndsWith(".tsx") || f.EndsWith(".json"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}"))
            .ToList();
        scanned.Should().Contain(f => f.EndsWith("EmployeeSelfServiceController.cs"), "the scan must actually reach the backend source");

        var offenders = scanned
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, line, i)))
            .Where(x => Retired.Any(r => x.line.Contains(r, StringComparison.Ordinal)))
            .Select(x => $"{Path.GetRelativePath(root, x.f)}:{x.i + 1}")
            .ToList();
        offenders.Should().BeEmpty("there is no Invite Employee screen; point people at User Management → the user → Link to employee record");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "backend-dotnet", "Zayra.Api")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException($"Could not locate the repository root from {AppContext.BaseDirectory}.");
    }
}
